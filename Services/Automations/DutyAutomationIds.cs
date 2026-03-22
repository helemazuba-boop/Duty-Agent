namespace DutyAgent.Services;

public static class DutyAutomationIds
{
    public const string RunScheduleAction = "duty-agent.actions.runSchedule";

    public const string ScheduleRunSucceededTrigger = "duty-agent.triggers.scheduleRunSucceeded";
    public const string ScheduleRunFailedTrigger = "duty-agent.triggers.scheduleRunFailed";
    public const string ScheduleUpdatedTrigger = "duty-agent.triggers.scheduleUpdated";

    public const string TodayAssignedRule = "duty-agent.rules.todayAssigned";

    public const string ScheduleCompletedNotification = "duty-agent.schedule.completed";
    public const string ScheduleSucceededNotification = "duty-agent.schedule.succeeded";
    public const string ScheduleFailedNotification = "duty-agent.schedule.failed";
    public const string ScheduleUpdatedNotification = "duty-agent.schedule.updated";
}
