using System.Text.Json;
using DutyAgent.Models;

namespace DutyAgent.Services;

public interface IDutySettingsRepository
{
    event EventHandler<DutySettingsChangedEventArgs>? SettingsChanged;

    DutyLocalSettingsDocument LoadLocalSettings();
    DutyHostRuntimeState LoadHostRuntimeState();
    DutySettingsDocument LoadSettingsDocument();
    DutyConfig LoadProjectedHostConfig();
    DutyLocalSettingsDocument SavePatch(DutySettingsPatchRequest patch);
    DutyHostAccessSecurityMutationResult SaveHostAccessSecurity(DutyHostAccessSecuritySaveRequest request);
    DutyConfig ReplaceFromProjectedConfig(DutyConfig projectedConfig);
}

public sealed class DutySettingsChangedEventArgs : EventArgs
{
    public DutySettingsChangedEventArgs(
        DutyLocalSettingsDocument settings,
        DutyHostRuntimeState hostState,
        DutyConfig projectedHostConfig,
        bool hostSettingsChanged,
        bool backendSettingsChanged,
        bool runtimeStateChanged)
    {
        Settings = settings;
        HostState = hostState;
        ProjectedHostConfig = projectedHostConfig;
        HostSettingsChanged = hostSettingsChanged;
        BackendSettingsChanged = backendSettingsChanged;
        RuntimeStateChanged = runtimeStateChanged;
    }

    public DutyLocalSettingsDocument Settings { get; }
    public DutyHostRuntimeState HostState { get; }
    public DutyConfig ProjectedHostConfig { get; }
    public bool HostSettingsChanged { get; }
    public bool BackendSettingsChanged { get; }
    public bool RuntimeStateChanged { get; }
}

public sealed partial class DutySettingsRepository : IDutySettingsRepository
{
    private const string DefaultDutyReminderTime = "07:40";
    private const string DefaultBaseUrl = "https://integrate.api.nvidia.com/v1";
    private const string DefaultModel = "moonshotai/kimi-k2-thinking";
    private const int MinServicePort = 1024;
    private const int MaxServicePort = 65535;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly DutyPluginPaths _pluginPaths;
    private readonly DutySettingsTraceService _settingsTrace;
    private readonly object _gate = new();

    public DutySettingsRepository(DutyPluginPaths pluginPaths, DutySettingsTraceService settingsTrace)
    {
        _pluginPaths = pluginPaths;
        _settingsTrace = settingsTrace;
        EnsureInitialized();
    }

    public event EventHandler<DutySettingsChangedEventArgs>? SettingsChanged;

    public DutyLocalSettingsDocument LoadLocalSettings()
    {
        lock (_gate)
        {
            return CloneLocalSettings(LoadLocalSettingsUnlocked());
        }
    }

    public DutyHostRuntimeState LoadHostRuntimeState()
    {
        lock (_gate)
        {
            return CloneHostRuntimeState(LoadHostRuntimeStateUnlocked());
        }
    }

    public DutySettingsDocument LoadSettingsDocument()
    {
        lock (_gate)
        {
            return CreateSettingsDocument(LoadLocalSettingsUnlocked());
        }
    }

    public DutyConfig LoadProjectedHostConfig()
    {
        lock (_gate)
        {
            return CreateProjectedHostConfig(LoadLocalSettingsUnlocked().Host, LoadHostRuntimeStateUnlocked());
        }
    }

    public DutyLocalSettingsDocument SavePatch(DutySettingsPatchRequest patch)
    {
        ArgumentNullException.ThrowIfNull(patch);

        DutySettingsChangedEventArgs? changedArgs = null;
        DutyLocalSettingsDocument savedDocument;

        lock (_gate)
        {
            var currentSettings = LoadLocalSettingsUnlocked();
            var runtimeState = LoadHostRuntimeStateUnlocked();
            var nextSettings = CloneLocalSettings(currentSettings);
            var nextHost = ClonePersistedHostSettings(nextSettings.Host);
            var nextBackend = CloneBackendDocument(nextSettings.Backend);

            ApplyHostPatch(nextHost, patch.Changes.Host);
            ApplyBackendPatch(nextBackend, patch.Changes.Backend);

            var hostSettingsChanged = !JsonContentEquals(currentSettings.Host, nextHost);
            var backendSettingsChanged = !JsonContentEquals(currentSettings.Backend, nextBackend);

            if (!hostSettingsChanged && !backendSettingsChanged)
            {
                _settingsTrace.Info("settings_patch_no_changes", new
                {
                    version = currentSettings.Version
                });
                return CloneLocalSettings(currentSettings);
            }

            nextSettings.Host = nextHost;
            nextSettings.Backend = nextBackend;
            nextSettings.Version = Math.Max(1, currentSettings.Version) + 1;
            nextSettings.SavedAtUtc = DateTimeOffset.UtcNow;

            _settingsTrace.Info("settings_json_write_started", new
            {
                previous_version = currentSettings.Version,
                next_version = nextSettings.Version,
                host_settings_changed = hostSettingsChanged,
                backend_settings_changed = backendSettingsChanged,
                selected_plan_id = nextSettings.Backend.SelectedPlanId
            });
            WriteJsonAtomicallyTracked(_pluginPaths.SettingsPath, nextSettings, "settings_json", new
            {
                previous_version = currentSettings.Version,
                next_version = nextSettings.Version,
                selected_plan_id = nextSettings.Backend.SelectedPlanId
            });
            if (hostSettingsChanged)
            {
                WriteCompatibilityHostConfig(nextSettings, runtimeState);
            }

            savedDocument = CloneLocalSettings(nextSettings);
            changedArgs = CreateChangedEventArgs(savedDocument, runtimeState, hostSettingsChanged, backendSettingsChanged, runtimeStateChanged: false);
        }

        RaiseSettingsChanged(changedArgs);
        return savedDocument;
    }

