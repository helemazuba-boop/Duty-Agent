using System.IO;
using System.Text.Json;
using System.Timers;
using DutyAgentBridge.Models;

namespace DutyAgentBridge.Services;

/// <summary>
/// 独立软件健康监控服务
/// 通过文件变化检测独立软件状态，无需轮询 HTTP
/// </summary>
public interface IHealthMonitorService : IDisposable
{
    void Start();
    void Stop();
    bool IsRunning { get; }
    StandaloneMeta? CurrentMeta { get; }

    event EventHandler<IpcBridgeState>? BridgeStateRequested;
}

public sealed class HealthMonitorService : IHealthMonitorService
{
    private readonly IBridgePaths _paths;
    private readonly IIpcBridgeService _bridge;

    private System.Timers.Timer? _timer;
    private DateTime _lastMetaWriteTime = DateTime.MinValue;
    private StandaloneMeta? _lastMeta;
    private bool _isRunning;
    private readonly object _lock = new();

    private const int DefaultIntervalMs = 5000;

    public bool IsRunning => _isRunning;
    public StandaloneMeta? CurrentMeta => _lastMeta;

    public event EventHandler<IpcBridgeState>? BridgeStateRequested;

    public HealthMonitorService(IBridgePaths paths, IIpcBridgeService bridge)
    {
        _paths = paths;
        _bridge = bridge;
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_isRunning) return;
            _isRunning = true;

            Diagnostics.Info("HealthMonitor", "Health monitor started.", new { intervalMs = DefaultIntervalMs });

            _timer = new System.Timers.Timer(DefaultIntervalMs)
            {
                AutoReset = true,
                Enabled = true
            };
            _timer.Elapsed += OnTimerElapsed;

            // 立即执行一次
            CheckMetaFile();
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (!_isRunning) return;
            _isRunning = false;

            if (_timer != null)
            {
                _timer.Stop();
                _timer.Elapsed -= OnTimerElapsed;
                _timer.Dispose();
                _timer = null;
            }

            Diagnostics.Info("HealthMonitor", "Health monitor stopped.");
        }
    }

    private void OnTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        CheckMetaFile();
    }

    private void CheckMetaFile()
    {
        try
        {
            var metaPath = _paths.MetaFilePath;

            if (!File.Exists(metaPath))
            {
                HandleMissingMeta();
                return;
            }

            // 检查文件写入时间（通过变化检测独立软件是否重启）
            var writeTime = File.GetLastWriteTime(metaPath);
            if (writeTime == _lastMetaWriteTime && _lastMeta != null)
            {
                // 文件未变化，检查进程是否存活
                if (_lastMeta.ProcessId > 0 && !IsProcessAlive(_lastMeta.ProcessId))
                {
                    HandleProcessDied();
                }
                return;
            }

            _lastMetaWriteTime = writeTime;

            // 读取并解析元数据
            var json = File.ReadAllText(metaPath);
            var meta = JsonSerializer.Deserialize<StandaloneMeta>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (meta == null || meta.ProcessId <= 0)
            {
                HandleMissingMeta();
                return;
            }

            // 检查进程是否存活
            if (!IsProcessAlive(meta.ProcessId))
            {
                HandleProcessDied();
                return;
            }

            _lastMeta = meta;

            // 根据当前状态和元数据决定下一步动作
            OnMetaLoaded(meta);
        }
        catch (Exception ex)
        {
            Diagnostics.Error("HealthMonitor", "Failed to check meta file.", ex);
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private void HandleMissingMeta()
    {
        var currentState = _bridge.State;
        if (currentState == IpcBridgeState.Connected || currentState == IpcBridgeState.Connecting)
        {
            Diagnostics.Warn("HealthMonitor", "Meta file disappeared, requesting disconnect.");
            BridgeStateRequested?.Invoke(this, IpcBridgeState.Disconnected);
        }
        else if (currentState == IpcBridgeState.Initial || currentState == IpcBridgeState.Checking)
        {
            BridgeStateRequested?.Invoke(this, IpcBridgeState.NotInstalled);
        }
    }

    private void HandleProcessDied()
    {
        Diagnostics.Warn("HealthMonitor", "Standalone process died.", new { pid = _lastMeta?.ProcessId });
        BridgeStateRequested?.Invoke(this, IpcBridgeState.Error);
    }

    private void OnMetaLoaded(StandaloneMeta meta)
    {
        var currentState = _bridge.State;

        switch (currentState)
        {
            case IpcBridgeState.Initial:
            case IpcBridgeState.Disconnected:
                // 独立软件已启动，尝试连接
                BridgeStateRequested?.Invoke(this, IpcBridgeState.Checking);
                _ = ConnectBridgeAsync();
                break;

            case IpcBridgeState.Checking:
            case IpcBridgeState.Connecting:
                // 正在连接中，无需额外操作
                break;

            case IpcBridgeState.Connected:
                // 已连接，检查端口是否变化（独立软件重启后可能获得新端口）
                if (meta.Port > 0 && meta.Port != _bridge.CurrentMeta?.Port)
                {
                    Diagnostics.Info("HealthMonitor", "Port changed, reconnecting.", new
                    {
                        oldPort = _bridge.CurrentMeta?.Port,
                        newPort = meta.Port
                    });
                    _ = ConnectBridgeAsync();
                }
                break;

            case IpcBridgeState.NotInstalled:
                // 独立软件已安装，重新检测
                BridgeStateRequested?.Invoke(this, IpcBridgeState.Checking);
                _ = ConnectBridgeAsync();
                break;

            case IpcBridgeState.Error:
                // 出错后重试
                BridgeStateRequested?.Invoke(this, IpcBridgeState.Checking);
                _ = ConnectBridgeAsync();
                break;
        }
    }

    private async Task ConnectBridgeAsync()
    {
        try
        {
            await _bridge.ConnectAsync();
        }
        catch (Exception ex)
        {
            Diagnostics.Error("HealthMonitor", "Failed to connect bridge.", ex);
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
