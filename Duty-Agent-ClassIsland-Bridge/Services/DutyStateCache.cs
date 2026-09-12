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

    /// <summary>按精确时间服务取"今天"；未解析到宿主服务时回退本机时钟。</summary>
    public DateTime Today => Now.Date;

    private DateTime Now => _exactTimeService?.GetCurrentLocalDateTime() ?? DateTime.Now;

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
            _ = RefreshCoreAsync("safety-poll");
        }
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
