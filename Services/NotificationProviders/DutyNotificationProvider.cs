using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Services.NotificationProviders;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Controls.NotificationTemplates;
using ClassIsland.Core.Models.Notification;
using ClassIsland.Core.Models.Notification.Templates;
using NotificationRequest = ClassIsland.Core.Models.Notification.NotificationRequest;

namespace DutyAgent.Services.NotificationProviders;

[NotificationProviderInfo("9DF601E1-EC34-4B37-8A5B-EA933E3C580A", "Duty-Agent 通知", "\uE7F4", "显示 Duty-Agent 的排班通知")]
public sealed class DutyNotificationProvider : NotificationProviderBase
{
    public DutyNotificationProvider(DutyNotificationService notificationService)
    {
        notificationService.NotificationRequested += (_, e) => ShowDutyNotification(e);
    }

    private void ShowDutyNotification(DutyNotificationEvent e)
    {
        Dispatcher.UIThread.Invoke(() =>
        {
            var maskDuration = TimeSpan.FromSeconds(2);
            var overlayDuration = TimeSpan.FromSeconds(Math.Clamp(e.DurationSeconds, 1, 30));

            // Mask Phase - 入场横幅
            var maskContent = NotificationContent.CreateSimpleTextContent(e.PrimaryText, null);
            maskContent.Duration = maskDuration;
            maskContent.IsSpeechEnabled = false;

            // Overlay Phase - 停留提醒
            NotificationContent overlayContent;
            if (string.IsNullOrWhiteSpace(e.ScrollingText))
            {
                overlayContent = NotificationContent.CreateSimpleTextContent(e.PrimaryText, null);
            }
            else
            {
                var fullText = $"{e.PrimaryText}  {e.ScrollingText}";
                overlayContent = NotificationContent.CreateRollingTextContent(fullText, overlayDuration, 1, null);
            }
            overlayContent.Duration = overlayDuration;
            overlayContent.IsSpeechEnabled = false;

            var request = new NotificationRequest
            {
                MaskContent = maskContent,
                OverlayContent = overlayContent
            };

            ShowNotification(request);
        });
    }
}
