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

        current.DecryptedApiKey = resolvedApiKey;
        current.BaseUrl = request.BaseUrl?.Trim() ?? string.Empty;
        current.Model = request.Model?.Trim() ?? string.Empty;
        current.AutoRunMode = DutyBackendService.NormalizeAutoRunMode(request.AutoRunMode);
        current.AutoRunParameter = (request.AutoRunParameter ?? current.AutoRunParameter).Trim();
        current.AutoRunTime = DutyBackendService.NormalizeTimeOrThrow(request.AutoRunTime);
        current.PerDay = Math.Clamp(current.PerDay, 1, 30);
        current.DutyRule = request.DutyRule ?? string.Empty;
        current.ComponentRefreshTime = DutyBackendService.NormalizeTimeOrThrow(request.ComponentRefreshTime);
        current.DutyReminderEnabled = request.DutyReminderEnabled;
        current.DutyReminderTimes = dutyReminderTimes;
        current.EnableMcp = request.EnableMcp;
        current.EnableWebViewDebugLayer = request.EnableWebViewDebugLayer;
        current.AutoRunTriggerNotificationEnabled = request.AutoRunTriggerNotificationEnabled;

        var restartRequired = previousEnableMcp != request.EnableMcp ||
                              previousEnableWebViewDebugLayer != request.EnableWebViewDebugLayer;
        return new DutySettingsApplyResult(restartRequired);
    }
}

