using System.IO;
using System.Text.Json;
using System.Timers;
using DutyAgentBridge.Models;

namespace DutyAgentBridge.Services;

/// <summary>
/// 独立软件健康监控服务。
/// 每 5s 轮询一次 meta 文件（stat + 按需解析）判断独立软件状态；已连接时同一节拍
/// 发送 HTTP 心跳维持后端的 bridge 在线判定（TTL 见后端 runtime）。
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
    private int _checkInFlight;
    private int _connectInFlight;

    // Orphan-meta backoff: a crashed standalone app can leave a meta file whose
    // PID is dead (or recycled). Without this guard we disconnect/raise-error
    // (and under PID reuse, attempt doomed reconnects) every tick forever.
    // After this many detections against an UNCHANGED meta file we pause until
    // the file itself changes (new instance rewrites it) or disappears.
    private const int StaleMetaDetectionLimit = 3;
    private long _staleMetaSignature;
    private int _staleMetaStreak;

    // Throttle doomed reconnect attempts while the meta file is unchanged
    // (covers PID-recycled cases where the process looks alive but the port
    // is dead). A rewritten meta file resets the throttle automatically.
    private long _connectFailureSignature;
    private int _connectFailureCount;

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
        // 重入闸门：System.Timers.Timer(AutoReset) 在线程池上触发，慢磁盘/杀软扫描
        // 卡住一个 tick 时，下一个 tick 会并发进入：_lastMeta/_lastMetaWriteTime
        // 被多线程读写，且可能同时发起多个连接。进行中则直接跳过本节拍。
        if (Interlocked.CompareExchange(ref _checkInFlight, 1, 0) != 0)
        {
            return;
        }

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
                else
                {
                    OnMetaLoaded(_lastMeta);
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

            // 进程恢复存活：清除孤儿-meta 计数，恢复正常重连节奏。
            _staleMetaSignature = 0;
            _staleMetaStreak = 0;
            if (_lastMeta != null && _lastMeta.ProcessId == meta.ProcessId)
            {
                // Same PID alive again — keep failure counters; they reset on
                // a successful heartbeat. A new PID means a fresh instance.
                _lastMeta = meta;
            }
            else
            {
                ResetConnectFailures();
                _lastMeta = meta;
            }

            // 根据当前状态和元数据决定下一步动作
            OnMetaLoaded(meta);
        }
        catch (Exception ex)
        {
            Diagnostics.Error("HealthMonitor", "Failed to check meta file.", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _checkInFlight, 0);
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            // Dispose the probe handle: this runs every heartbeat tick, so
            // leaked Process objects pile up finalizer pressure over weeks.
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private void HandleMissingMeta()
    {
        _staleMetaSignature = 0;
        _staleMetaStreak = 0;
        ResetConnectFailures();
        var currentState = _bridge.State;
        if (currentState == IpcBridgeState.Connected || currentState == IpcBridgeState.Connecting)
        {
            Diagnostics.Warn("HealthMonitor", "Meta file disappeared, requesting disconnect.");
            _bridge.Disconnect();
            BridgeStateRequested?.Invoke(this, IpcBridgeState.Disconnected);
        }
        else if (currentState == IpcBridgeState.Initial || currentState == IpcBridgeState.Checking)
        {
            BridgeStateRequested?.Invoke(this, IpcBridgeState.NotInstalled);
        }
    }

    private void HandleProcessDied()
    {
        var signature = ComputeMetaSignature();
        if (signature == _staleMetaSignature)
        {
            _staleMetaStreak++;
        }
        else
        {
            _staleMetaSignature = signature;
            _staleMetaStreak = 1;
        }

        Diagnostics.Warn("HealthMonitor", "Standalone process died.", new { pid = _lastMeta?.ProcessId, streak = _staleMetaStreak });

        // Only the first detections actively tear down and surface the error.
        // Once the limit is hit against an unchanged meta file, stay quiet —
        // the next instance will rewrite the meta file and reset this state.
        if (_staleMetaStreak <= StaleMetaDetectionLimit)
        {
            _bridge.Disconnect();
            BridgeStateRequested?.Invoke(this, IpcBridgeState.Error);
        }
    }

    private long ComputeMetaSignature()
    {
        try
        {
            var metaPath = _paths.MetaFilePath;
            var info = new FileInfo(metaPath);
            if (!info.Exists)
            {
                return 0;
            }

            unchecked
            {
                return info.LastWriteTimeUtc.Ticks * 397L ^ info.Length ^ (_lastMeta?.ProcessId ?? 0);
            }
        }
        catch
        {
            return 0;
        }
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
                else
                {
                    _ = SendHeartbeatAsync();
                }
                break;

            case IpcBridgeState.NotInstalled:
                // 独立软件已安装，重新检测
                BridgeStateRequested?.Invoke(this, IpcBridgeState.Checking);
                _ = ConnectBridgeAsync();
                break;

            case IpcBridgeState.Error:
                // 出错后重试（同一份 meta 反复失败时按上限节流）
                if (_connectFailureCount < StaleMetaDetectionLimit)
                {
                    BridgeStateRequested?.Invoke(this, IpcBridgeState.Checking);
                    _ = ConnectBridgeAsync();
                }
                break;
        }
    }

    private async Task ConnectBridgeAsync()
    {
        // 连接去抖：多个节拍/状态分支都可能 fire-and-forget 发起连接，
        // 同一时刻只允许一个在飞，避免并行连接风暴。
        if (Interlocked.CompareExchange(ref _connectInFlight, 1, 0) != 0)
        {
            return;
        }

        try
        {
            await _bridge.ConnectAsync();
        }
        catch (Exception ex)
        {
            Diagnostics.Error("HealthMonitor", "Failed to connect bridge.", ex);
            NoteConnectFailure();
        }
        finally
        {
            Interlocked.Exchange(ref _connectInFlight, 0);
        }
    }

    private void NoteConnectFailure()
    {
        var signature = ComputeMetaSignature();
        if (signature == _connectFailureSignature)
        {
            _connectFailureCount++;
        }
        else
        {
            _connectFailureSignature = signature;
            _connectFailureCount = 1;
        }
    }

    private void ResetConnectFailures()
    {
        _connectFailureSignature = 0;
        _connectFailureCount = 0;
    }

    private async Task SendHeartbeatAsync()
    {
        try
        {
            await _bridge.SendHeartbeatAsync();
            ResetConnectFailures();
        }
        catch (Exception ex)
        {
            Diagnostics.Error("HealthMonitor", "Bridge heartbeat failed.", ex);
            _bridge.Disconnect();
            BridgeStateRequested?.Invoke(this, IpcBridgeState.Error);
            NoteConnectFailure();

            // Stale-meta guard: if the meta file has already been flagged as
            // orphaned and has not changed since, do not schedule another
            // doomed reconnect every heartbeat tick.
            if (_staleMetaStreak < StaleMetaDetectionLimit && _connectFailureCount < StaleMetaDetectionLimit)
            {
                _ = ConnectBridgeAsync();
            }
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
