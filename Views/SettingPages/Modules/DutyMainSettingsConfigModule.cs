using DutyAgent.Models;
using DutyAgent.Services;

namespace DutyAgent.Views.SettingPages.Modules;

internal sealed class DutyMainSettingsConfigModule
{
    private readonly DutyScheduleOrchestrator _service;

    public DutyMainSettingsConfigModule(DutyScheduleOrchestrator service)
    {
        _service = service;
    }

    public DutySettingsFormModel LoadHostOnly()
    {
        _service.LoadConfig();
        var hostConfig = _service.Config;

        return new DutySettingsFormModel
        {
            BackendConfigAvailable = false,
            BackendConfigError = string.Empty,
            ApiKeyMask = string.Empty,
            BaseUrl = string.Empty,
            Model = string.Empty,
            ModelProfile = "auto",
            OrchestrationMode = "auto",
            MultiAgentExecutionMode = "auto",
            ProviderHint = string.Empty,
            AutoRunMode = hostConfig.AutoRunMode,
            AutoRunParameter = hostConfig.AutoRunParameter,
            AutoRunTime = hostConfig.AutoRunTime,
            AutoRunTriggerNotificationEnabled = hostConfig.AutoRunTriggerNotificationEnabled,
            DutyReminderEnabled = hostConfig.DutyReminderEnabled,
            DutyReminderTime = GetDutyReminderTime(hostConfig),
            EnableMcp = hostConfig.EnableMcp,
            EnableWebViewDebugLayer = hostConfig.EnableWebViewDebugLayer,
            ComponentRefreshTime = hostConfig.ComponentRefreshTime,
            DutyRule = string.Empty,
            NotificationDurationSeconds = hostConfig.NotificationDurationSeconds
        };
    }

    public Task<DutyBackendConfig> LoadBackendAsync(
        string requestSource = "host_settings",
        string? traceId = null,
        CancellationToken cancellationToken = default)
    {
        return _service.LoadBackendConfigAsync(requestSource, traceId, cancellationToken);
    }

    public DutySettingsApplyResult SaveHost(DutySettingsApplyRequest request, string? traceId = null)
    {
        var effectiveTraceId = string.IsNullOrWhiteSpace(traceId)
            ? DutyDiagnosticsLogger.CreateTraceId("host-save")
            : traceId.Trim();

        _service.LoadConfig();
        var hostConfig = _service.Config;

        var previousEnableMcp = hostConfig.EnableMcp;
        var previousEnableWebViewDebugLayer = hostConfig.EnableWebViewDebugLayer;

        hostConfig.AutoRunMode = DutyScheduleOrchestrator.NormalizeAutoRunMode(request.AutoRunMode);
        hostConfig.AutoRunParameter = (request.AutoRunParameter ?? hostConfig.AutoRunParameter).Trim();
        hostConfig.AutoRunTime = DutyScheduleOrchestrator.NormalizeTimeOrThrow(request.AutoRunTime);
        hostConfig.ComponentRefreshTime = DutyScheduleOrchestrator.NormalizeTimeOrThrow(request.ComponentRefreshTime);
        hostConfig.DutyReminderEnabled = request.DutyReminderEnabled;
        hostConfig.DutyReminderTimes = [request.DutyReminderTime];
        hostConfig.EnableMcp = request.EnableMcp;
        hostConfig.EnableWebViewDebugLayer = request.EnableWebViewDebugLayer;
        hostConfig.AutoRunTriggerNotificationEnabled = request.AutoRunTriggerNotificationEnabled;
        hostConfig.NotificationDurationSeconds = Math.Clamp(request.NotificationDurationSeconds, 3, 15);

        var restartRequired = previousEnableMcp != request.EnableMcp ||
                              previousEnableWebViewDebugLayer != request.EnableWebViewDebugLayer;

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

        return new DutySettingsApplyResult(
            Success: true,
            RestartRequired: restartRequired,
            Message: restartRequired
                ? "宿主设置已保存，调试层 / MCP 将在重启后生效。"
                : "宿主设置已保存。",
            HostSaved: true,
            BackendAttempted: false,
            BackendSaved: false,
            BackendConfigAvailable: true);
    }

    public Task<DutyBackendConfig> SaveBackendAsync(
        DutyBackendConfigPatch patch,
        string requestSource = "host_settings",
        string? traceId = null,
        CancellationToken cancellationToken = default)
    {
        return _service.SaveBackendConfigAsync(patch, requestSource, traceId, cancellationToken);
    }

    private static string GetDutyReminderTime(DutyConfig hostConfig)
    {
        return (hostConfig.DutyReminderTimes ?? [])
            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))
            ?? "07:40";
    }
}
