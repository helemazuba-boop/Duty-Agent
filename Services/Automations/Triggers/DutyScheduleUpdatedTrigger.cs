using ClassIsland.Core.Abstractions.Automation;
using ClassIsland.Core.Attributes;

namespace DutyAgent.Services.Automations.Triggers;

[TriggerInfo(DutyAutomationIds.ScheduleUpdatedTrigger, "\u4eca\u65e5\u503c\u65e5\u5b89\u6392\u53d1\u751f\u53d8\u66f4\u65f6", "\uE70F")]
public sealed class DutyScheduleUpdatedTrigger(DutyAutomationBridgeService automationBridge) : TriggerBase
{
    public override void Loaded()
    {
        automationBridge.ScheduleStateChanged += OnScheduleStateChanged;
    }

    public override void UnLoaded()
    {
        automationBridge.ScheduleStateChanged -= OnScheduleStateChanged;
    }

    private void OnScheduleStateChanged(object? sender, DutyScheduleStateChangedEvent e)
    {
        Trigger();
    }
}
