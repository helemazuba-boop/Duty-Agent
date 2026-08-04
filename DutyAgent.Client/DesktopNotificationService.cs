using Microsoft.Toolkit.Uwp.Notifications;
using Windows.UI.Notifications;

namespace DutyAgent.Client;

internal sealed class DesktopNotificationService : IDisposable
{
    private readonly Form _owner;
    private readonly NotifyIcon _fallbackNotifyIcon;
    private bool _toastAvailable;

    public event EventHandler<string>? NotificationActivated;

    public DesktopNotificationService(Form owner)
    {
        _owner = owner;
        _fallbackNotifyIcon = new NotifyIcon
        {
            Text = "Duty-Agent",
            Icon = owner.Icon ?? SystemIcons.Application,
            Visible = false,
        };
        _fallbackNotifyIcon.BalloonTipClicked += (_, _) => NotificationActivated?.Invoke(this, "/dashboard");
    }

    public void Initialize()
    {
        try
        {
            WindowsShortcutActivator.EnsureShortcut();
            _toastAvailable = true;
        }
        catch
        {
            _toastAvailable = false;
        }
    }

    public void Show(NotificationEvent notification)
    {
        if (notification is null)
        {
            return;
        }

        if (_toastAvailable && TryShowToast(notification))
        {
            return;
        }

        ShowFallbackBalloon(notification);
    }

    private bool TryShowToast(NotificationEvent notification)
    {
        try
        {
            var route = NormalizeRoute(notification.Route);
            var content = new ToastContentBuilder()
                .AddArgument("route", route)
                .AddText(notification.Title)
                .AddText(notification.Body)
                .GetToastContent();

            var toast = new ToastNotification(content.GetXml())
            {
                Tag = SafeTag(notification.Id),
                Group = "duty-agent",
                ExpirationTime = DateTimeOffset.Now.AddMinutes(10),
            };
            toast.Activated += (_, _) => _owner.BeginInvoke(() => NotificationActivated?.Invoke(this, route));

            ToastNotificationManager
                .CreateToastNotifier(WindowsShortcutActivator.AppUserModelId)
                .Show(toast);
            return true;
        }
        catch
        {
            _toastAvailable = false;
            return false;
        }
    }

    private void ShowFallbackBalloon(NotificationEvent notification)
    {
        try
        {
            _fallbackNotifyIcon.Visible = true;
            _fallbackNotifyIcon.BalloonTipTitle = notification.Title;
            _fallbackNotifyIcon.BalloonTipText = notification.Body;
            _fallbackNotifyIcon.BalloonTipIcon = notification.Level.Equals("error", StringComparison.OrdinalIgnoreCase)
                ? ToolTipIcon.Error
                : notification.Level.Equals("success", StringComparison.OrdinalIgnoreCase)
                    ? ToolTipIcon.Info
                    : ToolTipIcon.None;
            _fallbackNotifyIcon.ShowBalloonTip(8000);
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        _fallbackNotifyIcon.Visible = false;
        _fallbackNotifyIcon.Dispose();
    }

    private static string SafeTag(string id)
    {
        var value = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id.Trim();
        return value.Length <= 16 ? value : value[..16];
    }

    private static string NormalizeRoute(string route)
    {
        var normalized = string.IsNullOrWhiteSpace(route) ? "/dashboard" : route.Trim();
        if (normalized.StartsWith("#/", StringComparison.Ordinal))
        {
            normalized = normalized[1..];
        }

        return normalized.StartsWith("/", StringComparison.Ordinal) ? normalized : $"/{normalized}";
    }
}
