using ClassIsland.Core.Abstractions.Services;
using DutyAgent.Models.Automations.Rules;

namespace DutyAgent.Services;

public sealed class DutyRuleHandlerService
{
    private readonly IRulesetService _rulesetService;
    private readonly DutyScheduleOrchestrator _orchestrator;
    private bool _registered;

    public DutyRuleHandlerService(IRulesetService rulesetService, DutyScheduleOrchestrator orchestrator)
    {
        _rulesetService = rulesetService;
        _orchestrator = orchestrator;
    }

    public void Register()
    {
        if (_registered)
        {
            return;
        }

        _rulesetService.RegisterRuleHandler(DutyAutomationIds.TodayAssignedRule, HandleTodayAssignedRule);
        _registered = true;
    }

    private bool HandleTodayAssignedRule(object? settingsObject)
    {
        if (settingsObject is not DutyAssignedStudentRuleSettings settings)
        {
            return false;
        }

        var studentName = (settings.StudentName ?? string.Empty).Trim();
        var areaName = (settings.AreaName ?? string.Empty).Trim();

        var item = _orchestrator.GetCurrentScheduleItem();
        if (item == null)
        {
            return false;
        }

        var assignments = _orchestrator.GetAreaAssignments(item);
        if (studentName.Length == 0 && areaName.Length == 0)
        {
            return assignments.Values.Any(names => names.Count > 0);
        }

        if (areaName.Length > 0)
        {
            if (!assignments.TryGetValue(areaName, out var students))
            {
                return false;
            }

            return studentName.Length == 0 ||
                   students.Any(name => string.Equals(name, studentName, StringComparison.Ordinal));
        }

        return assignments.Values.Any(students =>
            students.Any(name => string.Equals(name, studentName, StringComparison.Ordinal)));
    }
}
