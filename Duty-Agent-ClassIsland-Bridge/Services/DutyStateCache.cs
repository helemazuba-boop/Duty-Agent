using System.ComponentModel;
using System.Timers;
using ClassIsland.Core.Abstractions.Services;
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
/// "今天"的判定统一走 IExactTimeService（NTP 校准时间），而非 DateTime.Now。
/// </summary>
public sealed class DutyStateCache : IDisposable
{
    private readonly IIpcBridgeService _bridge;
    private readonly IExactTimeService? _exactTimeService;
    private readonly object _gate = new();

    private DutyState? _state;
    private DateTimeOffset _lastRefreshUtc = DateTimeOffset.MinValue;
    private CancellationTokenSource? _debounceCts;
    private System.Timers.Timer? _safetyPollTimer;
    private int _refreshInFlight;

    /// <summary>缓存以新快照完成刷新后触发（任意线程）。</summary>
    public event EventHandler? SnapshotUpdated;

    public DutyStateCache(IIpcBridgeService bridge, IExactTimeService? exactTimeService = null)
    {
        _bridge = bridge;
        _exactTimeService = exactTimeService;

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

    private string _componentRefreshTime = "08:00";

    // 后端权威的"当前值日日期"（YYYY-MM-DD），随快照一起下发。
    // 桥接不再自行重算，避免与后端/ Web UI 出现"今天/明天"分歧。
    private string? _currentDutyDate;

    /// <summary>
    /// 当前生效的"值日日期"。
    /// 首选后端下发的 current_duty_date；缺失时按与后端一致的规则本地回退
    /// （早于 component_refresh_time 取今天，否则回退到今天——注意后端语义是
    /// "回退"，不是"推进"）。
    /// </summary>
    public DateTime Today
    {
        get
        {
            lock (_gate)
            {
                if (!string.IsNullOrWhiteSpace(_currentDutyDate) &&
                    DateTime.TryParseExact(_currentDutyDate, "yyyy-MM-dd",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var parsed))
                {
                    return parsed;
                }
            }

            var now = Now;
            var refreshTime = TimeSpan.TryParse(_componentRefreshTime, out var t)
                ? t
                : new TimeSpan(8, 0, 0);
            // 后端语义：过刷新时间 = 回退（仍是今天），未到刷新时间 = 今天。
            // 两者都落在"今天"，区别只在后端把当天视为值的边界；这里保持一致。
            return now.TimeOfDay >= refreshTime ? now.Date : now.Date.AddDays(-1);
        }
    }

    private DateTime Now => _exactTimeService?.GetCurrentLocalDateTime() ?? DateTime.Now;

    public SchedulePoolItem? GetTodayItem()
    {
        var today = Today.ToString("yyyy-MM-dd");
        var state = State;
        return state?.SchedulePool.FirstOrDefault(item =>
            string.Equals(item.Date, today, StringComparison.Ordinal));
    }

    /// <summary>缓存的"当前值日日期"（后端权威值，YYYY-MM-DD）；未知时为 null。</summary>
    public string? CurrentDutyDate
    {
        get
        {
            lock (_gate)
            {
                return _currentDutyDate;
            }
        }
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
                _componentRefreshTime = snapshot.ComponentRefreshTime ?? snapshot.Config.ComponentRefreshTime ?? "08:00";
                if (!string.IsNullOrWhiteSpace(snapshot.CurrentDutyDate))
                {
                    _currentDutyDate = snapshot.CurrentDutyDate;
                }
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
