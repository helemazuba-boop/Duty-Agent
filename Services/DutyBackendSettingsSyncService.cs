using System.Text.Json;
using DutyAgent.Models;

namespace DutyAgent.Services;

public sealed class DutyBackendSettingsSyncService : IDisposable
{
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
    // Consecutive-failure backoff: without it, a Faulted engine (or any
    // persistent sync error) turns this worker into an unbounded 5s retry
    // storm — thousands of log lines, trace writes and StatusChanged events
    // per day until someone intervenes. Doubles 5s → 160s, resets on success
    // or when the pending target changes (a new user edit deserves an
    // immediate attempt).
    private int _consecutiveSyncFailures;
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
                // A fresh user edit deserves an immediate sync attempt even if
                // the previous target failed repeatedly.
                _consecutiveSyncFailures = 0;
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
                    _consecutiveSyncFailures = 0;
                    lock (_gate)
                    {
                        if (_pendingVersion == targetVersion)
                        {
                            _pendingVersion = 0;
                        }
                    }
                    continue;
                }

                _consecutiveSyncFailures++;
                var backoffSeconds = Math.Min(5 << Math.Min(_consecutiveSyncFailures - 1, 5), 160);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), cancellationToken).ConfigureAwait(false);
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
                    // 按现状传值：本地存储的 api_key（dpapi:v1: 密文）原样上传，
                    // Python 侧 config 归一化处解密（契约 C2）。
                    PlanPresets = DutyBackendDocumentNormalizer.ClonePlanPresets(localBackend.PlanPresets),
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
        var normalizedLocal = DutyBackendDocumentNormalizer.Normalize(localBackend);
        var normalizedRemote = DutyBackendDocumentNormalizer.Normalize(new DutyEditableBackendSettingsDocument
        {
            SelectedPlanId = remote.SelectedPlanId,
            PlanPresets = DutyBackendDocumentNormalizer.ClonePlanPresets(remote.PlanPresets),
            DutyRule = remote.DutyRule
        });

        var localComparable = new
        {
            selected_plan_id = normalizedLocal.SelectedPlanId,
            plan_presets = CloneComparablePlanPresets(normalizedLocal.PlanPresets),
            duty_rule = normalizedLocal.DutyRule ?? string.Empty
        };
        var remoteComparable = new
        {
            selected_plan_id = normalizedRemote.SelectedPlanId,
            plan_presets = CloneComparablePlanPresets(normalizedRemote.PlanPresets),
            duty_rule = normalizedRemote.DutyRule ?? string.Empty
        };
        return string.Equals(
            JsonSerializer.Serialize(localComparable, JsonOptions),
            JsonSerializer.Serialize(remoteComparable, JsonOptions),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// api_key 参与比较前先"解密还原"：本地存储是 dpapi:v1: 密文（C2/D4），后端
    /// 归一化后返回的是解密后的明文。两侧都做 Unprotect-if-protected，才能按
    /// 内容比较 —— 否则密文(不确定) vs 明文永远不等，同步 worker 会陷入补丁循环。
    /// </summary>
    private static List<object> CloneComparablePlanPresets(IEnumerable<DutyPlanPreset> presets)
    {
        return presets.Select(plan => (object)new
        {
            id = plan.Id,
            name = plan.Name,
            mode_id = plan.ModeId,
            api_key = UnwrapApiKeyForComparison(plan.ApiKey),
            base_url = plan.BaseUrl,
            model = plan.Model,
            model_profile = plan.ModelProfile,
            provider_hint = plan.ProviderHint,
            multi_agent_execution_mode = plan.MultiAgentExecutionMode
        }).ToList();
    }

    private static string UnwrapApiKeyForComparison(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return string.Empty;
        }

        if (!SecurityHelper.IsDpapiProtected(apiKey))
        {
            return apiKey;
        }

        return SecurityHelper.UnprotectCurrentUser(apiKey) ?? string.Empty;
    }
}
