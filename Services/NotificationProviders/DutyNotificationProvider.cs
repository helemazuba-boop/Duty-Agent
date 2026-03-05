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
            // 为 Mask 和 Overlay 创建独立的 UI 实例，避免单实例在 Avalonia 中无法重复挂载到多个 Parent 的问题
            var maskUI = CreateNotificationUI(e);
            var overlayUI = CreateNotificationUI(e);

            var request = new NotificationRequest
            {
                MaskContent = new NotificationContent(maskUI)
                {
                    Duration = TimeSpan.FromSeconds(2),
                    IsSpeechEnabled = false
                },
                OverlayContent = new NotificationContent(overlayUI)
                {
                    Duration = TimeSpan.FromSeconds(Math.Clamp(e.DurationSeconds, 1, 30)),
                    IsSpeechEnabled = false
                }
            };

            ShowNotification(request);
        });
    }

    private Control CreateNotificationUI(DutyNotificationEvent e)
    {
        var primaryTextBlock = new TextBlock
        {
            Text = e.PrimaryText,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            MaxWidth = 620,
            FontSize = 20,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var rollingContainer = new ContentControl
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 620,
            IsVisible = !string.IsNullOrWhiteSpace(e.ScrollingText)
        };

        if (!string.IsNullOrWhiteSpace(e.ScrollingText))
        {
            var rollingTextData = new RollingTextTemplateData
            {
                Text = e.ScrollingText,
                Duration = TimeSpan.FromSeconds(Math.Clamp(e.DurationSeconds, 1, 30))
            };
            rollingContainer.Content = new RollingTextTemplate(rollingTextData);
        }

        var stackPanel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        
        stackPanel.Children.Add(primaryTextBlock);
        stackPanel.Children.Add(rollingContainer);

        return new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(4),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = stackPanel
        };
    }
}
