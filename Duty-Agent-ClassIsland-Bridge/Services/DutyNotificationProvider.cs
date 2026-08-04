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
    private CancellationTokenSource? _streamCts;
    private Task? _streamTask;

    public DutyNotificationProvider(IIpcBridgeService bridge)
    {
        _bridge = bridge;
        _bridge.StateChanged += (_, state) =>
        {
            if (state == IpcBridgeState.Connected)
            {
                StartNotificationStream();
            }
            else if (state is IpcBridgeState.Disconnected or IpcBridgeState.Error or IpcBridgeState.NotInstalled)
            {
                StopNotificationStream();
            }
        };
    }

    public void PublishScheduleCompleted(bool success, string message, string? instruction)
    {
        PublishGenericNotification(success ? "排班任务已完成" : "排班执行失败", message);
    }

    public void PublishAutoRunTriggered(DateTime now)
    {
        PublishGenericNotification("自动排班已开始执行", $"{now:yyyy-MM-dd HH:mm} 任务已加入队列");
    }

    public void PublishDutyReminder(string time, SchedulePoolItem? todayItem)
    {
        PublishGenericNotification($"当前值日提醒 {time}", FormatDutyReminderBody(todayItem));
    }

    private void StartNotificationStream()
    {
        if (_streamTask is { IsCompleted: false })
        {
            return;
        }

        StopNotificationStream();
        _streamCts = new CancellationTokenSource();
        var token = _streamCts.Token;
        _streamTask = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested && _bridge.State == IpcBridgeState.Connected)
            {
                try
                {
                    await _bridge.ListenNotificationsAsync(HandleNotificationAsync, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Diagnostics.Error("DutyNotificationProvider", "Notification stream failed.", ex);
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }
        }, token);
    }

    private void StopNotificationStream()
    {
        try
        {
            _streamCts?.Cancel();
            _streamCts?.Dispose();
            _streamCts = null;
        }
        catch
        {
        }
    }

    private Task HandleNotificationAsync(DutyNotificationEvent notification)
    {
        PublishGenericNotification(notification.Title, notification.Body);
        return Task.CompletedTask;
    }

    private void PublishGenericNotification(string title, string? body)
    {
        Dispatcher.UIThread.Invoke(() =>
        {
            var primaryText = string.IsNullOrWhiteSpace(title) ? "Duty-Agent" : title.Trim();
            var scrollingText = body?.Trim() ?? "";

            var maskContent = NotificationContent.CreateSimpleTextContent(primaryText, null);
            maskContent.Duration = TimeSpan.FromSeconds(2);
            maskContent.IsSpeechEnabled = false;

            var overlayContent = string.IsNullOrWhiteSpace(scrollingText)
                ? NotificationContent.CreateSimpleTextContent(primaryText, null)
                : NotificationContent.CreateRollingTextContent(
                    $"{primaryText}  {scrollingText}",
                    TimeSpan.FromSeconds(8),
                    1,
                    null);
            overlayContent.IsSpeechEnabled = false;

            ShowNotification(new NotificationRequest
            {
                MaskContent = maskContent,
                OverlayContent = overlayContent
            });
        });
    }

    private static string FormatDutyReminderBody(SchedulePoolItem? todayItem)
    {
        if (todayItem == null || todayItem.AreaAssignments.Count == 0)
        {
            return $"{DateTime.Now:yyyy-MM-dd} 暂无值日安排";
        }

        var parts = new List<string>();
        foreach (var (area, students) in todayItem.AreaAssignments)
        {
            if (students.Count > 0)
            {
                parts.Add($"{area}: {string.Join(", ", students)}");
            }
        }

        return parts.Count > 0
            ? string.Join("; ", parts)
            : $"{DateTime.Now:yyyy-MM-dd} 暂无值日安排";
    }
}
