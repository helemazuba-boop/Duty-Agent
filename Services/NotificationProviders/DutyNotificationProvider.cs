using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Services.NotificationProviders;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Models.Notification;
using NotificationRequest = ClassIsland.Core.Models.Notification.NotificationRequest;

namespace DutyIsland.Services.NotificationProviders;

[NotificationProviderInfo("9DF601E1-EC34-4B37-8A5B-EA933E3C580A", "Duty-Agent 通知", "\uE7F4", "显示 Duty-Agent 的排班通知")]
public sealed class DutyNotificationProvider : NotificationProviderBase
{
    private Border? _maskControl;
    private TextBlock? _messageTextBlock;

    public DutyNotificationProvider(DutyNotificationService notificationService)
    {
        notificationService.NotificationRequested += (_, e) => ShowDutyNotification(e);
    }

    private void ShowDutyNotification(DutyNotificationEvent e)
    {
        Dispatcher.UIThread.Invoke(() =>
        {
            if (_messageTextBlock != null)
            {
                _messageTextBlock.Text = e.Text;
                return;
            }

            _messageTextBlock = new TextBlock
            {
                Text = e.Text,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                MaxWidth = 620,
                FontSize = 20,
                FontWeight = FontWeight.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            _maskControl = new Border
            {
                Background = Brushes.Transparent,
                Padding = new Thickness(4),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = _messageTextBlock
            };

            var request = new NotificationRequest
            {
                MaskContent = new NotificationContent(_maskControl)
                {
                    Duration = TimeSpan.FromSeconds(Math.Clamp(e.DurationSeconds, 1, 30)),
                    IsSpeechEnabled = false
                }
            };
            request.CompletedToken.Register(() =>
            {
                _maskControl = null;
                _messageTextBlock = null;
            });

            ShowNotification(request);
        });
    }
}
