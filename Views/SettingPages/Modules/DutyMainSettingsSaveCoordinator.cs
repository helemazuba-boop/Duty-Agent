using System.Linq;
using DutyAgent.Models;
using DutyAgent.Services;

namespace DutyAgent.Views.SettingPages.Modules;

internal sealed class DutyMainSettingsSaveCoordinator
{
    private const int MinServicePort = 1024;
    private const int MaxServicePort = 65535;
    private readonly IDutySettingsRepository _settingsRepository;
    private readonly DutyBackendSettingsSyncService _backendSyncService;
    private readonly DutyMainSettingsHostModule _hostModule;
    private readonly DutyMainSettingsBackendModule _backendModule;
    private readonly DutySettingsTraceService _settingsTrace;

    public DutyMainSettingsSaveCoordinator(
        IDutySettingsRepository settingsRepository,
        DutyBackendSettingsSyncService backendSyncService,
        DutyMainSettingsHostModule hostModule,
        DutyMainSettingsBackendModule backendModule,
        DutySettingsTraceService settingsTrace)
    {
        _settingsRepository = settingsRepository;
        _backendSyncService = backendSyncService;
        _hostModule = hostModule;
        _backendModule = backendModule;
        _settingsTrace = settingsTrace;
    }

    public Task<DutySettingsSaveOutcome> ApplyAsync(
        DutySettingsSaveContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var hostChanged = _hostModule.HasChanges(context.Current.Host, context.LastAppliedHost);
        var backendPatch = _backendModule.TryBuildPatch(context.Current.Backend, context.LastAppliedBackend);
        var backendChanged = backendPatch != null;

        _settingsTrace.Info("save_patch_built", new
        {
            host_changed = hostChanged,
            backend_changed = backendChanged,
            selected_plan_id = backendPatch?.SelectedPlanId ?? context.Current.Backend.SelectedPlanId,
            plan_count = backendPatch?.PlanPresets?.Count ?? context.Current.Backend.PlanPresets?.Count ?? 0
        });

        if (!hostChanged && !backendChanged)
        {
            return Task.FromResult(new DutySettingsSaveOutcome(
                Success: true,
                NoChanges: true,
                RestartRequired: false,
                HostChanged: false,
                HostSaved: false,
                BackendChanged: false,
                BackendSaved: false,
                Message: string.Empty,
                MessageLevel: DutySettingsSaveMessageLevel.Info));
        }

        try
        {
            var patchRequest = BuildPatchRequest(context, hostChanged, backendPatch);
            var savedLocalSettings = _settingsRepository.SavePatch(patchRequest);
            var appliedDocument = CreateSettingsDocument(savedLocalSettings);
            var appliedHost = CreateHostValues(appliedDocument.Host);
            var appliedBackend = CreateBackendConfig(appliedDocument);

            if (backendChanged)
            {
                _backendSyncService.RequestSync("settings_edit");
            }

            var restartRequired = hostChanged &&
                                  (context.LastAppliedHost.EnableMcp != context.Current.Host.EnableMcp ||
                                   context.LastAppliedHost.EnableWebViewDebugLayer != context.Current.Host.EnableWebViewDebugLayer ||
                                   !string.Equals(
                                       DutyServerPortModes.Normalize(context.LastAppliedHost.ServerPortMode),
                                       DutyServerPortModes.Normalize(context.Current.Host.ServerPortMode),
                                       StringComparison.Ordinal) ||
                                   !string.Equals(
                                       NormalizeFixedServerPortText(ResolveFixedServerPortTextForSave(context.Current.Host, context.LastAppliedHost)),
                                       NormalizeFixedServerPortText(context.LastAppliedHost.FixedServerPortText),
                                       StringComparison.Ordinal));

            _settingsTrace.Info("local_settings_saved", new
            {
                version = savedLocalSettings.Version,
                host_changed = hostChanged,
                backend_changed = backendChanged,
                restart_required = restartRequired,
                selected_plan_id = savedLocalSettings.Backend.SelectedPlanId
            });

            var message = backendChanged
                ? "本地设置已保存，后端同步中。"
                : "本地设置已保存。";

            return Task.FromResult(new DutySettingsSaveOutcome(
                Success: true,
                NoChanges: false,
                RestartRequired: restartRequired,
                HostChanged: hostChanged,
                HostSaved: hostChanged,
                BackendChanged: backendChanged,
                BackendSaved: backendChanged,
                Message: message,
                MessageLevel: DutySettingsSaveMessageLevel.Info,
                AppliedDocument: appliedDocument,
                AppliedHost: appliedHost,
                AppliedBackend: appliedBackend));
        }
        catch (Exception ex)
        {
            DutyDiagnosticsLogger.Error("SettingsPage", "Failed to persist local settings.", ex);
            _settingsTrace.Error("local_settings_save_failed", new
            {
                host_changed = hostChanged,
                backend_changed = backendChanged,
                error = ex.Message
            });
            return Task.FromResult(new DutySettingsSaveOutcome(
                Success: false,
                NoChanges: false,
                RestartRequired: false,
                HostChanged: hostChanged,
                HostSaved: false,
                BackendChanged: backendChanged,
                BackendSaved: false,
                Message: $"本地设置保存失败：{ex.Message}",
                MessageLevel: DutySettingsSaveMessageLevel.Error));
        }
    }

