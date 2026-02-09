using System.Text.RegularExpressions;

namespace DutyIsland.Services;

public sealed class DutyNotificationService
{
    private static readonly Regex PlaceholderRegex = new(@"\{([a-zA-Z0-9_]+)\}", RegexOptions.Compiled);

    public event EventHandler<DutyNotificationEvent>? NotificationRequested;

    public void Publish(string text, double durationSeconds = 6)
    {
        var content = (text ?? string.Empty).Trim();
        if (content.Length == 0)
        {
            return;
        }

        NotificationRequested?.Invoke(this, new DutyNotificationEvent(content, durationSeconds));
    }

    public void PublishFromTemplates(
        IEnumerable<string> templates,
        IReadOnlyDictionary<string, string> placeholders,
        string fallbackText,
        double durationSeconds = 6)
    {
        var rendered = templates
            .Select(x => RenderTemplate(x, placeholders))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (rendered.Count == 0)
        {
            Publish(fallbackText, durationSeconds);
            return;
        }

        foreach (var message in rendered)
        {
            Publish(message, durationSeconds);
        }
    }

    private static string RenderTemplate(string template, IReadOnlyDictionary<string, string> placeholders)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return string.Empty;
        }

        return PlaceholderRegex.Replace(template, match =>
        {
            var key = match.Groups[1].Value;
            return placeholders.TryGetValue(key, out var value) ? value : match.Value;
        }).Trim();
    }
}

public sealed record DutyNotificationEvent(string Text, double DurationSeconds);

