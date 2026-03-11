using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DutyAgent.Services;

public sealed class DutyAutomationBridgeService
{
    private readonly IIpcService? _ipcService;
    private readonly IRulesetService? _rulesetService;

    public event EventHandler<DutyScheduleRunEvent>? ScheduleRunCompleted;
    public event EventHandler<DutyScheduleRunEvent>? ScheduleRunSucceeded;
    public event EventHandler<DutyScheduleRunEvent>? ScheduleRunFailed;
    public event EventHandler<DutyScheduleStateChangedEvent>? ScheduleStateChanged;

    public DutyScheduleRunEvent? LastRunEvent { get; private set; }
    public DutyScheduleStateChangedEvent? LastStateChangedEvent { get; private set; }

    public DutyAutomationBridgeService(IServiceProvider serviceProvider)
    {
        _ipcService = serviceProvider.GetService<IIpcService>();
        _rulesetService = serviceProvider.GetService<IRulesetService>();
    }

    public void PublishRunCompleted(DutyScheduleRunEvent runEvent)
    {
        LastRunEvent = runEvent;
        ScheduleRunCompleted?.Invoke(this, runEvent);

        if (runEvent.Success)
        {
            ScheduleRunSucceeded?.Invoke(this, runEvent);
        }
        else
        {
            ScheduleRunFailed?.Invoke(this, runEvent);
        }

        NotifyRulesetStatusChanged();
        _ = BroadcastAsync(DutyAutomationIds.ScheduleCompletedNotification, runEvent);
        _ = BroadcastAsync(
            runEvent.Success
                ? DutyAutomationIds.ScheduleSucceededNotification
                : DutyAutomationIds.ScheduleFailedNotification,
            runEvent);
    }

    public void PublishScheduleStateChanged(string reason)
    {
        var changeEvent = new DutyScheduleStateChangedEvent(DateTimeOffset.Now, reason);
        LastStateChangedEvent = changeEvent;
        ScheduleStateChanged?.Invoke(this, changeEvent);
        NotifyRulesetStatusChanged();
        _ = BroadcastAsync(DutyAutomationIds.ScheduleUpdatedNotification, changeEvent);
    }

    private void NotifyRulesetStatusChanged()
    {
        if (_rulesetService == null)
        {
            return;
        }

        Dispatcher.UIThread.Post(_rulesetService.NotifyStatusChanged);
    }

    private async Task BroadcastAsync<T>(string id, T payload) where T : class
    {
        if (_ipcService == null)
        {
            return;
        }

        try
        {
            await _ipcService.BroadcastNotificationAsync(id, payload);
        }
        catch
        {
        }
    }
}

public sealed record DutyScheduleRunEvent(
    DateTimeOffset OccurredAt,
    bool Success,
    string Instruction,
    string ApplyMode,
    string Message,
    string? Code,
    bool IsAutoRun);

public sealed record DutyScheduleStateChangedEvent(DateTimeOffset OccurredAt, string Reason);
