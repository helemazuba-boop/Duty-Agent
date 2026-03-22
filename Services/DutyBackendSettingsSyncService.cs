using System.Text.Json;
using DutyAgent.Models;

namespace DutyAgent.Services;

public sealed class DutyBackendSettingsSyncService : IDisposable
{
    private const string DefaultBaseUrl = "https://integrate.api.nvidia.com/v1";
    private const string DefaultModel = "moonshotai/kimi-k2-thinking";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IDutySettingsRepository _repository;
    private readonly IPythonIpcService _ipcService;
    private readonly DutySettingsTraceService _settingsTrace;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private bool _disposed;
    private bool _workerRunning;
    private int _pendingVersion;
    private DutyBackendSyncStatusSnapshot _status = new();

    public DutyBackendSettingsSyncService(
        IDutySettingsRepository repository,
        IPythonIpcService ipcService,
        DutySettingsTraceService settingsTrace)
    {
        _repository = repository;
        _ipcService = ipcService;
        _settingsTrace = settingsTrace;
        _repository.SettingsChanged += OnRepositorySettingsChanged;
    }

    public event EventHandler<DutyBackendSyncStatusSnapshot>? StatusChanged;

    public DutyBackendSyncStatusSnapshot GetStatusSnapshot()
    {
        lock (_gate)
        {
            return CloneStatus(_status);
        }
    }

    public void RequestSync(string reason = "manual")
    {
        var version = _repository.LoadLocalSettings().Version;
        DutyDiagnosticsLogger.Info("BackendSync", "Requested backend settings sync.",
            new { reason, version });
        _settingsTrace.Info("backend_sync_requested", new
        {
            reason,
            version
        });
        QueueSync(version);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _repository.SettingsChanged -= OnRepositorySettingsChanged;
        _disposeCts.Cancel();
        _disposeCts.Dispose();
    }

    private void OnRepositorySettingsChanged(object? sender, DutySettingsChangedEventArgs e)
    {
        if (!e.BackendSettingsChanged)
        {
            return;
        }

        _settingsTrace.Info("backend_sync_queued_from_settings_change", new
        {
            version = e.Settings.Version,
            selected_plan_id = e.Settings.Backend.SelectedPlanId
        });
        QueueSync(e.Settings.Version);
    }

    private void QueueSync(int version)
    {
        bool startWorker = false;
        DutyBackendSyncStatusSnapshot? snapshotToRaise = null;
        lock (_gate)
        {
            if (version > _pendingVersion)
            {
                _pendingVersion = version;
            }

            if (_status.State == DutyBackendSyncState.Idle || _status.State == DutyBackendSyncState.Synced)
            {
                _status = new DutyBackendSyncStatusSnapshot
                {
                    State = DutyBackendSyncState.Syncing,
                    SettingsVersion = Math.Max(version, _status.SettingsVersion),
                    LastAttemptAtUtc = _status.LastAttemptAtUtc,
                    LastSuccessAtUtc = _status.LastSuccessAtUtc,
                    LastError = string.Empty
                };
                snapshotToRaise = CloneStatus(_status);
            }

            if (!_workerRunning)
            {
                _workerRunning = true;
                startWorker = true;
            }
        }

        if (snapshotToRaise is not null)
        {
            RaiseStatusChanged(snapshotToRaise);
        }

        if (startWorker)
        {
            _ = Task.Run(() => RunWorkerAsync(_disposeCts.Token));
        }
    }

    private async Task RunWorkerAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int targetVersion;
                DutyBackendSyncStatusSnapshot? snapshotToRaise = null;
                lock (_gate)
                {
                    targetVersion = _pendingVersion;
                    if (targetVersion <= 0)
                    {
                        _workerRunning = false;
                        if (_status.State != DutyBackendSyncState.Failed)
                        {
                            _status = new DutyBackendSyncStatusSnapshot
                            {
                                State = DutyBackendSyncState.Idle,
                                SettingsVersion = _status.SettingsVersion,
                                LastAttemptAtUtc = _status.LastAttemptAtUtc,
                                LastSuccessAtUtc = _status.LastSuccessAtUtc,
                                LastError = _status.LastError
                            };
                            snapshotToRaise = CloneStatus(_status);
                        }
                    }
                }

