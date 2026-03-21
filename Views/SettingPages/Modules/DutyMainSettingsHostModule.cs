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
               !string.Equals(
                   NormalizeDutyReminderTime(current.DutyReminderTime),
                   NormalizeDutyReminderTime(lastApplied.DutyReminderTime),
                   StringComparison.Ordinal) ||
               current.EnableMcp != lastApplied.EnableMcp ||
               current.EnableWebViewDebugLayer != lastApplied.EnableWebViewDebugLayer ||
               Math.Clamp(current.NotificationDurationSeconds, 3, 15) !=
               Math.Clamp(lastApplied.NotificationDurationSeconds, 3, 15);
    }

    private static string NormalizeDutyReminderTime(string? time)
    {
        return TimeSpan.TryParse(time, out var parsed)
            ? $"{parsed.Hours:D2}:{parsed.Minutes:D2}"
            : DefaultDutyReminderTime;
    }
}
