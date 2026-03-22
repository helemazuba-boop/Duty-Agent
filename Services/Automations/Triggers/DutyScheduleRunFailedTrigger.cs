using ClassIsland.Core.Abstractions.Automation;
using ClassIsland.Core.Attributes;

namespace DutyAgent.Services.Automations.Triggers;

[TriggerInfo(DutyAutomationIds.ScheduleRunFailedTrigger, "\u503c\u65e5\u6392\u73ed\u6267\u884c\u5931\u8d25\u65f6", "\uEA39")]
public sealed class DutyScheduleRunFailedTrigger(DutyAutomationBridgeService automationBridge) : TriggerBase
{
    public override void Loaded()
    {
        automationBridge.ScheduleRunFailed += OnScheduleRunFailed;
    }

    public override void UnLoaded()
    {
        automationBridge.ScheduleRunFailed -= OnScheduleRunFailed;
    }

    private void OnScheduleRunFailed(object? sender, DutyScheduleRunEvent e)
    {
        Trigger();
    }
}
