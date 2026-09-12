using ClassIsland.Core.Abstractions.Services;
using DutyAgentBridge.Models;

namespace DutyAgentBridge.Services;

/// <summary>
/// 自动化规则处理服务。
/// 规则评估发生在 ClassIsland 的规则引擎调用路径上，因此这里只做纯内存判断：
/// 数据来自 DutyStateCache（由推送/兜底轮询维护），绝不发起同步网络请求。
/// </summary>
public sealed class DutyRuleHandlerService
{
    private readonly IRulesetService? _rulesetService;
    private readonly DutyStateCache _cache;
    private bool _registered;

    public DutyRuleHandlerService(IRulesetService? rulesetService, DutyStateCache cache)
    {
        _rulesetService = rulesetService;
        _cache = cache;
    }

    public void Register()
    {
        if (_registered || _rulesetService == null)
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

        var todayItem = _cache.GetTodayItem();
        if (todayItem == null)
        {
            return false;
        }

        var assignments = todayItem.AreaAssignments;

        if (studentName.Length == 0 && areaName.Length == 0)
        {
            // 无条件匹配：只要有安排就触发
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

        // 只匹配学生名（不限区域）
        return assignments.Values
            .Any(students => students.Any(name => string.Equals(name, studentName, StringComparison.Ordinal)));
    }
}

/// <summary>
/// 自动化 ID 常量。
/// 历史约定：Action/Rule 使用 kebab 字符串 id（组件/通知提供方使用 GUID）。
/// 不改为 GUID——已保存的自动化配置按这些 id 引用，改动会让存量配置失效。
/// </summary>
public static class DutyAutomationIds
{
    public const string RunScheduleActionId = "duty-bridge.run-schedule-action";
    public const string TodayAssignedRule = "duty-bridge.today-assigned-rule";
}
