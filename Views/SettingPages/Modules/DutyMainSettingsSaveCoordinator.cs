using DutyAgent.Models;
using DutyAgent.Services;

namespace DutyAgent.Views.SettingPages.Modules;

internal sealed class DutyMainSettingsSaveCoordinator
{
    private readonly DutyMainSettingsHostModule _hostModule;
    private readonly DutyMainSettingsBackendModule _backendModule;

    public DutyMainSettingsSaveCoordinator(
        DutyMainSettingsHostModule hostModule,
        DutyMainSettingsBackendModule backendModule)
    {
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

        DutyDiagnosticsLogger.Info("SettingsPage", "Applying settings from controls.",
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
            DutyDiagnosticsLogger.Info("SettingsPage", "No settings changes detected.",
                new
                {
                    traceId,
                    backendState = context.BackendLoadState.ToString()
                });
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

        DutyHostSettingsValues? appliedHost = null;
        var restartRequired = false;
        if (hostChanged)
        {
            try
            {
                var hostResult = _hostModule.Save(context.Current.Host, traceId);
                restartRequired = hostResult.RestartRequired;
                appliedHost = hostResult.AppliedValues;
            }
            catch (Exception ex)
            {
                DutyDiagnosticsLogger.Error("SettingsPage", "Host settings save failed.", ex,
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
                    HostChanged: true,
                    HostSaved: false,
                    BackendChanged: backendChanged,
                    BackendSaved: false,
                    Message: $"宿主设置保存失败：{ex.Message}",
                    MessageLevel: DutySettingsSaveMessageLevel.Error);
            }
        }

        DutyBackendConfig? appliedBackend = null;
        if (backendChanged && backendPatch != null)
        {
            try
            {
                await _backendModule.SaveAsync(
                    backendPatch,
                    requestSource: "host_settings",
                    traceId: traceId,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                appliedBackend = await _backendModule.LoadAsync(
                    requestSource: "host_settings_verify",
                    traceId: traceId,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (!_backendModule.MatchesSettings(context.Current.Backend, appliedBackend))
                {
                    return new DutySettingsSaveOutcome(
                        Success: false,
                        NoChanges: false,
                        RestartRequired: restartRequired,
                        HostChanged: hostChanged,
                        HostSaved: appliedHost != null,
                        BackendChanged: true,
                        BackendSaved: false,
                        Message: appliedHost != null
                            ? "宿主设置已保存，但后端配置回读校验失败，已重新加载当前后端值。"
                            : "后端配置回读校验失败，已重新加载当前后端值。",
                        MessageLevel: appliedHost != null
                            ? DutySettingsSaveMessageLevel.Warning
                            : DutySettingsSaveMessageLevel.Error,
                        AppliedHost: appliedHost,
                        AppliedBackend: appliedBackend);
                }
            }
            catch (Exception ex)
            {
                DutyDiagnosticsLogger.Error("SettingsPage", "Backend settings save failed.", ex,
                    new
                    {
                        traceId,
                        hostSaved = appliedHost != null,
                        backendState = context.BackendLoadState.ToString(),
                        backendError = context.BackendErrorMessage ?? string.Empty
                    });
                return new DutySettingsSaveOutcome(
                    Success: false,
                    NoChanges: false,
                    RestartRequired: restartRequired,
                    HostChanged: hostChanged,
                    HostSaved: appliedHost != null,
                    BackendChanged: true,
                    BackendSaved: false,
                    Message: appliedHost != null
                        ? $"宿主设置已保存，但后端设置失败：{ex.Message}"
                        : $"后端设置失败：{ex.Message}",
                    MessageLevel: appliedHost != null
                        ? DutySettingsSaveMessageLevel.Warning
                        : DutySettingsSaveMessageLevel.Error,
                    AppliedHost: appliedHost);
            }
        }
        else if (context.BackendLoadState != DutyBackendConfigLoadState.Loaded)
        {
            DutyDiagnosticsLogger.Warn("SettingsPage", "Skipped backend save because backend config is not loaded.",
                new
                {
                    traceId,
                    backendState = context.BackendLoadState.ToString(),
                    backendError = context.BackendErrorMessage ?? string.Empty
                });
        }

        return new DutySettingsSaveOutcome(
            Success: true,
            NoChanges: false,
            RestartRequired: restartRequired,
            HostChanged: hostChanged,
            HostSaved: appliedHost != null,
            BackendChanged: backendChanged,
            BackendSaved: appliedBackend != null,
            Message: BuildSuccessMessage(hostChanged, backendChanged, restartRequired, appliedHost),
            MessageLevel: DutySettingsSaveMessageLevel.Info,
            AppliedHost: appliedHost,
            AppliedBackend: appliedBackend);
    }

    private static string BuildSuccessMessage(
        bool hostChanged,
        bool backendChanged,
        bool restartRequired,
        DutyHostSettingsValues? appliedHost)
    {
        if (hostChanged && backendChanged)
        {
            return restartRequired ? "设置已保存，重启后启用相关宿主功能。" : "设置已保存。";
        }

        if (backendChanged)
        {
            return "后端设置已保存。";
        }

        if (appliedHost != null)
        {
            return restartRequired ? "宿主设置已保存，调试层 / MCP 将在重启后生效。" : "宿主设置已保存。";
        }

        return "设置已保存。";
    }
}
