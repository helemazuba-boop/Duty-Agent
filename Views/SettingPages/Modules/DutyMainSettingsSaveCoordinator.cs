using System;
using System.Linq;
using DutyAgent.Models;
using DutyAgent.Services;

namespace DutyAgent.Views.SettingPages.Modules;

internal sealed class DutyMainSettingsSaveCoordinator
{
    private readonly DutyScheduleOrchestrator _service;
    private readonly DutyMainSettingsHostModule _hostModule;
    private readonly DutyMainSettingsBackendModule _backendModule;

    public DutyMainSettingsSaveCoordinator(
        DutyScheduleOrchestrator service,
        DutyMainSettingsHostModule hostModule,
        DutyMainSettingsBackendModule backendModule)
    {
        _service = service;
        _hostModule = hostModule;
        _backendModule = backendModule;
    }

    public async Task<DutySettingsSaveOutcome> ApplyAsync(
        DutySettingsSaveContext context,
        CancellationToken cancellationToken = default)
    {
        var traceId = DutyDiagnosticsLogger.CreateTraceId("settings");
        var hostChanged = _hostModule.HasChanges(context.Current.Host, context.LastAppliedHost);
        var backendPatch = context.BackendLoadState == DutyBackendConfigLoadState.Loaded
            ? _backendModule.TryBuildPatch(context.Current.Backend, context.LastAppliedBackend)
            : null;
        var backendChanged = backendPatch != null;

        DutyDiagnosticsLogger.Info("SettingsPage", "Applying unified settings from controls.",
            new
            {
                traceId,
                hostChanged,
                backendChanged,
                backendState = context.BackendLoadState.ToString(),
                backendError = context.BackendErrorMessage ?? string.Empty,
                backendPatch = _backendModule.SummarizePatch(backendPatch)
            });

        if (!hostChanged && !backendChanged)
        {
            return new DutySettingsSaveOutcome(
                Success: true,
                NoChanges: true,
                RestartRequired: false,
                HostChanged: false,
                HostSaved: false,
                BackendChanged: false,
                BackendSaved: false,
                Message: string.Empty,
                MessageLevel: DutySettingsSaveMessageLevel.Info);
        }

        var request = BuildPatchRequest(context, hostChanged, backendPatch);
        try
        {
            var mutation = await _service.PatchSettingsAsync(
                request,
                requestSource: "host_settings",
                traceId: traceId,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var appliedHost = mutation.Document?.Host != null ? CreateHostValues(mutation.Document.Host) : null;
            var appliedBackend = mutation.Document?.Backend != null
                ? CreateBackendConfig(mutation.Document)
                : null;

            return new DutySettingsSaveOutcome(
                Success: mutation.Success,
                NoChanges: false,
                RestartRequired: mutation.RestartRequired,
                HostChanged: hostChanged,
                HostSaved: mutation.Applied.Host != null,
                BackendChanged: backendChanged,
                BackendSaved: mutation.Applied.Backend != null,
                Message: string.IsNullOrWhiteSpace(mutation.Message)
                    ? (mutation.Success ? "设置已保存。" : "设置保存失败。")
                    : mutation.Message,
                MessageLevel: mutation.Success
                    ? DutySettingsSaveMessageLevel.Info
                    : mutation.Warnings.Count > 0
                        ? DutySettingsSaveMessageLevel.Warning
                        : DutySettingsSaveMessageLevel.Error,
                AppliedDocument: mutation.Document,
                AppliedHost: appliedHost,
                AppliedBackend: appliedBackend);
        }
        catch (Exception ex)
        {
            DutyDiagnosticsLogger.Error("SettingsPage", "Unified settings save threw exception.", ex,
                new
                {
                    traceId,
                    backendState = context.BackendLoadState.ToString(),
                    backendError = context.BackendErrorMessage ?? string.Empty
                });
            return new DutySettingsSaveOutcome(
                Success: false,
                NoChanges: false,
                RestartRequired: false,
                HostChanged: hostChanged,
                HostSaved: false,
                BackendChanged: backendChanged,
                BackendSaved: false,
                Message: $"设置保存失败：{ex.Message}",
                MessageLevel: DutySettingsSaveMessageLevel.Error);
        }
    }

    private DutySettingsPatchRequest BuildPatchRequest(
        DutySettingsSaveContext context,
        bool hostChanged,
        DutyBackendConfigPatch? backendPatch)
    {
        var lastLoadedDocument = context.LastLoadedDocument ?? new DutySettingsDocument();
        var request = new DutySettingsPatchRequest
        {
            Expected = new DutySettingsExpectedVersions
            {
                HostVersion = lastLoadedDocument.HostVersion,
                BackendVersion = lastLoadedDocument.BackendVersion
            },
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

    private DutyHostSettingsValues CreateHostValues(DutyEditableHostSettingsDocument host)
    {
        return new DutyHostSettingsValues
        {
            AutoRunMode = host.AutoRunMode,
            AutoRunParameter = host.AutoRunParameter,
            AutoRunTime = host.AutoRunTime,
            AutoRunTriggerNotificationEnabled = host.AutoRunTriggerNotificationEnabled,
            DutyReminderEnabled = host.DutyReminderEnabled,
            DutyReminderTime = (host.DutyReminderTimes ?? []).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "07:40",
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
            OrchestrationMode = string.Equals(selectedModeId, DutyBackendModeIds.Campus6Agent, StringComparison.Ordinal)
                ? "multi_agent"
                : "single_pass",
            MultiAgentExecutionMode = string.Equals(selectedModeId, DutyBackendModeIds.Campus6Agent, StringComparison.Ordinal)
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
}
