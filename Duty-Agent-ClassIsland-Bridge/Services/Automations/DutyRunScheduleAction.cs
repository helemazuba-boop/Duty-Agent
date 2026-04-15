using ClassIsland.Core.Abstractions.Automation;
using ClassIsland.Core.Attributes;
using DutyAgentBridge.Models;
using DutyAgentBridge.Services;

namespace DutyAgentBridge.Services.Automations.Actions;

/// <summary>
/// 执行值日排班动作
/// </summary>
[ActionInfo(
    DutyAutomationIds.RunScheduleAction,
    "\u6267\u884C\u503C\u65E5\u6392\u73ED",
    "\uE31E")]
public sealed class DutyRunScheduleAction : ActionBase<DutyRunScheduleActionSettings>
{
    private readonly IIpcBridgeService _bridge;
    private readonly DutyNotificationProvider _notificationProvider;

    public DutyRunScheduleAction(
        IIpcBridgeService bridge,
        DutyNotificationProvider notificationProvider)
    {
        _bridge = bridge;
        _notificationProvider = notificationProvider;
    }

    protected override async Task OnInvoke()
    {
        await base.OnInvoke();

        if (_bridge.State != IpcBridgeState.Connected)
        {
            throw new InvalidOperationException("桥接未连接独立软件。");
        }

        var result = await _bridge.RunScheduleAsync(Settings.Instruction);

        if (Settings.PublishCompletionNotification)
        {
            _notificationProvider.PublishScheduleCompleted(result.Success, result.Message, Settings.Instruction);
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