    public DutyHostAccessSecurityMutationResult SaveHostAccessSecurity(DutyHostAccessSecuritySaveRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        DutySettingsChangedEventArgs? changedArgs = null;
        DutyHostAccessSecurityMutationResult result;

        lock (_gate)
        {
            var currentSettings = LoadLocalSettingsUnlocked();
            var runtimeState = LoadHostRuntimeStateUnlocked();
            var nextSettings = CloneLocalSettings(currentSettings);
            var nextHost = ClonePersistedHostSettings(nextSettings.Host);
            var requestedMode = DutyAccessTokenModes.Normalize(request.AccessTokenMode);
            var newStaticAccessToken = NormalizeOptionalSecret(request.NewStaticAccessTokenPlaintext);
            var currentStaticConfigured = HasStaticAccessTokenConfigured(currentSettings.Host);

            if (request.ClearStaticAccessToken)
            {
                nextHost.StaticAccessTokenEncrypted = string.Empty;
                nextHost.StaticAccessTokenVerifier = string.Empty;
                requestedMode = DutyAccessTokenModes.Dynamic;
            }
            else
            {
                if (requestedMode == DutyAccessTokenModes.Dynamic && newStaticAccessToken != null)
                {
                    throw new InvalidOperationException("动态模式下不会隐式保存静态 token。请切换到静态模式后再应用。");
                }

                if (newStaticAccessToken != null)
                {
                    nextHost.StaticAccessTokenEncrypted = SecurityHelper.EncryptString(newStaticAccessToken);
                    nextHost.StaticAccessTokenVerifier = SecurityHelper.CreatePasswordVerifier(newStaticAccessToken);
                }

                if (requestedMode == DutyAccessTokenModes.Static && !HasStaticAccessTokenConfigured(nextHost))
                {
                    throw new InvalidOperationException("切换到静态模式前，请先输入静态 token，或保留已配置的静态 token。");
                }
            }

            nextHost.AccessTokenMode = requestedMode;
            nextHost = NormalizePersistedHostSettings(nextHost);

            var hostSettingsChanged = !JsonContentEquals(currentSettings.Host, nextHost);
            if (!hostSettingsChanged)
            {
                result = new DutyHostAccessSecurityMutationResult
                {
                    Success = true,
                    NoChanges = true,
                    RestartRequired = false,
                    Message = "访问鉴权未变更。",
                    Document = CreateSettingsDocument(currentSettings)
                };
                return result;
            }

            nextSettings.Host = nextHost;
            nextSettings.Version = Math.Max(1, currentSettings.Version) + 1;
            nextSettings.SavedAtUtc = DateTimeOffset.UtcNow;

            WriteJsonAtomicallyTracked(_pluginPaths.SettingsPath, nextSettings, "settings_json_host_security", new
            {
                previous_version = currentSettings.Version,
                next_version = nextSettings.Version,
                access_token_mode = nextHost.AccessTokenMode,
                static_access_token_configured = HasStaticAccessTokenConfigured(nextHost)
            });
            WriteCompatibilityHostConfig(nextSettings, runtimeState);

            var savedDocument = CloneLocalSettings(nextSettings);
            changedArgs = CreateChangedEventArgs(savedDocument, runtimeState, hostSettingsChanged: true, backendSettingsChanged: false, runtimeStateChanged: false);
            result = new DutyHostAccessSecurityMutationResult
            {
                Success = true,
                NoChanges = false,
                RestartRequired = true,
                Message = BuildHostAccessSecurityMessage(request, currentStaticConfigured, nextHost, newStaticAccessToken != null),
                Document = CreateSettingsDocument(savedDocument)
            };
        }

        RaiseSettingsChanged(changedArgs);
        return result;
    }

