using ClassIsland.Core.Abstractions.Automation;
using ClassIsland.Core.Attributes;

namespace DutyAgentBridge.Services.Automations.Triggers;

/// <summary>值日排班执行成功时触发。</summary>
[TriggerInfo(
    DutyAutomationIds.ScheduleRunSucceededTriggerId,
    "\u503C\u65E5\u6392\u73ED\u6267\u884C\u6210\u529F\u65F6",
    "\uE73E")]
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
