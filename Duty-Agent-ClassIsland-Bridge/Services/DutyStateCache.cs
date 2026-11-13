using System.Timers;
using DutyAgentBridge.Models;

namespace DutyAgentBridge.Services;

/// <summary>
/// 排班状态缓存：把"组件/规则各自轮询快照"改为"缓存统一接收推送"。
///
/// 数据来源优先级：
/// 1. 桥接变为 Connected 时的全量拉取；
/// 2. 后端推送的 snapshot_changed / schedule_completed / schedule_failed
///    （500ms 防抖合并，避免短时间多条推送引发请求风暴）；
/// 3. 60s 低频安全轮询（仅在已连接时执行，兜底静默丢推送的极端情况）。
///
/// 消费方（组件渲染、自动化规则评估）全部读内存，不再各自发 HTTP。
/// "今天"只认设备本地日历日期（DateTime.Now），与 Python 后端、Web UI 同源；
/// 不用 ClassIsland 的 IExactTimeService，因为它带用户可调的时间偏移，
/// 会让组件在跨午夜时和另外两端落在不同的日期。
/// </summary>
public sealed class DutyStateCache : IDisposable
{
    private readonly IIpcBridgeService _bridge;
    private readonly object _gate = new();

    private DutyState? _state;
    private DateTimeOffset _lastRefreshUtc = DateTimeOffset.MinValue;
    private CancellationTokenSource? _debounceCts;
    private System.Timers.Timer? _safetyPollTimer;
    private int _refreshInFlight;

    /// <summary>缓存以新快照完成刷新后触发（任意线程）。</summary>
    public event EventHandler? SnapshotUpdated;

    public DutyStateCache(IIpcBridgeService bridge)
    {
        _bridge = bridge;

        _bridge.NotificationReceived += OnNotificationReceived;
        _bridge.StateChanged += OnStateChanged;

        _safetyPollTimer = new System.Timers.Timer(60_000)
        {
            AutoReset = true,
            Enabled = true
        };
        _safetyPollTimer.Elapsed += OnSafetyPoll;
    }

    /// <summary>当前缓存的排班状态（可能为 null：尚未完成首次拉取）。</summary>
    public DutyState? State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public DateTimeOffset LastRefreshUtc
    {
        get
        {
            lock (_gate)
            {
                return _lastRefreshUtc;
            }
        }
    }

    /// <summary>
    /// 缓存是否已过期待刷新：从未刷新过，或距上次成功刷新超过阈值。
    /// 用于组件在断连/长时间未更新时显示"离线数据"灰字而不是直接判死。
    /// </summary>
    public bool IsStale
    {
        get
        {
            lock (_gate)
            {
                if (_lastRefreshUtc == DateTimeOffset.MinValue)
                {
                    return true;
                }

                return DateTimeOffset.UtcNow - _lastRefreshUtc > StaleThreshold;
            }
        }
    }

    private static TimeSpan StaleThreshold => TimeSpan.FromSeconds(45);

    // 上一次见到的 state.json mtime（st_mtime_ns），安全轮询用它跳过无谓的全量拉取。
    private long? _lastStateMtimeNs;

    /// <summary>
    /// "今天" = 设备本地日历日期，每次访问现算，不缓存。
    ///
    /// 旧实现把后端快照里的 current_duty_date 缓存起来（该值越过
    /// component_refresh_time 后被 +1 天，且只在 state.json 变动时才重拉），
    /// 回退路径又自己做一次 +1。结果组件与主页在一天里的大部分时间都显示
    /// "明天"的值日，跳变时机还取决于快照何时刷新 —— 即"有概率认错时间"。
    /// 排班池本身一直是对的，错的只是这个被凭空推算出来的显示日期。
    /// </summary>
    public DateTime Today => DateTime.Now.Date;

    public SchedulePoolItem? GetTodayItem()
    {
        var today = Today.ToString("yyyy-MM-dd");
        var state = State;
        return state?.SchedulePool.FirstOrDefault(item =>
            string.Equals(item.Date, today, StringComparison.Ordinal));
    }

    /// <summary>立即刷新一次（连接建立、组件冷启动时调用）。</summary>
    public Task RefreshAsync(string reason)
    {
        return RefreshCoreAsync(reason);
    }