    public DutyConfig ReplaceFromProjectedConfig(DutyConfig projectedConfig)
    {
        ArgumentNullException.ThrowIfNull(projectedConfig);

        DutySettingsChangedEventArgs? changedArgs = null;
        DutyConfig projectedResult;

        lock (_gate)
        {
            var currentSettings = LoadLocalSettingsUnlocked();
            var currentRuntimeState = LoadHostRuntimeStateUnlocked();
            var nextSettings = CloneLocalSettings(currentSettings);
            var nextHost = CreatePersistedHostSettings(projectedConfig, currentSettings.Host);
            var nextRuntimeState = CreateRuntimeState(projectedConfig);

            var hostSettingsChanged = !JsonContentEquals(currentSettings.Host, nextHost);
            var runtimeStateChanged = !JsonContentEquals(currentRuntimeState, nextRuntimeState);

            if (!hostSettingsChanged && !runtimeStateChanged)
            {
                return CreateProjectedHostConfig(currentSettings.Host, currentRuntimeState);
            }

            nextSettings.Host = nextHost;
            if (hostSettingsChanged)
            {
                nextSettings.Version = Math.Max(1, currentSettings.Version) + 1;
                nextSettings.SavedAtUtc = DateTimeOffset.UtcNow;
                WriteJsonAtomicallyTracked(_pluginPaths.SettingsPath, nextSettings, "settings_json", new
                {
                    previous_version = currentSettings.Version,
                    next_version = nextSettings.Version,
                    host_settings_changed = true
                });
            }

            if (runtimeStateChanged)
            {
                WriteJsonAtomicallyTracked(_pluginPaths.HostStatePath, nextRuntimeState, "host_state_json", new
                {
                    ai_consecutive_failures = nextRuntimeState.AiConsecutiveFailures,
                    last_auto_run_date = nextRuntimeState.LastAutoRunDate
                });
            }

            WriteCompatibilityHostConfig(nextSettings, nextRuntimeState);

            projectedResult = CreateProjectedHostConfig(nextSettings.Host, nextRuntimeState);
            changedArgs = CreateChangedEventArgs(nextSettings, nextRuntimeState, hostSettingsChanged, backendSettingsChanged: false, runtimeStateChanged);
        }

        RaiseSettingsChanged(changedArgs);
        return projectedResult;
    }

    private void EnsureInitialized()
    {
        lock (_gate)
        {
            var settings = LoadLocalSettingsUnlocked();
            var runtimeState = LoadHostRuntimeStateUnlocked();
            if (!File.Exists(_pluginPaths.HostConfigPath))
            {
                WriteCompatibilityHostConfig(settings, runtimeState);
            }
        }
    }

    private DutyLocalSettingsDocument LoadLocalSettingsUnlocked()
    {
        if (File.Exists(_pluginPaths.SettingsPath))
        {
            var parsed = ReadJsonOrDefault(_pluginPaths.SettingsPath, CreateDefaultLocalSettings());
            return NormalizeLocalSettings(parsed);
        }

        _settingsTrace.Warn("settings_migration_started", new
        {
            settings_exists = File.Exists(_pluginPaths.SettingsPath),
            host_config_exists = File.Exists(_pluginPaths.HostConfigPath),
            backend_config_exists = File.Exists(_pluginPaths.ConfigPath),
            draft_exists = File.Exists(_pluginPaths.SettingsDraftPath)
        });
        var migrated = MigrateLocalSettingsUnlocked();
        WriteJsonAtomicallyTracked(_pluginPaths.SettingsPath, migrated.Settings, "settings_json_migrated", new
        {
            version = migrated.Settings.Version,
            selected_plan_id = migrated.Settings.Backend.SelectedPlanId
        });
        WriteJsonAtomicallyTracked(_pluginPaths.HostStatePath, migrated.RuntimeState, "host_state_json_migrated", null);
        WriteCompatibilityHostConfig(migrated.Settings, migrated.RuntimeState);
        TryDeleteFile(_pluginPaths.SettingsDraftPath);
        _settingsTrace.Info("settings_migration_completed", new
        {
            version = migrated.Settings.Version,
            selected_plan_id = migrated.Settings.Backend.SelectedPlanId,
            host_state = _settingsTrace.CaptureFileSnapshot(_pluginPaths.HostStatePath),
            settings_file = _settingsTrace.CaptureFileSnapshot(_pluginPaths.SettingsPath)
        });
        return CloneLocalSettings(migrated.Settings);
    }

    private DutyHostRuntimeState LoadHostRuntimeStateUnlocked()
    {
        if (File.Exists(_pluginPaths.HostStatePath))
        {
            var parsed = ReadJsonOrDefault(_pluginPaths.HostStatePath, new DutyHostRuntimeState());
            return NormalizeHostRuntimeState(parsed);
        }

        var state = new DutyHostRuntimeState();
        WriteJsonAtomicallyTracked(_pluginPaths.HostStatePath, state, "host_state_json_initialized", null);
        return CloneHostRuntimeState(state);
    }

    private (DutyLocalSettingsDocument Settings, DutyHostRuntimeState RuntimeState) MigrateLocalSettingsUnlocked()
    {
        var settings = CreateDefaultLocalSettings();
        var runtimeState = new DutyHostRuntimeState();

        if (File.Exists(_pluginPaths.HostConfigPath))
        {
            var legacyHost = ReadJsonOrDefault(_pluginPaths.HostConfigPath, new DutyConfig());
            settings.Version = Math.Max(settings.Version, Math.Max(1, legacyHost.Version));
            settings.Host = CreatePersistedHostSettings(legacyHost, settings.Host);
            runtimeState = CreateRuntimeState(legacyHost);
        }

        if (File.Exists(_pluginPaths.ConfigPath))
        {
            var legacyBackend = ReadJsonOrDefault(_pluginPaths.ConfigPath, new DutyBackendConfig());
            settings.Version = Math.Max(settings.Version, Math.Max(1, legacyBackend.Version));
            settings.Backend = CreateBackendDocument(legacyBackend);
        }

        if (File.Exists(_pluginPaths.SettingsDraftPath))
        {
            var draft = ReadJsonOrDefault<DutySettingsDraftDocument?>(_pluginPaths.SettingsDraftPath, null);
            if (draft != null)
            {
                ApplyDraft(settings, draft);
            }
        }

        settings.Host = NormalizePersistedHostSettings(settings.Host);
        settings.Backend = NormalizeBackendDocument(settings.Backend);
        settings.SavedAtUtc = DateTimeOffset.UtcNow;
        runtimeState = NormalizeHostRuntimeState(runtimeState);
        return (settings, runtimeState);
    }

