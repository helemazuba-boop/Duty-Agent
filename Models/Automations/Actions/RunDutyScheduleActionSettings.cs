namespace DutyAgent.Models.Automations.Actions;

public sealed class RunDutyScheduleActionSettings
{
    public string Instruction { get; set; } = string.Empty;
    public bool PublishCompletionNotification { get; set; } = true;
}
