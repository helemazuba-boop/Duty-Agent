using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using DutyAgent.Models;

namespace DutyAgent.Services;

public interface IStateAndRosterManager
{
    DutyState LoadState();
    event EventHandler<DutyState> StateChanged;
}

public class DutyStateManager : IStateAndRosterManager, IDisposable
{
    private const int StateChangeDebounceMilliseconds = 150;
    private const int StateReadRetryCount = 5;
    private const int StateReadRetryDelayMilliseconds = 50;
    // Watcher rebuild retry backoff: doubles per consecutive failure, capped.
    // Unlimited retries (capped interval) are intentional — the directory may
    // legitimately come back after an AV scan or a moved/renamed data dir —
    // but each retry must actually be scheduled, hence InitializeWatcher
    // reports failure instead of swallowing it.
    private static readonly int[] WatcherRebuildBackoffMs = { 2000, 4000, 8000, 16000, 32000, 60000 };

    private readonly string _statePath;
    private readonly string _watchDirectory;
    private FileSystemWatcher? _stateWatcher;
    private readonly object _watcherRebuildGate = new();
    private int _watcherRebuildAttempt;
    private bool _disposed;
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly object _stateChangeGate = new();
    private CancellationTokenSource? _pendingStateChangeCts;

    public event EventHandler<DutyState>? StateChanged;

    public DutyStateManager(DutyPluginPaths pluginPaths)
    {
        var dataDir = pluginPaths.DataDirectory;
        Directory.CreateDirectory(dataDir);
        _statePath = pluginPaths.StatePath;
        _watchDirectory = dataDir;

        InitializeWatcher(_watchDirectory);
        // First arm can fail (data dir deleted between CreateDirectory and the
        // watcher start, AV lock, ...); route into the retry chain instead of
        // staying silent forever.
        if (_stateWatcher == null)
        {
            ScheduleWatcherRebuildRetry();
        }
    }