    private static DutyLocalSettingsDocument CreateDefaultLocalSettings()
    {
        return new DutyLocalSettingsDocument
        {
            Version = 1,
            SavedAtUtc = DateTimeOffset.UtcNow,
            Host = new DutyPersistedHostSettings(),
            Backend = NormalizeBackendDocument(new DutyEditableBackendSettingsDocument())
        };
    }

    private static void ApplyDraft(DutyLocalSettingsDocument settings, DutySettingsDraftDocument draft)
    {
        var hostPatch = draft.Host ?? new DutyEditableHostSettingsDocument();
        settings.Host.AutoRunMode = NormalizeAutoRunMode(hostPatch.AutoRunMode);
        settings.Host.AutoRunParameter = (hostPatch.AutoRunParameter ?? settings.Host.AutoRunParameter).Trim();
        settings.Host.AutoRunTime = NormalizeTime(hostPatch.AutoRunTime, settings.Host.AutoRunTime);
        settings.Host.AutoRunTriggerNotificationEnabled = hostPatch.AutoRunTriggerNotificationEnabled;
        settings.Host.DutyReminderEnabled = hostPatch.DutyReminderEnabled;
        settings.Host.DutyReminderTimes = [NormalizeReminderTime((hostPatch.DutyReminderTimes ?? []).FirstOrDefault())];
        settings.Host.ServerPortMode = DutyServerPortModes.Normalize(hostPatch.ServerPortMode);
        settings.Host.FixedServerPort = NormalizeServicePort(hostPatch.FixedServerPort);
        settings.Host.EnableMcp = hostPatch.EnableMcp;
        settings.Host.EnableWebViewDebugLayer = hostPatch.EnableWebViewDebugLayer;
        settings.Host.ComponentRefreshTime = NormalizeTime(hostPatch.ComponentRefreshTime, settings.Host.ComponentRefreshTime);
        settings.Host.NotificationDurationSeconds = Math.Clamp(hostPatch.NotificationDurationSeconds, 3, 15);

        if (draft.Backend != null)
        {
            settings.Backend = NormalizeBackendDocument(draft.Backend);
        }
    }

    private static void ApplyHostPatch(DutyPersistedHostSettings host, DutyEditableHostSettingsPatch? patch)
    {
        if (patch == null)
        {
            return;
        }

        if (patch.AutoRunMode != null)
        {
            host.AutoRunMode = NormalizeAutoRunMode(patch.AutoRunMode);
        }

        if (patch.AutoRunParameter != null)
        {
            host.AutoRunParameter = patch.AutoRunParameter.Trim();
        }

        if (patch.AutoRunTime != null)
        {
            host.AutoRunTime = NormalizeTime(patch.AutoRunTime, host.AutoRunTime);
        }

        if (patch.AutoRunTriggerNotificationEnabled.HasValue)
        {
            host.AutoRunTriggerNotificationEnabled = patch.AutoRunTriggerNotificationEnabled.Value;
        }

        if (patch.DutyReminderEnabled.HasValue)
        {
            host.DutyReminderEnabled = patch.DutyReminderEnabled.Value;
        }

        if (patch.DutyReminderTimes != null)
        {
            host.DutyReminderTimes = [NormalizeReminderTime(patch.DutyReminderTimes.FirstOrDefault())];
        }

        if (patch.ServerPortMode != null)
        {
            host.ServerPortMode = DutyServerPortModes.Normalize(patch.ServerPortMode);
        }

        if (patch.FixedServerPort.HasValue)
        {
            host.FixedServerPort = NormalizeServicePort(patch.FixedServerPort);
        }

        if (patch.EnableMcp.HasValue)
        {
            host.EnableMcp = patch.EnableMcp.Value;
        }

        if (patch.EnableWebViewDebugLayer.HasValue)
        {
            host.EnableWebViewDebugLayer = patch.EnableWebViewDebugLayer.Value;
        }

        if (patch.ComponentRefreshTime != null)
        {
            host.ComponentRefreshTime = NormalizeTime(patch.ComponentRefreshTime, host.ComponentRefreshTime);
        }

        if (patch.NotificationDurationSeconds.HasValue)
        {
            host.NotificationDurationSeconds = Math.Clamp(patch.NotificationDurationSeconds.Value, 3, 15);
        }

        host.DutyReminderTimes = NormalizeReminderTimes(host.DutyReminderTimes);
    }

