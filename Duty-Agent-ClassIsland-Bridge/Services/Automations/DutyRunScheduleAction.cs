using ClassIsland.Core.Abstractions.Automation;
using ClassIsland.Core.Attributes;
using ClassIsland.Shared;
using DutyAgentBridge.Models;
using DutyAgentBridge.Services;

namespace DutyAgentBridge.Services.Automations.Actions;

/// <summary>
/// 执行值日排班动作
/// </summary>
[ActionInfo(
    DutyAutomationIds.RunScheduleActionId,
    "\u6267\u884C\u503C\u65E5\u6392\u73ED",
    "\uE31E")]
public sealed class DutyRunScheduleAction : ActionBase<DutyRunScheduleActionSettings>
{
    private readonly IIpcBridgeService _bridge;

    public DutyRunScheduleAction(IIpcBridgeService bridge)
    {
        _bridge = bridge;
    }

    protected override async Task OnInvoke()
    {
        await base.OnInvoke();

        if (_bridge.State != IpcBridgeState.Connected)
        {
            throw new InvalidOperationException("桥接未连接独立软件。");
        }

        var result = await _bridge.RunScheduleAsync(
            Settings.Instruction,
            progress => Diagnostics.Log(
                "DutyRunScheduleAction",
                $"Run progress: {progress.Phase} {progress.Message}",
                "DEBUG"));

        // 运行结束（无论成败）都要给 ClassIsland 一个可见反馈；
        // 尊重动作设置中的完成通知开关。通知提供方为宿主注册的单例，
        // 这里惰性解析以避免把解析失败放大成动作失败。
        if (Settings.PublishCompletionNotification)
        {
            var notifications = IAppHost.GetService<DutyNotificationProvider>();
            notifications?.PublishScheduleCompleted(
                result.Success,
                result.Success ? (result.AiResponse ?? "") : result.Message,
                Settings.Instruction);
        }

        if (!result.Success)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(result.Message)
                    ? "排班执行失败。"
                    : result.Message);
        }
    }
}
