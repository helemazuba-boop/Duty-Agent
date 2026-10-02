using ClassIsland.Core.Abstractions.Automation;
using ClassIsland.Core.Attributes;

namespace DutyAgentBridge.Services.Automations.Triggers;

/// <summary>值日排班执行失败时触发。</summary>
[TriggerInfo(
    DutyAutomationIds.ScheduleRunFailedTriggerId,
    "\u503C\u65E5\u6392\u73ED\u6267\u884C\u5931\u8D25\u65F6",
    "\uEA39")]
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