    public DutyState LoadState()
    {
        _stateLock.Wait();
        try
        {
            if (!File.Exists(_statePath))
            {
                var state = new DutyState();
                var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_statePath, json);
                return state;
            }

            try
            {
                return ReadStateFileUnsafe(createIfMissing: false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error loading state file: {ex.Message}");
                return new DutyState();
            }
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private DutyState LoadStateInternalUnsafe()
    {
        try
        {
            return ReadStateFileUnsafe(createIfMissing: false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error loading state file: {ex.Message}");
            return new DutyState();
        }
    }

    /// <summary>
    /// Constructs and arms the watcher. Returns false when the watcher could
    /// not be armed (missing/locked directory): callers must react by
    /// scheduling a rebuild — swallowing here used to leave the watcher in a
    /// constructed-but-disabled state forever (events silently lost).
    /// </summary>
    private bool InitializeWatcher(string dataDir)
    {
        try
        {
            var watcher = new FileSystemWatcher(dataDir, Path.GetFileName(_statePath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                // The default 4KB buffer silently overflows under write storms;
                // 64KB plus the Error handler below keeps us alive regardless.
                InternalBufferSize = 64 * 1024
            };
            watcher.Changed += OnStateFileChanged;
            watcher.Created += OnStateFileChanged;
            watcher.Deleted += OnStateFileChanged;
            watcher.Renamed += OnStateFileRenamed;
            watcher.Error += OnStateWatcherError;
            // Throws FileNotFoundException/ArgumentException when the directory
            // is gone or unusable — that is exactly the signal the retry path
            // needs, so it must not be caught inside this method.
            watcher.EnableRaisingEvents = true;
            _stateWatcher = watcher;
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"InitializeWatcher Error: {ex.Message}");
            return false;
        }
    }

    private void OnStateWatcherError(object? sender, ErrorEventArgs e)
    {
        Debug.WriteLine($"State watcher error: {e.GetException().Message}");
        RecreateWatcher();
        // Events may have been dropped around the failure; force one reload so
        // the UI catches up instead of silently freezing on a stale state.
        QueueStateChangedNotification();
    }

    private void RecreateWatcher()
    {
        if (_disposed)
        {
            return;
        }

        lock (_watcherRebuildGate)
        {
            if (_disposed)
            {
                return;
            }

            var old = _stateWatcher;
            _stateWatcher = null;
            if (old != null)
            {
                try
                {
                    old.EnableRaisingEvents = false;
                    old.Dispose();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Old state watcher dispose error: {ex.Message}");
                }
            }

            // InitializeWatcher reports failure instead of swallowing it, so a
            // rebuild that cannot arm (directory missing/locked) actually
            // reaches the retry path instead of dying quietly.
            if (InitializeWatcher(_watchDirectory))
            {
                _watcherRebuildAttempt = 0;
                return;
            }

            ScheduleWatcherRebuildRetry();
        }
    }

    private void ScheduleWatcherRebuildRetry()
    {
        if (_disposed)
        {
            return;
        }

        var attempt = _watcherRebuildAttempt;
        _watcherRebuildAttempt = Math.Min(attempt + 1, WatcherRebuildBackoffMs.Length - 1);
        var delayMs = WatcherRebuildBackoffMs[Math.Min(attempt, WatcherRebuildBackoffMs.Length - 1)];
        Debug.WriteLine($"State watcher rebuild failed; retrying in {delayMs}ms.");

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMs).ConfigureAwait(false);
                RecreateWatcher();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"State watcher retry error: {ex.Message}");
            }
        });
    }

    private void OnStateFileChanged(object sender, FileSystemEventArgs e)
    {
        if (IsStatePath(e.FullPath))
        {
            QueueStateChangedNotification();
        }
    }

    private void OnStateFileRenamed(object sender, RenamedEventArgs e)
    {
        if (IsStatePath(e.FullPath) || IsStatePath(e.OldFullPath))
        {
            QueueStateChangedNotification();
        }
    }

    private void QueueStateChangedNotification()
    {
        CancellationTokenSource nextCts;
        lock (_stateChangeGate)
        {
            _pendingStateChangeCts?.Cancel();
            _pendingStateChangeCts?.Dispose();
            _pendingStateChangeCts = new CancellationTokenSource();
            nextCts = _pendingStateChangeCts;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(StateChangeDebounceMilliseconds, nextCts.Token).ConfigureAwait(false);
                var state = LoadStateSnapshotWithoutCreating();
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!_disposed)
                    {
                        StateChanged?.Invoke(this, state);
                    }
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"State watcher dispatch error: {ex.Message}");
            }
        });
    }

    private DutyState LoadStateSnapshotWithoutCreating()
    {
        _stateLock.Wait();
        try
        {
            return ReadStateFileUnsafe(createIfMissing: false);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private DutyState ReadStateFileUnsafe(bool createIfMissing)
    {
        for (var attempt = 0; attempt < StateReadRetryCount; attempt++)
        {
            if (!File.Exists(_statePath))
            {
                if (!createIfMissing)
                {
                    Thread.Sleep(StateReadRetryDelayMilliseconds);
                    continue;
                }

                var state = new DutyState();
                var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_statePath, json);
                return state;
            }

            try
            {
                var json = File.ReadAllText(_statePath);
                return JsonSerializer.Deserialize<DutyState>(json) ?? new DutyState();
            }
            catch (IOException) when (attempt < StateReadRetryCount - 1)
            {
                Thread.Sleep(StateReadRetryDelayMilliseconds);
            }
            catch (UnauthorizedAccessException) when (attempt < StateReadRetryCount - 1)
            {
                Thread.Sleep(StateReadRetryDelayMilliseconds);
            }
            catch (JsonException) when (attempt < StateReadRetryCount - 1)
            {
                Thread.Sleep(StateReadRetryDelayMilliseconds);
            }
        }

        return new DutyState();
    }

    private bool IsStatePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return string.Equals(Path.GetFullPath(path), Path.GetFullPath(_statePath), StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_stateWatcher != null)
        {
            _stateWatcher.EnableRaisingEvents = false;
            _stateWatcher.Changed -= OnStateFileChanged;
            _stateWatcher.Created -= OnStateFileChanged;
            _stateWatcher.Deleted -= OnStateFileChanged;
            _stateWatcher.Renamed -= OnStateFileRenamed;
            _stateWatcher.Error -= OnStateWatcherError;
            _stateWatcher.Dispose();
            _stateWatcher = null;
        }

        lock (_stateChangeGate)
        {
            _pendingStateChangeCts?.Cancel();
            _pendingStateChangeCts?.Dispose();
            _pendingStateChangeCts = null;
        }

        _stateLock.Dispose();
    }
}
