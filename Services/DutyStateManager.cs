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
    private readonly string _statePath;
    private FileSystemWatcher? _stateWatcher;
    private bool _disposed;
    private readonly SemaphoreSlim _stateLock = new(1, 1);

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
                var json = File.ReadAllText(_statePath);
                return JsonSerializer.Deserialize<DutyState>(json) ?? new DutyState();
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
        if (!File.Exists(_statePath))
        {
            return new DutyState();
        }

        try
        {
            var json = File.ReadAllText(_statePath);
            return JsonSerializer.Deserialize<DutyState>(json) ?? new DutyState();
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
            _stateWatcher = new FileSystemWatcher(dataDir, "state.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
            };
            _stateWatcher.Changed += OnStateFileChanged;
            _stateWatcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"InitializeWatcher Error: {ex.Message}");
        }
    }

    private void OnStateFileChanged(object sender, FileSystemEventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            StateChanged?.Invoke(this, LoadState());
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_stateWatcher != null)
        {
            _stateWatcher.EnableRaisingEvents = false;
            _stateWatcher.Changed -= OnStateFileChanged;
            _stateWatcher.Dispose();
        }
        
        _stateLock.Dispose();
    }
}
