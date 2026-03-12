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
        var config = _service.Config;

        return new DutySettingsFormModel
        {
            ApiKeyMask = _service.GetApiKeyMaskForUi(),
            BaseUrl = config.BaseUrl,
            Model = config.Model,
            ModelProfile = config.ModelProfile,
            OrchestrationMode = config.OrchestrationMode,
            ProviderHint = config.ProviderHint,
            AutoRunMode = config.AutoRunMode,
            AutoRunParameter = config.AutoRunParameter,
            AutoRunTime = config.AutoRunTime,
            AutoRunTriggerNotificationEnabled = config.AutoRunTriggerNotificationEnabled,
            DutyReminderEnabled = config.DutyReminderEnabled,
            DutyReminderTime = _service.GetDutyReminderTimes().FirstOrDefault() ?? "07:40",
            EnableMcp = config.EnableMcp,
            EnableWebViewDebugLayer = config.EnableWebViewDebugLayer,
            ComponentRefreshTime = config.ComponentRefreshTime,
            DutyRule = config.DutyRule,
            NotificationDurationSeconds = config.NotificationDurationSeconds
        };
    }

    public DutySettingsApplyResult Apply(DutySettingsApplyRequest request)
    {
        _service.LoadConfig();
        var current = _service.Config;

        var previousEnableMcp = current.EnableMcp;
        var previousEnableWebViewDebugLayer = current.EnableWebViewDebugLayer;

        var resolvedApiKey = DutyScheduleOrchestrator.ResolveApiKeyInput(request.ApiKeyInput, current.DecryptedApiKey);
        var dutyReminderTimes = new List<string> { request.DutyReminderTime };

        current.DecryptedApiKey = resolvedApiKey;
        current.BaseUrl = request.BaseUrl?.Trim() ?? string.Empty;
        current.Model = request.Model?.Trim() ?? string.Empty;
        current.ModelProfile = DutyScheduleOrchestrator.NormalizeModelProfile(request.ModelProfile);
        current.OrchestrationMode = DutyScheduleOrchestrator.NormalizeOrchestrationMode(request.OrchestrationMode);
        current.ProviderHint = request.ProviderHint?.Trim() ?? current.ProviderHint;
        current.AutoRunMode = DutyScheduleOrchestrator.NormalizeAutoRunMode(request.AutoRunMode);
        current.AutoRunParameter = (request.AutoRunParameter ?? current.AutoRunParameter).Trim();
        current.AutoRunTime = DutyScheduleOrchestrator.NormalizeTimeOrThrow(request.AutoRunTime);
        current.PerDay = Math.Clamp(current.PerDay, 1, 30);
        current.DutyRule = request.DutyRule ?? string.Empty;
        current.ComponentRefreshTime = DutyScheduleOrchestrator.NormalizeTimeOrThrow(request.ComponentRefreshTime);
        current.DutyReminderEnabled = request.DutyReminderEnabled;
        current.DutyReminderTimes = dutyReminderTimes;
        current.EnableMcp = request.EnableMcp;
        current.EnableWebViewDebugLayer = request.EnableWebViewDebugLayer;
        current.AutoRunTriggerNotificationEnabled = request.AutoRunTriggerNotificationEnabled;
        current.NotificationDurationSeconds = Math.Clamp(request.NotificationDurationSeconds, 3, 15);

        var restartRequired = previousEnableMcp != request.EnableMcp ||
                              previousEnableWebViewDebugLayer != request.EnableWebViewDebugLayer;
        return new DutySettingsApplyResult(restartRequired);
    }
}