    private DutySettingsPatchRequest BuildPatchRequest(
        DutySettingsSaveContext context,
        bool hostChanged,
        DutyBackendConfigPatch? backendPatch)
    {
        var request = new DutySettingsPatchRequest
        {
            Changes = new DutySettingsPatchChanges()
        };

        if (hostChanged)
        {
            request.Changes.Host = new DutyEditableHostSettingsPatch
            {
                AutoRunMode = DutyScheduleOrchestrator.NormalizeAutoRunMode(context.Current.Host.AutoRunMode),
                AutoRunParameter = (context.Current.Host.AutoRunParameter ?? string.Empty).Trim(),
                AutoRunTime = DutyScheduleOrchestrator.NormalizeTimeOrThrow(context.Current.Host.AutoRunTime),
                AutoRunTriggerNotificationEnabled = context.Current.Host.AutoRunTriggerNotificationEnabled,
                DutyReminderEnabled = context.Current.Host.DutyReminderEnabled,
                DutyReminderTimes = [NormalizeDutyReminderTime(context.Current.Host.DutyReminderTime)],
                ServerPortMode = DutyServerPortModes.Normalize(context.Current.Host.ServerPortMode),
                FixedServerPort = ParseFixedServerPortOrThrow(
                    ResolveFixedServerPortTextForSave(context.Current.Host, context.LastAppliedHost),
                    DutyServerPortModes.Normalize(context.Current.Host.ServerPortMode)),
                EnableMcp = context.Current.Host.EnableMcp,
                EnableWebViewDebugLayer = context.Current.Host.EnableWebViewDebugLayer,
                ComponentRefreshTime = DutyScheduleOrchestrator.NormalizeTimeOrThrow(context.Current.Host.ComponentRefreshTime),
                NotificationDurationSeconds = Math.Clamp(context.Current.Host.NotificationDurationSeconds, 3, 15)
            };
        }

        if (backendPatch != null)
        {
            request.Changes.Backend = new DutyEditableBackendSettingsPatch
            {
                SelectedPlanId = backendPatch.SelectedPlanId,
                PlanPresets = backendPatch.PlanPresets,
                DutyRule = backendPatch.DutyRule
            };
        }

        return request;
    }

    private DutySettingsDocument CreateSettingsDocument(DutyLocalSettingsDocument localSettings)
    {
        return new DutySettingsDocument
        {
            HostVersion = Math.Max(1, localSettings.Version),
            BackendVersion = Math.Max(1, localSettings.Version),
            Host = new DutyEditableHostSettingsDocument
            {
                AutoRunMode = localSettings.Host.AutoRunMode,
                AutoRunParameter = localSettings.Host.AutoRunParameter,
                AutoRunTime = localSettings.Host.AutoRunTime,
                AutoRunTriggerNotificationEnabled = localSettings.Host.AutoRunTriggerNotificationEnabled,
                DutyReminderEnabled = localSettings.Host.DutyReminderEnabled,
                DutyReminderTimes = [.. (localSettings.Host.DutyReminderTimes ?? [])],
                AccessTokenMode = localSettings.Host.AccessTokenMode,
                StaticAccessTokenConfigured = !string.IsNullOrWhiteSpace(localSettings.Host.StaticAccessTokenEncrypted) &&
                                              !string.IsNullOrWhiteSpace(localSettings.Host.StaticAccessTokenVerifier),
                ServerPortMode = localSettings.Host.ServerPortMode,
                FixedServerPort = localSettings.Host.FixedServerPort,
                EnableMcp = localSettings.Host.EnableMcp,
                EnableWebViewDebugLayer = localSettings.Host.EnableWebViewDebugLayer,
                ComponentRefreshTime = localSettings.Host.ComponentRefreshTime,
                NotificationDurationSeconds = localSettings.Host.NotificationDurationSeconds
            },
            Backend = new DutyEditableBackendSettingsDocument
            {
                SelectedPlanId = localSettings.Backend.SelectedPlanId,
                PlanPresets = _backendModule.ClonePlanPresets(localSettings.Backend.PlanPresets),
                DutyRule = localSettings.Backend.DutyRule
            }
        };
    }