                if (snapshotToRaise is not null)
                {
                    RaiseStatusChanged(snapshotToRaise);
                }

                if (targetVersion <= 0)
                {
                    return;
                }

                var success = await TrySyncLatestAsync(targetVersion, cancellationToken).ConfigureAwait(false);
                if (success)
                {
                    lock (_gate)
                    {
                        if (_pendingVersion == targetVersion)
                        {
                            _pendingVersion = 0;
                        }
                    }
                    continue;
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _workerRunning = false;
            }
        }
    }

    private async Task<bool> TrySyncLatestAsync(int targetVersion, CancellationToken cancellationToken)
    {
        var traceId = DutyDiagnosticsLogger.CreateTraceId("backend-sync");
        var startedAt = DateTimeOffset.UtcNow;
        var localSettings = _repository.LoadLocalSettings();
        var localBackend = localSettings.Backend;
        _settingsTrace.Info("backend_sync_started", new
        {
            trace_id = traceId,
            target_version = targetVersion,
            local_version = localSettings.Version,
            selected_plan_id = localBackend.SelectedPlanId
        });

        UpdateStatus(new DutyBackendSyncStatusSnapshot
        {
            State = DutyBackendSyncState.Syncing,
            SettingsVersion = localSettings.Version,
            LastAttemptAtUtc = startedAt,
            LastSuccessAtUtc = GetStatusSnapshot().LastSuccessAtUtc,
            LastError = string.Empty
        });

        try
        {
            var remote = await _ipcService.GetBackendConfigAsync(
                requestSource: "host_settings_sync",
                traceId: traceId,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!BackendMatches(localBackend, remote))
            {
                var patch = new DutyBackendConfigPatch
                {
                    ExpectedVersion = remote.Version,
                    SelectedPlanId = localBackend.SelectedPlanId,
                    PlanPresets = ClonePlanPresets(localBackend.PlanPresets),
                    DutyRule = localBackend.DutyRule
                };

                await _ipcService.UpdateBackendConfigAsync(
                    patch,
                    requestSource: "host_settings_sync",
                    traceId: traceId,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                _settingsTrace.Info("backend_sync_patch_applied", new
                {
                    trace_id = traceId,
                    expected_version = remote.Version,
                    selected_plan_id = patch.SelectedPlanId,
                    plan_count = patch.PlanPresets?.Count ?? 0
                });
            }
            else
            {
                _settingsTrace.Info("backend_sync_skipped_same_state", new
                {
                    trace_id = traceId,
                    local_version = localSettings.Version,
                    remote_version = remote.Version,
                    selected_plan_id = localBackend.SelectedPlanId
                });
            }

            UpdateStatus(new DutyBackendSyncStatusSnapshot
            {
                State = DutyBackendSyncState.Synced,
                SettingsVersion = Math.Max(localSettings.Version, targetVersion),
                LastAttemptAtUtc = startedAt,
                LastSuccessAtUtc = DateTimeOffset.UtcNow,
                LastError = string.Empty
            });
            _settingsTrace.Info("backend_sync_completed", new
            {
                trace_id = traceId,
                local_version = localSettings.Version,
                target_version = targetVersion,
                selected_plan_id = localBackend.SelectedPlanId
            });
            return true;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            DutyDiagnosticsLogger.Warn("BackendSync", "Backend settings sync failed.",
                new { traceId, targetVersion, error = ex.Message });
            UpdateStatus(new DutyBackendSyncStatusSnapshot
            {
                State = DutyBackendSyncState.Failed,
                SettingsVersion = Math.Max(localSettings.Version, targetVersion),
                LastAttemptAtUtc = startedAt,
                LastSuccessAtUtc = GetStatusSnapshot().LastSuccessAtUtc,
                LastError = ex.Message
            });
            _settingsTrace.Warn("backend_sync_failed", new
            {
                trace_id = traceId,
                target_version = targetVersion,
                local_version = localSettings.Version,
                error = ex.Message
            });
            return false;
        }
    }

    private void UpdateStatus(DutyBackendSyncStatusSnapshot snapshot)
    {
        lock (_gate)
        {
            _status = snapshot;
        }

        RaiseStatusChanged(CloneStatus(snapshot));
    }

    private void RaiseStatusChanged(DutyBackendSyncStatusSnapshot snapshot)
    {
        try
        {
            StatusChanged?.Invoke(this, snapshot);
        }
        catch (Exception ex)
        {
            DutyDiagnosticsLogger.Error("BackendSync", "StatusChanged handler failed.", ex);
        }
    }

    private static DutyBackendSyncStatusSnapshot CloneStatus(DutyBackendSyncStatusSnapshot snapshot)
    {
        return new DutyBackendSyncStatusSnapshot
        {
            State = snapshot.State,
            SettingsVersion = snapshot.SettingsVersion,
            LastAttemptAtUtc = snapshot.LastAttemptAtUtc,
            LastSuccessAtUtc = snapshot.LastSuccessAtUtc,
            LastError = snapshot.LastError
        };
    }

    private static bool BackendMatches(DutyEditableBackendSettingsDocument localBackend, DutyBackendConfig remote)
    {
        var normalizedLocal = NormalizeBackendDocument(localBackend);
        var normalizedRemote = NormalizeBackendDocument(new DutyEditableBackendSettingsDocument
        {
            SelectedPlanId = remote.SelectedPlanId,
            PlanPresets = ClonePlanPresets(remote.PlanPresets),
            DutyRule = remote.DutyRule
        });

        var localComparable = new
        {
            selected_plan_id = normalizedLocal.SelectedPlanId,
            plan_presets = ClonePlanPresets(normalizedLocal.PlanPresets),
            duty_rule = normalizedLocal.DutyRule ?? string.Empty
        };
        var remoteComparable = new
        {
            selected_plan_id = normalizedRemote.SelectedPlanId,
            plan_presets = ClonePlanPresets(normalizedRemote.PlanPresets),
            duty_rule = normalizedRemote.DutyRule ?? string.Empty
        };
        return string.Equals(
            JsonSerializer.Serialize(localComparable, JsonOptions),
            JsonSerializer.Serialize(remoteComparable, JsonOptions),
            StringComparison.Ordinal);
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

    private static DutyEditableBackendSettingsDocument NormalizeBackendDocument(DutyEditableBackendSettingsDocument? backend)
    {
        backend ??= new DutyEditableBackendSettingsDocument();
        var presets = ClonePlanPresets(backend.PlanPresets);
        if (presets.Count == 0)
        {
            presets =
            [
                CreateDefaultPlanPreset(DutyBackendModeIds.Standard),
                CreateDefaultPlanPreset(DutyBackendModeIds.Campus6Agent),
                CreateDefaultPlanPreset(DutyBackendModeIds.IncrementalSmall)
            ];
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

    private static DutyPlanPreset CreateDefaultPlanPreset(string modeId)
    {
        return new DutyPlanPreset
        {
            Id = modeId,
            Name = modeId switch
            {
                DutyBackendModeIds.Campus6Agent => "6Agent",
                DutyBackendModeIds.IncrementalSmall => "增量小模型",
                _ => "标准"
            },
            ModeId = modeId,
            BaseUrl = DefaultBaseUrl,
            Model = DefaultModel,
            ModelProfile = "auto",
            MultiAgentExecutionMode = "auto"
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
}
