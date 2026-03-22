using ClassIsland.Core.Abstractions.Automation;
using ClassIsland.Core.Attributes;
using DutyAgent.Models.Automations.Actions;

namespace DutyAgent.Services.Automations.Actions;

[ActionInfo(DutyAutomationIds.RunScheduleAction, "\u6267\u884c\u503c\u65e5\u6392\u73ed", "\uE31E")]
public sealed class RunDutyScheduleAction(DutyScheduleOrchestrator orchestrator)
    : ActionBase<RunDutyScheduleActionSettings>
{
    protected override async Task OnInvoke()
    {
        await base.OnInvoke();

        var applyMode = NormalizeApplyMode(Settings.ApplyMode);
        var result = await orchestrator.RunCoreAgentAsync(Settings.Instruction, applyMode);

        if (Settings.PublishCompletionNotification)
        {
            orchestrator.PublishRunCompletionNotification(
                Settings.Instruction,
                applyMode,
                result.Message,
                result.Success);
        }

        if (!result.Success)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(result.Message)
                    ? "Duty-Agent schedule run failed."
                    : result.Message);
        }
    }

    private static string NormalizeApplyMode(string? applyMode)
    {
        return (applyMode ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "append" => "append",
            "replace_future" => "replace_future",
            "replace_overlap" => "replace_overlap",
            "replace_all" => "replace_all",
            _ => "replace_all"
        };
    }
}