    private void OnStateChanged(object? sender, IpcBridgeState state)
    {
        if (state == IpcBridgeState.Connected)
        {
            _ = RefreshCoreAsync("connected");
        }
    }

    private void OnNotificationReceived(object? sender, DutyNotificationEvent notification)
    {
        if (notification.Type is "snapshot_changed" or "schedule_completed" or "schedule_failed")
        {
            ScheduleDebouncedRefresh();
        }
    }

    private void OnSafetyPoll(object? sender, ElapsedEventArgs e)
    {
        if (_bridge.State == IpcBridgeState.Connected)
        {
            _ = SafetyPollAsync();
        }
    }

    /// <summary>
    /// 60s 安全轮询：先打轻量的 /api/v1/state 比对 mtime_ns，只有确实变了
    /// 才拉全量 snapshot。稳态下把每次全量（含 roster + config）降为一次轻量请求。
    /// </summary>
    private async Task SafetyPollAsync()
    {
        // 单飞行闸门与 RefreshCoreAsync 共用：慢请求期间不叠加。
        if (Interlocked.CompareExchange(ref _refreshInFlight, 1, 0) != 0)
        {
            return;
        }

        try
        {
            if (_bridge.State != IpcBridgeState.Connected)
            {
                return;
            }

            long? remoteMtime;
            try
            {
                var light = await _bridge.GetStateAsync();
                remoteMtime = light.MtimeNs;
            }
            catch (Exception ex)
            {
                Diagnostics.Log("DutyStateCache", $"State poll failed: {ex.Message}", "WARN");
                return;
            }

            if (remoteMtime.HasValue && remoteMtime.Value == _lastStateMtimeNs)
            {
                // 文件没变：刷新时间戳即可，省掉一次全量 snapshot。
                lock (_gate)
                {
                    _lastRefreshUtc = DateTimeOffset.UtcNow;
                }
                return;
            }

            _lastStateMtimeNs = remoteMtime;
        }
        finally
        {
            Interlocked.Exchange(ref _refreshInFlight, 0);
        }

        // mtime 变化（或未知）：拉一次全量以刷新 config/roster/日期。
        await RefreshCoreAsync("safety-poll");
    }

    private void ScheduleDebouncedRefresh()
    {
        CancellationTokenSource? previousCts;
        lock (_gate)
        {
            previousCts = _debounceCts;
            _debounceCts = new CancellationTokenSource();
        }

        var token = _debounceCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                // 500ms 合并窗口：短时间多条推送只触发一次拉取。
                previousCts?.Cancel();
                await Task.Delay(500, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            await RefreshCoreAsync("push");
        });
    }

    private async Task RefreshCoreAsync(string reason)
    {
        // 单飞行闸门：防抖 + 安全轮询 + 连接事件可能同时到达。
        if (Interlocked.CompareExchange(ref _refreshInFlight, 1, 0) != 0)
        {
            return;
        }

        try
        {
            if (_bridge.State != IpcBridgeState.Connected)
            {
                return;
            }

            var snapshot = await _bridge.GetSnapshotAsync();
            lock (_gate)
            {
                _state = snapshot.State;
                // 不再缓存 current_duty_date / component_refresh_time：
                // "今天"由本机时钟现算，缓存只会让它在跨天后变成陈旧的日期。
                _lastRefreshUtc = DateTimeOffset.UtcNow;
            }
            Diagnostics.Log("DutyStateCache", $"Snapshot refreshed ({reason}).", "DEBUG",
                new { scheduleCount = snapshot.State.SchedulePool.Count });
            SnapshotUpdated?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Diagnostics.Log("DutyStateCache", $"Snapshot refresh failed ({reason}): {ex.Message}", "WARN");
        }
        finally
        {
            Interlocked.Exchange(ref _refreshInFlight, 0);
        }
    }

    public void Dispose()
    {
        _bridge.NotificationReceived -= OnNotificationReceived;
        _bridge.StateChanged -= OnStateChanged;
        _safetyPollTimer?.Dispose();
        _debounceCts?.Dispose();
    }
}
