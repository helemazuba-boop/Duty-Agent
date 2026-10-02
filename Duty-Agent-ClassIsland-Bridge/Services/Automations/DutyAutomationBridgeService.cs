using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Services;
using DutyAgentBridge.Models;

namespace DutyAgentBridge.Services;

/// <summary>
/// 自动化信号源：把后端的通知流翻译成三个自动化触发器需要的事件
/// （排班执行成功 / 排班执行失败 / 今日值日安排变更）。
///
/// 为什么用通知流而不是本地调用点：排班可能从独立软件界面、内置 Web UI、
/// CLI 或 MCP 发起，只有后端广播的 <c>schedule_completed</c> /
/// <c>schedule_failed</c> / <c>snapshot_changed</c> 能覆盖全部入口。
///
/// 注意：<c>schedule_completed</c> / <c>schedule_failed</c> 由后端
/// <c>runtime.publish_schedule_result_notification</c> 发出，受"排班完成通知"
/// 开关（<c>schedule_completion_notification_enabled</c>，默认开启）约束；
/// 关闭该开关时这两个触发器不会触发，这是与后端约定的边界。
/// </summary>
public sealed class DutyAutomationBridgeService
{
    private readonly IRulesetService? _rulesetService;
    private readonly DutyStateCache _cache;

    // snapshot_changed 只是"数据可能变了"的信号，真正的判定要等缓存重拉完成，
    // 因此先记下待处理标记与原因，在 SnapshotUpdated 里再比对值日指纹。
    private bool _snapshotSignalPending;
    private string _pendingSnapshotReason = "";
    private string? _lastDutyFingerprint;

    public event EventHandler<DutyScheduleRunEvent>? ScheduleRunSucceeded;
    public event EventHandler<DutyScheduleRunEvent>? ScheduleRunFailed;
    public event EventHandler<DutyScheduleStateChangedEvent>? ScheduleStateChanged;

    public DutyScheduleRunEvent? LastRunEvent { get; private set; }
    public DutyScheduleStateChangedEvent? LastStateChangedEvent { get; private set; }

    public DutyAutomationBridgeService(
        IIpcBridgeService bridge,
        DutyStateCache cache,
        IRulesetService? rulesetService = null)
    {
        _cache = cache;
        _rulesetService = rulesetService;

        bridge.NotificationReceived += OnNotificationReceived;
        cache.SnapshotUpdated += OnSnapshotUpdated;
    }

    private void OnNotificationReceived(object? sender, DutyNotificationEvent notification)
    {
        switch (notification.Type)
        {
            case "schedule_completed":
                PublishRunCompleted(success: true, notification);
                break;

            case "schedule_failed":
                PublishRunCompleted(success: false, notification);
                break;

            case "snapshot_changed":
                _snapshotSignalPending = true;
                _pendingSnapshotReason = string.IsNullOrWhiteSpace(notification.Body)
                    ? "snapshot-changed"
                    : notification.Body.Trim();
                break;
        }
    }

    private void PublishRunCompleted(bool success, DutyNotificationEvent notification)
    {
        // 通知契约里没有 instruction / is_auto_run，这里留空而不是猜测；
        // 订阅方目前只关心成败，需要更多上下文时应扩展后端事件负载。
        var runEvent = new DutyScheduleRunEvent(
            DateTimeOffset.Now,
            success,
            notification.Body ?? "");

        LastRunEvent = runEvent;

        if (success)
        {
            ScheduleRunSucceeded?.Invoke(this, runEvent);

            // 一次成功的运行必然改写了排班池：等价于一次"值日安排变更"，
            // 与旧插件在 state 落盘后广播变更的行为保持一致。
            PublishScheduleStateChanged("schedule-run-succeeded");
        }
        else
        {
            ScheduleRunFailed?.Invoke(this, runEvent);
        }

        NotifyRulesetStatusChanged();
    }

    private void OnSnapshotUpdated(object? sender, EventArgs e)
    {
        var fingerprint = BuildDutyFingerprint();
        if (fingerprint == null)
        {
            // 缓存尚未就绪（首帧）：不建立基线也不触发。
            return;
        }

        if (!_snapshotSignalPending)
        {
            // 连接建立 / 60s 安全轮询 / 唤醒重连驱动的刷新：只更新基线。
            _lastDutyFingerprint = fingerprint;
            return;
        }

        _snapshotSignalPending = false;
        var reason = _pendingSnapshotReason;
        _pendingSnapshotReason = "";

        if (string.Equals(_lastDutyFingerprint, fingerprint, StringComparison.Ordinal))
        {
            // 典型场景：只改了模型、BaseUrl 等与当前值日无关的配置。
            // 数据总线信号发出去了，但"今日值日安排"其实没变，不该触发。
            return;
        }

        _lastDutyFingerprint = fingerprint;
        PublishScheduleStateChanged(reason);
    }

    /// <summary>
    /// 当前"值日日期 + 各区域人员"的指纹。今日无排班时用日期占位，
    /// 避免 null 与"有排班"之间来回切换时漏报。
    /// </summary>
    private string? BuildDutyFingerprint()
    {
        if (_cache.State == null)
        {
            return null;
        }

        var item = _cache.GetTodayItem();
        if (item == null)
        {
            return $"empty@{_cache.Today:yyyy-MM-dd}";
        }

        var assignments = string.Join(
            "|",
            item.AreaAssignments
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}:{string.Join(",", pair.Value)}"));

        return $"{item.Date}#{assignments}";
    }

    private void PublishScheduleStateChanged(string reason)
    {
        var changeEvent = new DutyScheduleStateChangedEvent(DateTimeOffset.Now, reason);
        LastStateChangedEvent = changeEvent;
        ScheduleStateChanged?.Invoke(this, changeEvent);
        NotifyRulesetStatusChanged();
    }

    /// <summary>
    /// 让 ClassIsland 重算规则集：DutyRuleHandlerService 的"今日值日匹配"规则
    /// 读的是缓存，数据变了必须显式通知宿主，否则规则状态会停在上一次评估结果。
    /// </summary>
    private void NotifyRulesetStatusChanged()
    {
        if (_rulesetService == null)
        {
            return;
        }

        Dispatcher.UIThread.Post(_rulesetService.NotifyStatusChanged);
    }
}

public sealed record DutyScheduleRunEvent(DateTimeOffset OccurredAt, bool Success, string Message);

public sealed record DutyScheduleStateChangedEvent(DateTimeOffset OccurredAt, string Reason);
