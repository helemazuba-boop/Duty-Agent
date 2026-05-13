using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Services.NotificationProviders;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Controls.NotificationTemplates;
using ClassIsland.Core.Models.Notification;
using ClassIsland.Core.Models.Notification.Templates;
using DutyAgentBridge.Models;
using NotificationRequest = ClassIsland.Core.Models.Notification.NotificationRequest;

namespace DutyAgentBridge.Services;

/// <summary>
/// 通知提供者（继承 ClassIsland 通知系统）
/// 通过 IpcBridgeService 获取独立软件的排班状态，发布通知
/// </summary>
[NotificationProviderInfo(
    "DUTY-BRIDGE-NOTIFY-001",
    "Duty-Agent 桥接通知",
    "\uE7F4",
    "显示来自独立 Duty-Agent 软件的排班通知")]
public sealed class DutyNotificationProvider : NotificationProviderBase
{
    private readonly IIpcBridgeService _bridge;
    private readonly DispatcherTimer _pollTimer;
    private string? _lastScheduleDate;
    private string? _lastScheduleContent;

    public DutyNotificationProvider(IIpcBridgeService bridge)
    {
        _bridge = bridge;

        // 监听连接状态变化，连接后启动轮询
        _bridge.StateChanged += (_, state) =>
        {
            if (state == IpcBridgeState.Connected)
            {
                Dispatcher.UIThread.Post(StartPolling);
            }
            else if (state == IpcBridgeState.Disconnected || state == IpcBridgeState.Error)
            {
                Dispatcher.UIThread.Post(StopPolling);
            }
        };

        // 监听排班更新事件（如果 WebSocket 连接）
        _bridge.StateChanged += OnBridgeStateChanged;

        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(1)
        };
        _pollTimer.Tick += async (_, _) => await PollScheduleAsync();
    }

    private void OnBridgeStateChanged(object? sender, IpcBridgeState e)
    {
        // 当连接建立时立即拉取一次状态
        if (e == IpcBridgeState.Connected)
        {
            Dispatcher.UIThread.Post(async () => await PollScheduleAsync());
        }
    }

    private void StartPolling()
    {
        _pollTimer.Start();
    }

    private void StopPolling()
    {
        _pollTimer.Stop();
    }

    private async Task PollScheduleAsync()
    {
        if (_bridge.State != IpcBridgeState.Connected)
        {
            return;
        }

        try
        {
            var snapshot = await _bridge.GetSnapshotAsync();
            var today = DateTime.Now.ToString("yyyy-MM-dd");

            var todayItem = snapshot.State.SchedulePool
                .FirstOrDefault(x => string.Equals(x.Date, today, StringComparison.Ordinal));

            if (todayItem == null)
            {
                return;
            }

            var content = SerializeScheduleContent(todayItem);

            // 仅当内容变化时通知
            if (content != _lastScheduleContent)
            {
                _lastScheduleContent = content;
                _lastScheduleDate = today;
                // 不自动弹出通知，只在需要时由外部触发
            }
        }
        catch
        {
            // 静默忽略轮询错误
        }
    }

    private static string SerializeScheduleContent(SchedulePoolItem item)
    {
        var parts = new List<string>();
        foreach (var (area, students) in item.AreaAssignments)
        {
            if (students.Count > 0)
            {
                parts.Add($"{area}：{string.Join("、", students)}");
            }
        }
        return string.Join("；", parts);
    }

    /// <summary>
    /// 发布排班完成通知（由外部调用，如 RunScheduleAction）
    /// </summary>
    public void PublishScheduleCompleted(bool success, string message, string? instruction)
    {
        Dispatcher.UIThread.Invoke(() =>
        {
            var primaryText = success ? $"排班任务已完成" : $"排班执行失败";
            var scrollingText = message;

            var maskContent = NotificationContent.CreateSimpleTextContent(primaryText, null);
            maskContent.Duration = TimeSpan.FromSeconds(2);
            maskContent.IsSpeechEnabled = false;

            NotificationContent overlayContent;
            if (string.IsNullOrWhiteSpace(scrollingText))
            {
                overlayContent = NotificationContent.CreateSimpleTextContent(primaryText, null);
            }
            else
            {
                overlayContent = NotificationContent.CreateRollingTextContent(
                    $"{primaryText}  {scrollingText}",
                    TimeSpan.FromSeconds(8),
                    1,
                    null);
            }
            overlayContent.IsSpeechEnabled = false;

            var request = new NotificationRequest
            {
                MaskContent = maskContent,
                OverlayContent = overlayContent
            };

            ShowNotification(request);
        });
    }

    /// <summary>
    /// 发布自动排班触发通知
    /// </summary>
    public void PublishAutoRunTriggered(DateTime now)
    {
        Dispatcher.UIThread.Invoke(() =>
        {
            var primaryText = "自动排班已开始执行";
            var scrollingText = $"{now:yyyy-MM-dd HH:mm} 任务已加入队列";

            var maskContent = NotificationContent.CreateSimpleTextContent(primaryText, null);
            maskContent.Duration = TimeSpan.FromSeconds(2);
            maskContent.IsSpeechEnabled = false;

            var overlayContent = NotificationContent.CreateRollingTextContent(
                $"{primaryText}  {scrollingText}",
                TimeSpan.FromSeconds(8),
                1,
                null);
            overlayContent.IsSpeechEnabled = false;

            var request = new NotificationRequest
            {
                MaskContent = maskContent,
                OverlayContent = overlayContent
            };

            ShowNotification(request);
        });
    }

    /// <summary>
    /// 发布值日提醒通知
    /// </summary>
    public void PublishDutyReminder(string time, SchedulePoolItem? todayItem)
    {
        Dispatcher.UIThread.Invoke(() =>
        {
            var primaryText = $"当前值日提醒 {time}";

            string scrollingText;
            if (todayItem == null || todayItem.AreaAssignments.Count == 0)
            {
                scrollingText = $"{DateTime.Now:yyyy-MM-dd} 暂无值日安排";
            }
            else
            {
                var parts = new List<string>();
                foreach (var (area, students) in todayItem.AreaAssignments)
                {
                    if (students.Count > 0)
                    {
                        parts.Add($"{area}：{string.Join("、", students)}");
                    }
                }
                scrollingText = parts.Count > 0
                    ? string.Join("；", parts)
                    : $"{DateTime.Now:yyyy-MM-dd} 暂无值日安排";
            }

            var maskContent = NotificationContent.CreateSimpleTextContent(primaryText, null);
            maskContent.Duration = TimeSpan.FromSeconds(2);
            maskContent.IsSpeechEnabled = false;

            var overlayContent = NotificationContent.CreateRollingTextContent(
                $"{primaryText}  {scrollingText}",
                TimeSpan.FromSeconds(8),
                1,
                null);
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
