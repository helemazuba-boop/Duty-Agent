using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Services.NotificationProviders;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Controls.NotificationTemplates;
using ClassIsland.Core.Models.Notification;
using ClassIsland.Core.Models.Notification.Templates;
using DutyAgentBridge.Models;
using NotificationRequest = ClassIsland.Core.Models.Notification.NotificationRequest;

namespace DutyAgentBridge.Services;

[NotificationProviderInfo(
    "04c55f80-bf1f-4d79-83d9-69f9c1e6d26f",
    "Duty-Agent 桥接通知",
    "\uE7F4",
    "显示来自独立 Duty-Agent 软件的排班通知")]
public sealed class DutyNotificationProvider : NotificationProviderBase
{
    private readonly IIpcBridgeService _bridge;

    public DutyNotificationProvider(IIpcBridgeService bridge)
    {
        _bridge = bridge;
        // 通知长连接由 IpcBridgeService 持有（随连接状态启停），这里只消费事件。
        _bridge.NotificationReceived += OnNotificationReceived;
    }

    private void OnNotificationReceived(object? sender, DutyNotificationEvent notification)
    {
        // 后端 duty_reminder 事件已带格式化正文（runtime._format_duty_reminder_body），
        // 直接透传；snapshot_changed 是数据总线信号，不作为用户通知弹出。
        if (notification.Type == "snapshot_changed")
        {
            return;
        }

        PublishGenericNotification(notification.Title, notification.Body);
    }

    /// <summary>
    /// 排班任务结束通知。由自动化动作（DutyRunScheduleAction）在运行结束后调用。
    /// </summary>
    public void PublishScheduleCompleted(bool success, string message, string? instruction)
    {
        PublishGenericNotification(
            success ? "排班任务已完成" : "排班执行失败",
            string.IsNullOrWhiteSpace(message) ? $"指令：{instruction}" : message);
    }

    private void PublishGenericNotification(string title, string? body)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            var primaryText = string.IsNullOrWhiteSpace(title) ? "Duty-Agent" : title.Trim();
            var scrollingText = body?.Trim() ?? "";

            var maskContent = NotificationContent.CreateSimpleTextContent(primaryText, null);
            maskContent.Duration = TimeSpan.FromSeconds(2);

            var overlayContent = string.IsNullOrWhiteSpace(scrollingText)
                ? NotificationContent.CreateSimpleTextContent(primaryText, null)
                : NotificationContent.CreateRollingTextContent(
                    $"{primaryText}  {scrollingText}",
                    TimeSpan.FromSeconds(8),
                    1,
                    null);

            ShowNotification(new NotificationRequest
            {
                MaskContent = maskContent,
                OverlayContent = overlayContent
            });
        });
    }
}
