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
    private FileSystemWatcher? _metaWatcher;
    private CancellationTokenSource? _metaWatcherDebounce;
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
    private long _lastHeartbeatErrorLogTick = long.MinValue;

    private readonly BridgeSettings _settings;

    // Heartbeat tolerance: a single failed heartbeat (system resume, backend
    // mid-write of state.json, antivirus disk scan) must NOT tear the bridge
    // down and flash the component red. Only after this many consecutive
    // failures do we degrade; before that we retry in place with backoff.
    // Budget check: 3 failures at the 5s cadence plus 1s/2s backoff ≈ 18s,
    // still under the backend's 20s bridge-heartbeat TTL.
    private const int HeartbeatFailureThreshold = 3;
    private static readonly TimeSpan[] HeartbeatRetryBackoff =
    {
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
    };

    private int _consecutiveHeartbeatFailures;
    private int _heartbeatRetryInFlight;
    private bool _heartbeatDegraded;
    private int _staleReconnectInFlight;

    public bool IsRunning => _isRunning;
    public StandaloneMeta? CurrentMeta => _lastMeta;

    public event EventHandler<IpcBridgeState>? BridgeStateRequested;

    public HealthMonitorService(IBridgePaths paths, IIpcBridgeService bridge, BridgeSettings settings)
    {
        _paths = paths;
        _bridge = bridge;
        _settings = settings;
    }

    private int IntervalMs => Math.Clamp(_settings.HealthCheckIntervalMs, 1000, 60000);

    public void Start()
    {
        lock (_lock)
        {
            if (_isRunning) return;
            _isRunning = true;

            Diagnostics.Info("HealthMonitor", "Health monitor started.", new { intervalMs = IntervalMs });

            // 定时器只负责心跳节拍 + meta 兜底复查；meta 的存在性/变更交给
            // FileSystemWatcher 即时感知，故节拍可比纯轮询时代放宽。
            _timer = new System.Timers.Timer(IntervalMs)
            {
                AutoReset = true,
                Enabled = true
            };
            _timer.Elapsed += OnTimerElapsed;

            StartMetaWatcher();

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

            StopMetaWatcher();
            _metaWatcherDebounce?.Dispose();
            _metaWatcherDebounce = null;

            Diagnostics.Info("HealthMonitor", "Health monitor stopped.");
        }
    }

    /// <summary>
    /// meta 文件观察：新实例/重启会重写 meta（Changed/Created/Renamed），
    /// 客户端退出会删除（Deleted）。即时感知后走 200ms 去抖再复查，
    /// 免去 5s 一次的无谓 stat + GetProcessById。
    /// </summary>
    private void StartMetaWatcher()
    {
        try
        {
            var directory = Path.GetDirectoryName(_paths.MetaFilePath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            Directory.CreateDirectory(directory);
            _metaWatcher = new FileSystemWatcher(directory, Path.GetFileName(_paths.MetaFilePath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
                    | NotifyFilters.CreationTime | NotifyFilters.Size,
            };
            _metaWatcher.Changed += OnMetaFileChanged;
            _metaWatcher.Created += OnMetaFileChanged;
            _metaWatcher.Deleted += OnMetaFileChanged;
            _metaWatcher.Renamed += OnMetaFileChanged;
            _metaWatcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            // 观察失败仅退化为纯定时器节拍，不影响功能。
            Diagnostics.Warn("HealthMonitor", $"Meta file watcher unavailable: {ex.Message}");
        }
    }

    private void StopMetaWatcher()
    {
        if (_metaWatcher == null)
        {
            return;
        }

        try
        {
            _metaWatcher.EnableRaisingEvents = false;
            _metaWatcher.Changed -= OnMetaFileChanged;
            _metaWatcher.Created -= OnMetaFileChanged;
            _metaWatcher.Deleted -= OnMetaFileChanged;
            _metaWatcher.Renamed -= OnMetaFileChanged;
            _metaWatcher.Dispose();
        }
        catch
        {
        }
        finally
        {
            _metaWatcher = null;
        }
    }

    private void OnMetaFileChanged(object? sender, FileSystemEventArgs e)
    {
        ScheduleMetaRecheck();
    }

    private void ScheduleMetaRecheck()
    {
        // 去抖：meta 写入可能触发多条事件（temp 写 + rename），合并为一次复查。
        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _metaWatcherDebounce, cts);
        previous?.Cancel();

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(200, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            CheckMetaFile();
        });
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
            // 报 Disconnected（而非 Error）让组件走"离线但有缓存"的灰字分支；
            // 若从未拉到过数据，组件仍会因无缓存而显示红色"未连接"。
            BridgeStateRequested?.Invoke(this, IpcBridgeState.Disconnected);
        }
        else if (currentState == IpcBridgeState.Initial || currentState == IpcBridgeState.Checking)
        {
            BridgeStateRequested?.Invoke(this, IpcBridgeState.NotInstalled);
        }
        else
        {
            // 已处于 Error/Disconnected 等终态：meta 消失说明软件已退出，
            // 收敛到 NotInstalled，避免组件停在陈旧的错误文案上。
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

        // 前几次立即拆连并暴露错误态（用户能马上看到"未连接/错误"）。
        if (_staleMetaStreak <= StaleMetaDetectionLimit)
        {
            _bridge.Disconnect();
            BridgeStateRequested?.Invoke(this, IpcBridgeState.Error);
            return;
        }

        // 超过阈值仍指向同一份未变更的 meta：不再无限重连（PID 复用场景下会
        // 打成"注定失败"的请求风暴），但也**不永久停连**——按指数退避重试，
        // 上限 5min。新实例重写 meta 会由签名变化自动重置节奏。
        ScheduleStaleReconnectBackoff();
    }

    private void ScheduleStaleReconnectBackoff()
    {
        if (Interlocked.CompareExchange(ref _staleReconnectInFlight, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var exponent = Math.Min(_staleMetaStreak - StaleMetaDetectionLimit, 4);
                var delaySeconds = Math.Min(300, 5 * Math.Pow(2, exponent - 1));
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds)).ConfigureAwait(false);

                // meta 已被重写（签名变化）时无需在此重连：下一次检查会正常走
                // Checking → Connect。这里只处理"仍是同一份坏 meta"的兜底重试。
                if (_bridge.State is IpcBridgeState.Error or IpcBridgeState.Disconnected)
                {
                    _ = ConnectBridgeAsync();
                }
            }
            finally
            {
                Interlocked.Exchange(ref _staleReconnectInFlight, 0);
            }
        });
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

        // 自动连接关闭时只维持已建立的连接（心跳），不再主动发起新连接；
        // 用户仍可在设置页手动“重新连接”。
        var autoConnectAllowed = _settings.AutoConnect ||
                                 currentState is IpcBridgeState.Connected;

        switch (currentState)
        {
            case IpcBridgeState.Initial:
            case IpcBridgeState.Disconnected:
                if (!autoConnectAllowed)
                {
                    return;
                }
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
                if (!autoConnectAllowed)
                {
                    return;
                }
                // 独立软件已安装，重新检测
                BridgeStateRequested?.Invoke(this, IpcBridgeState.Checking);
                _ = ConnectBridgeAsync();
                break;

            case IpcBridgeState.Error:
                // 出错后重试（同一份 meta 反复失败时按上限节流）
                if (_connectFailureCount < StaleMetaDetectionLimit && autoConnectAllowed)
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
            OnHeartbeatSucceeded();
        }
        catch (Exception ex)
        {
            OnHeartbeatFailed(ex);
        }
    }

    private void OnHeartbeatSucceeded()
    {
        _consecutiveHeartbeatFailures = 0;

        if (_heartbeatDegraded)
        {
            // Recovered: clear the red state and rebuild the connection/SSE.
            _heartbeatDegraded = false;
            Diagnostics.Info("HealthMonitor", "Bridge heartbeat recovered; clearing degraded state.");
            BridgeStateRequested?.Invoke(this, IpcBridgeState.Checking);
            _ = ConnectBridgeAsync();
        }

        ResetConnectFailures();
    }

    private void OnHeartbeatFailed(Exception ex)
    {
        _consecutiveHeartbeatFailures++;

        // Connection-tearing failures during a genuine outage every tick: keep
        // the 30s log dedup so a fault window doesn't flood the log.
        var now = Environment.TickCount64;
        if (now - _lastHeartbeatErrorLogTick >= 30_000)
        {
            _lastHeartbeatErrorLogTick = now;
            Diagnostics.Error(
                "HealthMonitor",
                $"Bridge heartbeat failed (consecutive {_consecutiveHeartbeatFailures}/{HeartbeatFailureThreshold}).",
                ex);
        }

        if (_consecutiveHeartbeatFailures < HeartbeatFailureThreshold)
        {
            // Under threshold: retry in place, no disconnect, no red.
            ScheduleHeartbeatRetry();
            return;
        }

        // Threshold reached: this is a real outage. Degrade once.
        if (!_heartbeatDegraded)
        {
            _heartbeatDegraded = true;
            Diagnostics.Warn("HealthMonitor", "Bridge heartbeat degraded after repeated failures.");
            _bridge.Disconnect();
            BridgeStateRequested?.Invoke(this, IpcBridgeState.Error);
            NoteConnectFailure();
        }

        if (_staleMetaStreak < StaleMetaDetectionLimit && _connectFailureCount < StaleMetaDetectionLimit)
        {
            _ = ConnectBridgeAsync();
        }
    }

    private void ScheduleHeartbeatRetry()
    {
        // Single-flight: at most one retry loop is in flight; the timer tick
        // may fire again before the backoff completes.
        if (Interlocked.CompareExchange(ref _heartbeatRetryInFlight, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                for (var attempt = 0; attempt < HeartbeatRetryBackoff.Length; attempt++)
                {
                    if (_bridge.State != IpcBridgeState.Connected)
                    {
                        return;
                    }

                    await Task.Delay(HeartbeatRetryBackoff[attempt]).ConfigureAwait(false);

                    try
                    {
                        await _bridge.SendHeartbeatAsync().ConfigureAwait(false);
                        OnHeartbeatSucceeded();
                        return;
                    }
                    catch (Exception ex)
                    {
                        _consecutiveHeartbeatFailures++;
                        if (_consecutiveHeartbeatFailures >= HeartbeatFailureThreshold)
                        {
                            OnHeartbeatFailed(ex);
                            return;
                        }
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _heartbeatRetryInFlight, 0);
            }
        });
    }

    public void Dispose()
    {
        Stop();
    }
}