    private static void ApplyBackendPatch(DutyEditableBackendSettingsDocument backend, DutyEditableBackendSettingsPatch? patch)
    {
        if (patch == null)
        {
            return;
        }

        if (patch.SelectedPlanId != null)
        {
            backend.SelectedPlanId = patch.SelectedPlanId.Trim();
        }

        if (patch.PlanPresets != null)
        {
            backend.PlanPresets = ClonePlanPresets(patch.PlanPresets);
        }

        if (patch.DutyRule != null)
        {
            backend.DutyRule = patch.DutyRule;
        }

        var normalized = NormalizeBackendDocument(backend);
        backend.SelectedPlanId = normalized.SelectedPlanId;
        backend.PlanPresets = normalized.PlanPresets;
        backend.DutyRule = normalized.DutyRule;
    }

    private static DutyLocalSettingsDocument NormalizeLocalSettings(DutyLocalSettingsDocument document)
    {
        return new DutyLocalSettingsDocument
        {
            Version = Math.Max(1, document.Version),
            SavedAtUtc = document.SavedAtUtc == default ? DateTimeOffset.UtcNow : document.SavedAtUtc.ToUniversalTime(),
            Host = NormalizePersistedHostSettings(document.Host),
            Backend = NormalizeBackendDocument(document.Backend)
        };
    }

    private static DutyPersistedHostSettings NormalizePersistedHostSettings(DutyPersistedHostSettings? host)
    {
        host ??= new DutyPersistedHostSettings();
        return new DutyPersistedHostSettings
        {
            PythonPath = string.IsNullOrWhiteSpace(host.PythonPath) ? new DutyPersistedHostSettings().PythonPath : host.PythonPath.Trim(),
            AutoRunMode = NormalizeAutoRunMode(host.AutoRunMode),
            AutoRunParameter = (host.AutoRunParameter ?? string.Empty).Trim() is { Length: > 0 } parameter ? parameter : "Monday",
            AccessTokenMode = DutyAccessTokenModes.Normalize(host.AccessTokenMode),
            StaticAccessTokenEncrypted = NormalizePersistedSecret(host.StaticAccessTokenEncrypted),
            StaticAccessTokenVerifier = NormalizePersistedSecret(host.StaticAccessTokenVerifier),
            ServerPortMode = DutyServerPortModes.Normalize(host.ServerPortMode),
            FixedServerPort = NormalizeServicePort(host.FixedServerPort),
            EnableMcp = host.EnableMcp,
            EnableWebViewDebugLayer = host.EnableWebViewDebugLayer,
            AutoRunTime = NormalizeTime(host.AutoRunTime, "08:00"),
            AutoRunTriggerNotificationEnabled = host.AutoRunTriggerNotificationEnabled,
            AutoRunRetryTimes = Math.Max(0, host.AutoRunRetryTimes),
            ComponentRefreshTime = NormalizeTime(host.ComponentRefreshTime, "08:00"),
            NotificationDurationSeconds = Math.Clamp(host.NotificationDurationSeconds, 3, 15),
            DutyReminderEnabled = host.DutyReminderEnabled,
            DutyReminderTimes = NormalizeReminderTimes(host.DutyReminderTimes)
        };
    }

    private static DutyHostRuntimeState NormalizeHostRuntimeState(DutyHostRuntimeState? state)
    {
        state ??= new DutyHostRuntimeState();
        return new DutyHostRuntimeState
        {
            AiConsecutiveFailures = Math.Max(0, state.AiConsecutiveFailures),
            LastAutoRunDate = (state.LastAutoRunDate ?? string.Empty).Trim()
        };
    }

    private static DutyEditableBackendSettingsDocument NormalizeBackendDocument(DutyEditableBackendSettingsDocument? backend)
    {
        backend ??= new DutyEditableBackendSettingsDocument();
        var presets = ClonePlanPresets(backend.PlanPresets);
        if (presets.Count == 0)
        {
            presets = CreateDefaultPlanPresets();
        }

        for (var i = 0; i < presets.Count; i++)
        {
            var preset = presets[i];
            preset.Id = string.IsNullOrWhiteSpace(preset.Id) ? $"plan-{i + 1}" : preset.Id.Trim();
            preset.Name = string.IsNullOrWhiteSpace(preset.Name) ? preset.Id : preset.Name.Trim();
            preset.ModeId = NormalizePlanModeId(preset.ModeId);
            preset.ApiKey = (preset.ApiKey ?? string.Empty).Trim();
            preset.BaseUrl = string.IsNullOrWhiteSpace(preset.BaseUrl) ? DefaultBaseUrl : preset.BaseUrl.Trim();
            preset.Model = string.IsNullOrWhiteSpace(preset.Model) ? DefaultModel : preset.Model.Trim();
            preset.ModelProfile = NormalizeModelProfile(preset.ModelProfile);
            preset.ProviderHint = (preset.ProviderHint ?? string.Empty).Trim();
            preset.MultiAgentExecutionMode = string.Equals(preset.ModeId, DutyBackendModeIds.Campus6Agent, StringComparison.Ordinal)
                ? NormalizeMultiAgentExecutionMode(preset.MultiAgentExecutionMode)
                : "auto";
        }

        var selectedPlanId = (backend.SelectedPlanId ?? string.Empty).Trim();
        if (!presets.Any(x => string.Equals(x.Id, selectedPlanId, StringComparison.Ordinal)))
        {
            selectedPlanId = presets[0].Id;
        }

        return new DutyEditableBackendSettingsDocument
        {
            SelectedPlanId = selectedPlanId,
            PlanPresets = presets,
            DutyRule = backend.DutyRule ?? string.Empty
        };
    }

