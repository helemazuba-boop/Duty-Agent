using DutyAgent.Services;

namespace DutyAgent.Views.SettingPages.Modules;

internal sealed class DutyMainSettingsConfigModule
{
    private readonly DutyBackendService _service;

    public DutyMainSettingsConfigModule(DutyBackendService service)
    {
        _service = service;
    }

    public DutySettingsFormModel Load()
    {
        _service.LoadConfig();
        var config = _service.Config;

        return new DutySettingsFormModel
        {
            ApiKeyMask = _service.GetApiKeyMaskForUi(),
            BaseUrl = config.BaseUrl,
            Model = config.Model,
            AutoRunMode = config.AutoRunMode,
            AutoRunParameter = config.AutoRunParameter,
            AutoRunTime = config.AutoRunTime,
            AutoRunTriggerNotificationEnabled = config.AutoRunTriggerNotificationEnabled,
            DutyReminderEnabled = config.DutyReminderEnabled,
            DutyReminderTime = _service.GetDutyReminderTimes().FirstOrDefault() ?? "07:40",
            EnableMcp = config.EnableMcp,
            EnableWebViewDebugLayer = config.EnableWebViewDebugLayer,
            ComponentRefreshTime = config.ComponentRefreshTime,
            DutyRule = config.DutyRule
        };
    }

    public DutySettingsApplyResult Apply(DutySettingsApplyRequest request)
    {
        _service.LoadConfig();
        var current = _service.Config;

        var previousEnableMcp = current.EnableMcp;
        var previousEnableWebViewDebugLayer = current.EnableWebViewDebugLayer;

        var resolvedApiKey = DutyBackendService.ResolveApiKeyInput(request.ApiKeyInput, current.DecryptedApiKey);
        var dutyReminderTimes = new List<string> { request.DutyReminderTime };

        _service.SaveUserConfig(
            apiKey: resolvedApiKey,
            baseUrl: request.BaseUrl?.Trim() ?? string.Empty,
            model: request.Model?.Trim() ?? string.Empty,
            autoRunMode: request.AutoRunMode,
            autoRunParameter: request.AutoRunParameter,
            autoRunTime: request.AutoRunTime,
            perDay: Math.Clamp(current.PerDay, 1, 30),
            dutyRule: request.DutyRule ?? string.Empty,
            componentRefreshTime: request.ComponentRefreshTime,
            pythonPath: current.PythonPath,
            dutyReminderEnabled: request.DutyReminderEnabled,
            dutyReminderTimes: dutyReminderTimes,
            enableMcp: request.EnableMcp,
            enableWebViewDebugLayer: request.EnableWebViewDebugLayer,
            autoRunTriggerNotificationEnabled: request.AutoRunTriggerNotificationEnabled);

        var restartRequired = previousEnableMcp != request.EnableMcp ||
                              previousEnableWebViewDebugLayer != request.EnableWebViewDebugLayer;
        return new DutySettingsApplyResult(restartRequired);
    }
}

