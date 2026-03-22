using ClassIsland.Core.Abstractions.Automation;
using ClassIsland.Core.Attributes;

namespace DutyAgent.Services.Automations.Triggers;

[TriggerInfo(DutyAutomationIds.ScheduleRunSucceededTrigger, "\u503c\u65e5\u6392\u73ed\u6267\u884c\u6210\u529f\u65f6", "\uE73E")]
public sealed class DutyScheduleRunSucceededTrigger(DutyAutomationBridgeService automationBridge) : TriggerBase
{
    public override void Loaded()
    {
        automationBridge.ScheduleRunSucceeded += OnScheduleRunSucceeded;
    }

    public override void UnLoaded()
    {
        automationBridge.ScheduleRunSucceeded -= OnScheduleRunSucceeded;
    }

    private void OnScheduleRunSucceeded(object? sender, DutyScheduleRunEvent e)
    {
        Trigger();
    }
}