    private static DutyConfig CreateProjectedHostConfig(DutyPersistedHostSettings host, DutyHostRuntimeState runtimeState)
    {
        return new DutyConfig
        {
            Version = 1,
            PythonPath = host.PythonPath,
            AutoRunMode = host.AutoRunMode,
            AutoRunParameter = host.AutoRunParameter,
            AccessTokenMode = host.AccessTokenMode,
            StaticAccessTokenVerifier = host.StaticAccessTokenVerifier,
            EnableMcp = host.EnableMcp,
            EnableWebViewDebugLayer = host.EnableWebViewDebugLayer,
            AutoRunTime = host.AutoRunTime,
            AutoRunTriggerNotificationEnabled = host.AutoRunTriggerNotificationEnabled,
            AutoRunRetryTimes = host.AutoRunRetryTimes,
            AiConsecutiveFailures = runtimeState.AiConsecutiveFailures,
            LastAutoRunDate = runtimeState.LastAutoRunDate,
            ComponentRefreshTime = host.ComponentRefreshTime,
            NotificationDurationSeconds = host.NotificationDurationSeconds,
            DutyReminderEnabled = host.DutyReminderEnabled,
            DutyReminderTimes = [.. NormalizeReminderTimes(host.DutyReminderTimes)]
        };
    }

    private static DutyPersistedHostSettings CreatePersistedHostSettings(DutyConfig config, DutyPersistedHostSettings? fallback = null)
    {
        var next = NormalizePersistedHostSettings(fallback);
        next.PythonPath = string.IsNullOrWhiteSpace(config.PythonPath) ? next.PythonPath : config.PythonPath.Trim();
        next.AutoRunMode = NormalizeAutoRunMode(config.AutoRunMode);
        next.AutoRunParameter = (config.AutoRunParameter ?? next.AutoRunParameter).Trim();
        next.AccessTokenMode = string.IsNullOrWhiteSpace(config.AccessTokenMode)
            ? next.AccessTokenMode
            : DutyAccessTokenModes.Normalize(config.AccessTokenMode);
        next.StaticAccessTokenVerifier = string.IsNullOrWhiteSpace(config.StaticAccessTokenVerifier)
            ? next.StaticAccessTokenVerifier
            : NormalizePersistedSecret(config.StaticAccessTokenVerifier);
        next.EnableMcp = config.EnableMcp;
        next.EnableWebViewDebugLayer = config.EnableWebViewDebugLayer;
        next.AutoRunTime = NormalizeTime(config.AutoRunTime, next.AutoRunTime);
        next.AutoRunTriggerNotificationEnabled = config.AutoRunTriggerNotificationEnabled;
        next.AutoRunRetryTimes = Math.Max(0, config.AutoRunRetryTimes);
        next.ComponentRefreshTime = NormalizeTime(config.ComponentRefreshTime, next.ComponentRefreshTime);
        next.NotificationDurationSeconds = Math.Clamp(config.NotificationDurationSeconds, 3, 15);
        next.DutyReminderEnabled = config.DutyReminderEnabled;
        next.DutyReminderTimes = NormalizeReminderTimes(config.DutyReminderTimes);
        return next;
    }

    private static DutyHostRuntimeState CreateRuntimeState(DutyConfig config)
    {
        return new DutyHostRuntimeState
        {
            AiConsecutiveFailures = Math.Max(0, config.AiConsecutiveFailures),
            LastAutoRunDate = (config.LastAutoRunDate ?? string.Empty).Trim()
        };
    }

    private static DutyEditableBackendSettingsDocument CreateBackendDocument(DutyBackendConfig config)
    {
        return NormalizeBackendDocument(new DutyEditableBackendSettingsDocument
        {
            SelectedPlanId = config.SelectedPlanId,
            PlanPresets = ClonePlanPresets(config.PlanPresets),
            DutyRule = config.DutyRule
        });
    }