    private static DutyHostSettingsValues CreateHostValues(DutyEditableHostSettingsDocument host)
    {
        return new DutyHostSettingsValues
        {
            AutoRunMode = host.AutoRunMode,
            AutoRunParameter = host.AutoRunParameter,
            AutoRunTime = host.AutoRunTime,
            AutoRunTriggerNotificationEnabled = host.AutoRunTriggerNotificationEnabled,
            DutyReminderEnabled = host.DutyReminderEnabled,
            DutyReminderTime = (host.DutyReminderTimes ?? []).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "07:40",
            ServerPortMode = host.ServerPortMode,
            FixedServerPortText = host.FixedServerPort?.ToString() ?? string.Empty,
            EnableMcp = host.EnableMcp,
            EnableWebViewDebugLayer = host.EnableWebViewDebugLayer,
            ComponentRefreshTime = host.ComponentRefreshTime,
            NotificationDurationSeconds = host.NotificationDurationSeconds
        };
    }

    private DutyBackendConfig CreateBackendConfig(DutySettingsDocument document)
    {
        var backend = document.Backend ?? new DutyEditableBackendSettingsDocument();
        var selectedPlanId = _backendModule.NormalizeSelectedPlanId(backend.SelectedPlanId, backend.PlanPresets);
        var plans = _backendModule.NormalizePlanPresets(backend.PlanPresets);
        var selectedPlan = _backendModule.GetSelectedPlan(plans, selectedPlanId) ?? plans.First();
        var selectedModeId = _backendModule.NormalizePlanModeId(selectedPlan.ModeId);

        return new DutyBackendConfig
        {
            Version = document.BackendVersion,
            ApiKey = selectedPlan.ApiKey,
            BaseUrl = selectedPlan.BaseUrl,
            Model = selectedPlan.Model,
            ModelProfile = selectedPlan.ModelProfile,
            OrchestrationMode = string.Equals(selectedModeId, DutyBackendModeIds.Agents, StringComparison.Ordinal)
                ? "multi_agent"
                : "single_pass",
            MultiAgentExecutionMode = string.Equals(selectedModeId, DutyBackendModeIds.Agents, StringComparison.Ordinal)
                ? selectedPlan.MultiAgentExecutionMode
                : "auto",
            SinglePassStrategy = string.Equals(selectedModeId, DutyBackendModeIds.IncrementalSmall, StringComparison.Ordinal)
                ? "incremental_thinking"
                : "auto",
            ProviderHint = selectedPlan.ProviderHint,
            SelectedPlanId = selectedPlanId,
            PlanPresets = _backendModule.ClonePlanPresets(plans),
            DutyRule = backend.DutyRule ?? string.Empty
        };
    }

    private static string NormalizeDutyReminderTime(string? input)
    {
        return TimeSpan.TryParse(input, out var parsed)
            ? $"{parsed.Hours:D2}:{parsed.Minutes:D2}"
            : "07:40";
    }

    private static string ResolveFixedServerPortTextForSave(DutyHostSettingsValues current, DutyHostSettingsValues lastApplied)
    {
        return DutyMainSettingsHostModule.ResolveEffectiveFixedServerPortText(current, lastApplied);
    }

    private static int? ParseFixedServerPortOrThrow(string? text, string normalizedMode)
    {
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            if (normalizedMode == DutyServerPortModes.Fixed)
            {
                throw new InvalidOperationException("固定服务端口不能为空。");
            }

            return null;
        }

        if (!int.TryParse(trimmed, out var port))
        {
            throw new InvalidOperationException("固定服务端口必须是 1024-65535 之间的整数。");
        }

        if (port < MinServicePort || port > MaxServicePort)
        {
            throw new InvalidOperationException("固定服务端口必须在 1024-65535 范围内。");
        }

        return port;
    }

    private static string NormalizeFixedServerPortText(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return int.TryParse(normalized, out var port) ? port.ToString() : normalized;
    }
}
