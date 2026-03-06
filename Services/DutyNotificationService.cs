namespace DutyAgent.Services;

public sealed class DutyNotificationService
{
    public event EventHandler<DutyNotificationEvent>? NotificationRequested;

    public void Publish(string primaryText, string scrollingText = "", double durationSeconds = 8)
    {
        var primaryContent = (primaryText ?? string.Empty).Trim();
        var scrollingContent = (scrollingText ?? string.Empty).Trim();

        if (primaryContent.Length == 0 && scrollingContent.Length == 0)
        {
            return;
        }

        NotificationRequested?.Invoke(this, new DutyNotificationEvent(primaryContent, scrollingContent, durationSeconds));
    }
}

public sealed record DutyNotificationEvent(string PrimaryText, string ScrollingText, double DurationSeconds);


