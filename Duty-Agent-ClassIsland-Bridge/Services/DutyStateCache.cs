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
///
/// "过了 HH:MM 就显示次日值日"是独立的展示选择（DisplayDutyDate），不是
/// 对 Today 的改写。规则可以缓存，算出来的日期绝不缓存 —— 每次访问现算，
/// 否则切换时机又会退化成"快照何时刷新"。规则/自动化仍读 GetTodayItem()。
/// </summary>
public sealed class DutyStateCache : IDisposable
{
    private readonly IIpcBridgeService _bridge;
    private readonly object _gate = new();

    private DutyState? _state;
    // 值日预览的"规则"（可缓存），不是算好的日期（绝不可缓存）。
    private TimeSpan _previewBoundary = new(8, 0, 0);
    private bool _previewEnabled = true;
    private DateTimeOffset _lastRefreshUtc = DateTimeOffset.MinValue;
    private CancellationTokenSource? _debounceCts;
    private System.Timers.Timer? _safetyPollTimer;
    private System.Timers.Timer? _boundaryTimer;
    private int _refreshInFlight;

    /// <summary>缓存以新快照完成刷新后触发（任意线程）。</summary>
    public event EventHandler? SnapshotUpdated;

    /// <summary>
    /// 值日展示日期发生翻转（到达 HH:MM 边界，或跨午夜）时触发，
    /// 让组件能精确重绘而不必等下一次快照 / 下一分钟轮询。
    /// </summary>
    public event EventHandler? DisplayDateChanged;

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
        ArmDisplayBoundaryTimer();
    }

    /// <summary>
    /// 精确对准下一次日期翻转（边界或午夜）的一次性定时器。
    /// 旧的"有概率认错时间"根因就是翻转靠快照/缓存到达时机；这里由时钟驱动。
    /// </summary>
    private void ArmDisplayBoundaryTimer()
    {
        _boundaryTimer?.Dispose();
        TimeSpan boundary;
        lock (_gate)
        {
            boundary = _previewBoundary;
        }

        var now = DateTime.Now;
        var nextTransition = new[]
        {
            now.Date.AddDays(1),                     // 跨午夜：标签 明日 -> 今日
            now.Date + boundary,                     // 到达当日边界：切到次日
            now.Date.AddDays(1) + boundary,          // 明日边界
        }.Where(t => t > now).Min();

        _boundaryTimer = new System.Timers.Timer(Math.Max(50, (nextTransition - now).TotalMilliseconds))
        {
            AutoReset = false
        };
        _boundaryTimer.Elapsed += (_, _) =>
        {
            DisplayDateChanged?.Invoke(this, EventArgs.Empty);
            ArmDisplayBoundaryTimer();
        };
        _boundaryTimer.Start();
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
    /// "今天" = 设备本地日历日期，每次访问现算，不缓存。状态写入、规则评估、
    /// 自动化指纹一律用它，任何配置都不能改写。
    /// </summary>
    public DateTime Today => DateTime.Now.Date;

    /// <summary>解析 HH:MM 边界；非法值统一回退 08:00（与 Python/TS 一致）。</summary>
    public static TimeSpan ParsePreviewBoundary(string? value)
    {
        return TimeSpan.TryParse(value, out var parsed)
               && parsed >= TimeSpan.Zero
               && parsed < TimeSpan.FromDays(1)
            ? parsed
            : new TimeSpan(8, 0, 0);
    }

    /// <summary>
    /// 纯规则：展示哪一天的值日。Python / TypeScript 实现同一条公式。
    /// 跨午夜连续：同一日期在 00:00 只把「明日」标签换成「今日」，内容不跳。
    /// </summary>
    public static DateTime ResolveDisplayDutyDate(DateTime now, TimeSpan boundary, bool enabled)
    {
        var target = now.Date;
        return enabled && now.TimeOfDay >= boundary ? target.AddDays(1) : target;
    }

    /// <summary>组件展示用日期，每次访问由本机时钟现算，绝不缓存。</summary>
    public DateTime DisplayDutyDate
    {
        get
        {
            TimeSpan boundary;
            bool enabled;
            lock (_gate)
            {
                boundary = _previewBoundary;
                enabled = _previewEnabled;
            }
            return ResolveDisplayDutyDate(DateTime.Now, boundary, enabled);
        }
    }

    /// <summary>当前展示的是次日值日（组件必须显式标注「明日」）。</summary>
    public bool IsPreview => DisplayDutyDate != Today;

    /// <summary>规则/自动化用：永远按日历今天取值日条目。</summary>
    public SchedulePoolItem? GetTodayItem()
    {
        var today = Today.ToString("yyyy-MM-dd");
        var state = State;
        return state?.SchedulePool.FirstOrDefault(item =>
            string.Equals(item.Date, today, StringComparison.Ordinal));
    }

    /// <summary>组件展示用：按 DisplayDutyDate 取值日条目。</summary>
    public SchedulePoolItem? GetDisplayItem()
    {
        var displayDate = DisplayDutyDate.ToString("yyyy-MM-dd");
        var state = State;
        return state?.SchedulePool.FirstOrDefault(item =>
            string.Equals(item.Date, displayDate, StringComparison.Ordinal));
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
                // 只缓存规则。后端算好的 current_duty_date / display_duty_date
                // 一律不缓存：缓存日期正是旧版"跳变时机看快照脸色"的根源。
                _previewBoundary = ParsePreviewBoundary(snapshot.ComponentRefreshTime);
                _previewEnabled = snapshot.DutyPreviewEnabled;
                _lastRefreshUtc = DateTimeOffset.UtcNow;
            }
            // 规则变更后重新对准下一次翻转（在锁外调用，避免重入）。
            ArmDisplayBoundaryTimer();
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
        _boundaryTimer?.Dispose();
        _debounceCts?.Dispose();
    }
}
