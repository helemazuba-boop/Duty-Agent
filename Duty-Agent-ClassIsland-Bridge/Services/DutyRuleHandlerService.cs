using ClassIsland.Core.Abstractions.Services;
using DutyAgentBridge.Models;

namespace DutyAgentBridge.Services;

/// <summary>
/// 自动化规则处理服务
/// </summary>
public sealed class DutyRuleHandlerService
{
    private readonly IRulesetService? _rulesetService;
    private readonly IIpcBridgeService _bridge;
    private bool _registered;

    public DutyRuleHandlerService(IRulesetService? rulesetService, IIpcBridgeService bridge)
    {
        _rulesetService = rulesetService;
        _bridge = bridge;
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

        // 同步获取当前排班数据
        DutyBackendSnapshot? snapshot;
        try
        {
            snapshot = GetSnapshotSync();
        }
        catch
        {
            return false;
        }

        if (snapshot == null)
        {
            return false;
        }

        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var todayItem = snapshot.State.SchedulePool
            .FirstOrDefault(x => string.Equals(x.Date, today, StringComparison.Ordinal));

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

    private DutyBackendSnapshot? GetSnapshotSync()
    {
        try
        {
            if (_bridge.State != IpcBridgeState.Connected)
            {
                return null;
            }

            // 使用 Task.Run 同步等待（避免 async void 问题）
            return Task.Run(() => _bridge.GetSnapshotAsync()).Result;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// 自动化 ID 常量
/// </summary>
public static class DutyAutomationIds
{
    public const string TodayAssignedRule = "duty-bridge.today-assigned-rule";
}
