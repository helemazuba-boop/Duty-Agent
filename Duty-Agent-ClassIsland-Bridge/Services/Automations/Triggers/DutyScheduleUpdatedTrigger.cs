using ClassIsland.Core.Abstractions.Automation;
using ClassIsland.Core.Attributes;

namespace DutyAgentBridge.Services.Automations.Triggers;

/// <summary>今日值日安排发生变更时触发（保存、回滚、登记缺席、名单调整等）。</summary>
[TriggerInfo(
    DutyAutomationIds.ScheduleUpdatedTriggerId,
    "\u4ECA\u65E5\u503C\u65E5\u5B89\u6392\u53D1\u751F\u53D8\u66F4\u65F6",
    "\uE70F")]
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
