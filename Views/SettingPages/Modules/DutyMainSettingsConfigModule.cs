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

    public DutySettingsFormModel Load()
    {
        _service.LoadConfig();
        var hostConfig = _service.Config;

        try
        {
            var backendConfig = _service.LoadBackendConfig();
            return new DutySettingsFormModel
            {
                BackendConfigAvailable = true,
                ApiKeyMask = _service.GetApiKeyMaskForUi(),
                BaseUrl = backendConfig.BaseUrl,
                Model = backendConfig.Model,
                ModelProfile = backendConfig.ModelProfile,
                OrchestrationMode = backendConfig.OrchestrationMode,
                MultiAgentExecutionMode = backendConfig.MultiAgentExecutionMode,
                ProviderHint = backendConfig.ProviderHint,
                AutoRunMode = hostConfig.AutoRunMode,
                AutoRunParameter = hostConfig.AutoRunParameter,
                AutoRunTime = hostConfig.AutoRunTime,
                AutoRunTriggerNotificationEnabled = hostConfig.AutoRunTriggerNotificationEnabled,
                DutyReminderEnabled = hostConfig.DutyReminderEnabled,
                DutyReminderTime = _service.GetDutyReminderTimes().FirstOrDefault() ?? "07:40",
                EnableMcp = hostConfig.EnableMcp,
                EnableWebViewDebugLayer = hostConfig.EnableWebViewDebugLayer,
                ComponentRefreshTime = hostConfig.ComponentRefreshTime,
                DutyRule = backendConfig.DutyRule,
                NotificationDurationSeconds = hostConfig.NotificationDurationSeconds
            };
        }
        catch (Exception ex)
        {
            return new DutySettingsFormModel
            {
                BackendConfigAvailable = false,
                BackendConfigError = ex.Message,
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
                DutyReminderTime = _service.GetDutyReminderTimes().FirstOrDefault() ?? "07:40",
                EnableMcp = hostConfig.EnableMcp,
                EnableWebViewDebugLayer = hostConfig.EnableWebViewDebugLayer,
                ComponentRefreshTime = hostConfig.ComponentRefreshTime,
                DutyRule = string.Empty,
                NotificationDurationSeconds = hostConfig.NotificationDurationSeconds
            };
        }
    }

    public DutySettingsApplyResult Apply(DutySettingsApplyRequest request)
    {
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

        try
        {
            var currentBackend = _service.LoadBackendConfig();
            var resolvedApiKey = DutyScheduleOrchestrator.ResolveApiKeyInput(request.ApiKeyInput, currentBackend.ApiKey);
            var patch = new DutyBackendConfigPatch
            {
                ApiKey = resolvedApiKey,
                BaseUrl = request.BaseUrl?.Trim() ?? currentBackend.BaseUrl,
                Model = request.Model?.Trim() ?? currentBackend.Model,
                ModelProfile = DutyScheduleOrchestrator.NormalizeModelProfile(request.ModelProfile),
                OrchestrationMode = DutyScheduleOrchestrator.NormalizeOrchestrationMode(request.OrchestrationMode),
                MultiAgentExecutionMode = DutyScheduleOrchestrator.NormalizeMultiAgentExecutionMode(request.MultiAgentExecutionMode),
                ProviderHint = request.ProviderHint?.Trim() ?? currentBackend.ProviderHint,
                DutyRule = request.DutyRule ?? string.Empty
            };

            _service.SaveBackendConfig(patch);
            return new DutySettingsApplyResult(
                Success: true,
                RestartRequired: restartRequired,
                Message: restartRequired
                    ? "设置已保存，调试层/MCP 将在重启后生效。"
                    : "设置已保存。");
        }
        catch (Exception ex)
        {
            return new DutySettingsApplyResult(
                Success: false,
                RestartRequired: restartRequired,
                Message: $"后端配置保存失败：{ex.Message}",
                BackendConfigAvailable: false);
        }
    }
}
