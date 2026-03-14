using DutyAgent.Models;
using DutyAgent.Services;

namespace DutyAgent.Views.SettingPages.Modules;

internal sealed class DutyMainSettingsHostModule
{
    private const string DefaultDutyReminderTime = "07:40";

    private readonly DutyScheduleOrchestrator _service;

    public DutyMainSettingsHostModule(DutyScheduleOrchestrator service)
    {
        _service = service;
    }

    public DutyHostSettingsValues Load()
    {
        _service.LoadConfig();
        return CreateSnapshot(_service.Config);
    }

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

    public DutyHostSettingsSaveResult Save(DutyHostSettingsValues values, string? traceId = null)
    {
        var effectiveTraceId = string.IsNullOrWhiteSpace(traceId)
            ? DutyDiagnosticsLogger.CreateTraceId("host-save")
            : traceId.Trim();

        _service.LoadConfig();
        var hostConfig = _service.Config;

        var previousEnableMcp = hostConfig.EnableMcp;
        var previousEnableWebViewDebugLayer = hostConfig.EnableWebViewDebugLayer;

        hostConfig.AutoRunMode = DutyScheduleOrchestrator.NormalizeAutoRunMode(values.AutoRunMode);
        hostConfig.AutoRunParameter = (values.AutoRunParameter ?? hostConfig.AutoRunParameter).Trim();
        hostConfig.AutoRunTime = DutyScheduleOrchestrator.NormalizeTimeOrThrow(values.AutoRunTime);
        hostConfig.ComponentRefreshTime = DutyScheduleOrchestrator.NormalizeTimeOrThrow(values.ComponentRefreshTime);
        hostConfig.DutyReminderEnabled = values.DutyReminderEnabled;
        hostConfig.DutyReminderTimes = [NormalizeDutyReminderTime(values.DutyReminderTime)];
        hostConfig.EnableMcp = values.EnableMcp;
        hostConfig.EnableWebViewDebugLayer = values.EnableWebViewDebugLayer;
        hostConfig.AutoRunTriggerNotificationEnabled = values.AutoRunTriggerNotificationEnabled;
        hostConfig.NotificationDurationSeconds = Math.Clamp(values.NotificationDurationSeconds, 3, 15);

        var restartRequired = previousEnableMcp != values.EnableMcp ||
                              previousEnableWebViewDebugLayer != values.EnableWebViewDebugLayer;

        DutyDiagnosticsLogger.Info("SettingsHost", "Saved host config.",
            new
            {
                traceId = effectiveTraceId,
                autoRunMode = hostConfig.AutoRunMode,
                autoRunParameter = hostConfig.AutoRunParameter,
                autoRunTime = hostConfig.AutoRunTime,
                componentRefreshTime = hostConfig.ComponentRefreshTime,
                dutyReminderEnabled = hostConfig.DutyReminderEnabled,
                enableMcp = hostConfig.EnableMcp,
                enableWebViewDebugLayer = hostConfig.EnableWebViewDebugLayer,
                notificationDurationSeconds = hostConfig.NotificationDurationSeconds,
                restartRequired
            });

        return new DutyHostSettingsSaveResult(
            restartRequired,
            CreateSnapshot(hostConfig));
    }

    private static DutyHostSettingsValues CreateSnapshot(DutyConfig config)
    {
        return new DutyHostSettingsValues
        {
            AutoRunMode = config.AutoRunMode,
            AutoRunParameter = config.AutoRunParameter,
            AutoRunTime = config.AutoRunTime,
            AutoRunTriggerNotificationEnabled = config.AutoRunTriggerNotificationEnabled,
            DutyReminderEnabled = config.DutyReminderEnabled,
            DutyReminderTime = GetDutyReminderTime(config),
            EnableMcp = config.EnableMcp,
            EnableWebViewDebugLayer = config.EnableWebViewDebugLayer,
            ComponentRefreshTime = config.ComponentRefreshTime,
            NotificationDurationSeconds = config.NotificationDurationSeconds
        };
    }

    private static string GetDutyReminderTime(DutyConfig hostConfig)
    {
        return (hostConfig.DutyReminderTimes ?? [])
            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))
            ?? DefaultDutyReminderTime;
    }

    private static string NormalizeDutyReminderTime(string? time)
    {
        return TimeSpan.TryParse(time, out var parsed)
            ? $"{parsed.Hours:D2}:{parsed.Minutes:D2}"
            : DefaultDutyReminderTime;
    }
}
