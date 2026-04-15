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

    private readonly string _statePath;
    private FileSystemWatcher? _stateWatcher;
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

        InitializeWatcher(dataDir);
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

    private void InitializeWatcher(string dataDir)
    {
        try
        {
            _stateWatcher = new FileSystemWatcher(dataDir, Path.GetFileName(_statePath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
            };
            _stateWatcher.Changed += OnStateFileChanged;
            _stateWatcher.Created += OnStateFileChanged;
            _stateWatcher.Deleted += OnStateFileChanged;
            _stateWatcher.Renamed += OnStateFileRenamed;
            _stateWatcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"InitializeWatcher Error: {ex.Message}");
        }
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
            _stateWatcher.Dispose();
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