    private static DutySettingsDocument CreateSettingsDocument(DutyLocalSettingsDocument localSettings)
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
                DutyReminderTimes = [.. NormalizeReminderTimes(localSettings.Host.DutyReminderTimes)],
                AccessTokenMode = localSettings.Host.AccessTokenMode,
                StaticAccessTokenConfigured = HasStaticAccessTokenConfigured(localSettings.Host),
                ServerPortMode = localSettings.Host.ServerPortMode,
                FixedServerPort = localSettings.Host.FixedServerPort,
                EnableMcp = localSettings.Host.EnableMcp,
                EnableWebViewDebugLayer = localSettings.Host.EnableWebViewDebugLayer,
                ComponentRefreshTime = localSettings.Host.ComponentRefreshTime,
                NotificationDurationSeconds = localSettings.Host.NotificationDurationSeconds
            },
            Backend = CloneBackendDocument(localSettings.Backend)
        };
    }

    private void WriteCompatibilityHostConfig(DutyLocalSettingsDocument settings, DutyHostRuntimeState runtimeState)
    {
        var projected = CreateProjectedHostConfig(settings.Host, runtimeState);
        projected.Version = Math.Max(1, settings.Version);
        WriteJsonAtomicallyTracked(_pluginPaths.HostConfigPath, projected, "host_config_projection", new
        {
            settings_version = settings.Version,
            auto_run_mode = projected.AutoRunMode,
            access_token_mode = projected.AccessTokenMode,
            enable_mcp = projected.EnableMcp,
            static_access_token_configured = HasStaticAccessTokenConfigured(settings.Host)
        });
    }

    private DutySettingsChangedEventArgs CreateChangedEventArgs(
        DutyLocalSettingsDocument settings,
        DutyHostRuntimeState hostState,
        bool hostSettingsChanged,
        bool backendSettingsChanged,
        bool runtimeStateChanged)
    {
        return new DutySettingsChangedEventArgs(
            CloneLocalSettings(settings),
            CloneHostRuntimeState(hostState),
            CreateProjectedHostConfig(settings.Host, hostState),
            hostSettingsChanged,
            backendSettingsChanged,
            runtimeStateChanged);
    }

    private static T ReadJsonOrDefault<T>(string path, T fallback)
    {
        try
        {
            var raw = File.ReadAllText(path);
            var parsed = JsonSerializer.Deserialize<T>(raw, JsonOptions);
            return parsed == null ? fallback : parsed;
        }
        catch
        {
            return fallback;
        }
    }

    private static void WriteJsonAtomically<T>(string path, T payload)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, payload, JsonOptions);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(path))
        {
            File.Replace(tempPath, path, null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tempPath, path);
        }
    }

    private void WriteJsonAtomicallyTracked<T>(string path, T payload, string purpose, object? metadata)
    {
        try
        {
            WriteJsonAtomically(path, payload);
            _settingsTrace.Info("settings_file_write_completed", new
            {
                purpose,
                metadata,
                file = _settingsTrace.CaptureFileSnapshot(path)
            });
        }
        catch (Exception ex)
        {
            _settingsTrace.Error("settings_file_write_failed", new
            {
                purpose,
                metadata,
                path,
                error = ex.Message
            });
            throw;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private void RaiseSettingsChanged(DutySettingsChangedEventArgs? args)
    {
        if (args == null)
        {
            return;
        }

        try
        {
            SettingsChanged?.Invoke(this, args);
        }
        catch (Exception ex)
        {
            DutyDiagnosticsLogger.Error("SettingsRepo", "SettingsChanged event handler failed.", ex);
        }
    }

    private static List<DutyPlanPreset> CreateDefaultPlanPresets()
    {
        return
        [
            new DutyPlanPreset
            {
                Id = DutyBackendModeIds.Standard,
                Name = "标准",
                ModeId = DutyBackendModeIds.Standard,
                BaseUrl = DefaultBaseUrl,
                Model = DefaultModel,
                ModelProfile = "auto",
                MultiAgentExecutionMode = "auto"
            },
            new DutyPlanPreset
            {
                Id = DutyBackendModeIds.Campus6Agent,
                Name = "6Agent",
                ModeId = DutyBackendModeIds.Campus6Agent,
                BaseUrl = DefaultBaseUrl,
                Model = DefaultModel,
                ModelProfile = "auto",
                MultiAgentExecutionMode = "auto"
            },
            new DutyPlanPreset
            {
                Id = DutyBackendModeIds.IncrementalSmall,
                Name = "增量小模型",
                ModeId = DutyBackendModeIds.IncrementalSmall,
                BaseUrl = DefaultBaseUrl,
                Model = DefaultModel,
                ModelProfile = "auto",
                MultiAgentExecutionMode = "auto"
            }
        ];
    }

    private static List<DutyPlanPreset> ClonePlanPresets(IEnumerable<DutyPlanPreset>? presets)
    {
        return (presets ?? [])
            .Select(plan => new DutyPlanPreset
            {
                Id = plan.Id,
                Name = plan.Name,
                ModeId = plan.ModeId,
                ApiKey = plan.ApiKey,
                BaseUrl = plan.BaseUrl,
                Model = plan.Model,
                ModelProfile = plan.ModelProfile,
                ProviderHint = plan.ProviderHint,
                MultiAgentExecutionMode = plan.MultiAgentExecutionMode
            })
            .ToList();
    }

    private static DutyPersistedHostSettings ClonePersistedHostSettings(DutyPersistedHostSettings host)
    {
        return new DutyPersistedHostSettings
        {
            PythonPath = host.PythonPath,
            AutoRunMode = host.AutoRunMode,
            AutoRunParameter = host.AutoRunParameter,
            AccessTokenMode = host.AccessTokenMode,
            StaticAccessTokenEncrypted = host.StaticAccessTokenEncrypted,
            StaticAccessTokenVerifier = host.StaticAccessTokenVerifier,
            ServerPortMode = host.ServerPortMode,
            FixedServerPort = host.FixedServerPort,
            EnableMcp = host.EnableMcp,
            EnableWebViewDebugLayer = host.EnableWebViewDebugLayer,
            AutoRunTime = host.AutoRunTime,
            AutoRunTriggerNotificationEnabled = host.AutoRunTriggerNotificationEnabled,
            AutoRunRetryTimes = host.AutoRunRetryTimes,
            ComponentRefreshTime = host.ComponentRefreshTime,
            NotificationDurationSeconds = host.NotificationDurationSeconds,
            DutyReminderEnabled = host.DutyReminderEnabled,
            DutyReminderTimes = [.. host.DutyReminderTimes]
        };
    }

    private static DutyEditableBackendSettingsDocument CloneBackendDocument(DutyEditableBackendSettingsDocument backend)
    {
        return new DutyEditableBackendSettingsDocument
        {
            SelectedPlanId = backend.SelectedPlanId,
            PlanPresets = ClonePlanPresets(backend.PlanPresets),
            DutyRule = backend.DutyRule
        };
    }

    private static DutyLocalSettingsDocument CloneLocalSettings(DutyLocalSettingsDocument settings)
    {
        return new DutyLocalSettingsDocument
        {
            Version = settings.Version,
            SavedAtUtc = settings.SavedAtUtc,
            Host = ClonePersistedHostSettings(settings.Host),
            Backend = CloneBackendDocument(settings.Backend)
        };
    }

    private static DutyHostRuntimeState CloneHostRuntimeState(DutyHostRuntimeState state)
    {
        return new DutyHostRuntimeState
        {
            AiConsecutiveFailures = state.AiConsecutiveFailures,
            LastAutoRunDate = state.LastAutoRunDate
        };
    }

    private static bool JsonContentEquals<T>(T left, T right)
    {
        return string.Equals(
            JsonSerializer.Serialize(left, JsonOptions),
            JsonSerializer.Serialize(right, JsonOptions),
            StringComparison.Ordinal);
    }

    private static string NormalizeAutoRunMode(string? mode)
    {
        return (mode ?? "Off").Trim().ToLowerInvariant() switch
        {
            "weekly" => "Weekly",
            "monthly" => "Monthly",
            "custom" => "Custom",
            _ => "Off"
        };
    }

    private static string NormalizePlanModeId(string? modeId)
    {
        return (modeId ?? DutyBackendModeIds.Standard).Trim().ToLowerInvariant() switch
        {
            DutyBackendModeIds.Campus6Agent => DutyBackendModeIds.Campus6Agent,
            "campus6agent" => DutyBackendModeIds.Campus6Agent,
            "6agent" => DutyBackendModeIds.Campus6Agent,
            "multi_agent" => DutyBackendModeIds.Campus6Agent,
            DutyBackendModeIds.IncrementalSmall => DutyBackendModeIds.IncrementalSmall,
            "incremental" => DutyBackendModeIds.IncrementalSmall,
            "small_incremental" => DutyBackendModeIds.IncrementalSmall,
            _ => DutyBackendModeIds.Standard
        };
    }

    private static string NormalizeModelProfile(string? value)
    {
        return (value ?? "auto").Trim().ToLowerInvariant() switch
        {
            "cloud" => "cloud",
            "campus_small" => "campus_small",
            "edge" => "edge",
            "custom" => "custom",
            _ => "auto"
        };
    }

    private static string NormalizeMultiAgentExecutionMode(string? value)
    {
        return (value ?? "auto").Trim().ToLowerInvariant() switch
        {
            "parallel" => "parallel",
            "serial" => "serial",
            _ => "auto"
        };
    }

    private static string NormalizeTime(string? value, string fallback)
    {
        return TimeSpan.TryParse(value, out var parsed)
            ? $"{parsed.Hours:D2}:{parsed.Minutes:D2}"
            : fallback;
    }

    private static string NormalizeReminderTime(string? value)
    {
        return TimeSpan.TryParse(value, out var parsed)
            ? $"{parsed.Hours:D2}:{parsed.Minutes:D2}"
            : DefaultDutyReminderTime;
    }

    private static List<string> NormalizeReminderTimes(IEnumerable<string>? values)
    {
        var normalized = (values ?? [])
            .Select(NormalizeReminderTime)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        return normalized.Count == 0 ? [DefaultDutyReminderTime] : normalized;
    }

    private static bool HasStaticAccessTokenConfigured(DutyPersistedHostSettings? host)
    {
        return !string.IsNullOrWhiteSpace(host?.StaticAccessTokenEncrypted) &&
               !string.IsNullOrWhiteSpace(host?.StaticAccessTokenVerifier);
    }

    private static string NormalizePersistedSecret(string? value)
    {
        return (value ?? string.Empty).Trim();
    }

    private static int? NormalizeServicePort(int? value)
    {
        if (!value.HasValue)
        {
            return null;
        }

        return value.Value is >= MinServicePort and <= MaxServicePort
            ? value.Value
            : null;
    }

    private static string? NormalizeOptionalSecret(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length == 0 ? null : normalized;
    }

    private static string BuildHostAccessSecurityMessage(
        DutyHostAccessSecuritySaveRequest request,
        bool hadStaticTokenConfigured,
        DutyPersistedHostSettings nextHost,
        bool replacedStaticToken)
    {
        if (request.ClearStaticAccessToken)
        {
            return "静态 token 已清除，重启后恢复动态 token。";
        }

        if (nextHost.AccessTokenMode == DutyAccessTokenModes.Static)
        {
            if (replacedStaticToken)
            {
                return hadStaticTokenConfigured
                    ? "静态 token 已更新，重启后生效。"
                    : "静态 token 已保存，重启后生效。";
            }

            return "已切换到静态 token 模式，重启后生效。";
        }

        return "已切换到动态 token 模式，重启后生效。";
    }
}
