using DutyAgent.Models;
using DutyAgent.Services;

namespace DutyAgent.Views.SettingPages.Modules;

internal sealed class DutyMainSettingsHostModule
{
    private const string DefaultDutyReminderTime = "07:40";

    public bool HasChanges(DutyHostSettingsValues current, DutyHostSettingsValues lastApplied)
    {
        return !string.Equals(
                   DutyScheduleOrchestrator.NormalizeAutoRunMode(current.AutoRunMode),
                   DutyScheduleOrchestrator.NormalizeAutoRunMode(lastApplied.AutoRunMode),
                   StringComparison.Ordinal) ||
               !string.Equals(
                   (current.AutoRunParameter ?? string.Empty).Trim(),
                   (lastApplied.AutoRunParameter ?? string.Empty).Trim(),
                   StringComparison.Ordinal) ||
               !string.Equals(
                   DutyScheduleOrchestrator.NormalizeTimeOrThrow(current.AutoRunTime),
                   DutyScheduleOrchestrator.NormalizeTimeOrThrow(lastApplied.AutoRunTime),
                   StringComparison.Ordinal) ||
               !string.Equals(
                   DutyScheduleOrchestrator.NormalizeTimeOrThrow(current.ComponentRefreshTime),
                   DutyScheduleOrchestrator.NormalizeTimeOrThrow(lastApplied.ComponentRefreshTime),
                   StringComparison.Ordinal) ||
               current.AutoRunTriggerNotificationEnabled != lastApplied.AutoRunTriggerNotificationEnabled ||
               current.DutyReminderEnabled != lastApplied.DutyReminderEnabled ||
               !NormalizeDutyReminderTimes(current.DutyReminderTimes).SequenceEqual(
                   NormalizeDutyReminderTimes(lastApplied.DutyReminderTimes),
                   StringComparer.Ordinal) ||
               !string.Equals(
                   DutyServerPortModes.Normalize(current.ServerPortMode),
                   DutyServerPortModes.Normalize(lastApplied.ServerPortMode),
                   StringComparison.Ordinal) ||
               !string.Equals(
                   NormalizeFixedServerPortText(ResolveEffectiveFixedServerPortText(current, lastApplied)),
                   NormalizeFixedServerPortText(lastApplied.FixedServerPortText),
                   StringComparison.Ordinal) ||
               current.EnableMcp != lastApplied.EnableMcp ||
               current.EnableWebViewDebugLayer != lastApplied.EnableWebViewDebugLayer ||
               Math.Clamp(current.NotificationDurationSeconds, 3, 15) !=
               Math.Clamp(lastApplied.NotificationDurationSeconds, 3, 15);
    }

    internal static List<string> NormalizeDutyReminderTimes(IEnumerable<string>? values)
    {
        var times = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (values != null)
        {
            foreach (var raw in values)
            {
                var text = raw ?? string.Empty;
                foreach (var token in text.Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!TryNormalizeDutyReminderTime(token, out var normalized) || !seen.Add(normalized))
                    {
                        continue;
                    }

                    times.Add(normalized);
                }
            }
        }

        if (times.Count == 0)
        {
            times.Add(DefaultDutyReminderTime);
        }

        times.Sort(StringComparer.Ordinal);
        return times;
    }

    internal static string FormatDutyReminderTimes(IEnumerable<string>? values)
    {
        return string.Join(", ", NormalizeDutyReminderTimes(values));
    }

    private static bool TryNormalizeDutyReminderTime(string? time, out string normalized)
    {
        normalized = string.Empty;
        if (!TimeSpan.TryParse(time, out var parsed))
        {
            return false;
        }

        if (parsed < TimeSpan.Zero || parsed >= TimeSpan.FromDays(1))
        {
            return false;
        }

        normalized = $"{parsed.Hours:D2}:{parsed.Minutes:D2}";
        return true;
    }

    internal static string ResolveEffectiveFixedServerPortText(DutyHostSettingsValues current, DutyHostSettingsValues lastApplied)
    {
        var currentMode = DutyServerPortModes.Normalize(current.ServerPortMode);
        var currentText = (current.FixedServerPortText ?? string.Empty).Trim();
        if (currentMode == DutyServerPortModes.Random && currentText.Length == 0)
        {
            return lastApplied.FixedServerPortText ?? string.Empty;
        }

        return currentText;
    }

    private static string NormalizeFixedServerPortText(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return int.TryParse(normalized, out var port) ? port.ToString() : normalized;
    }
}
