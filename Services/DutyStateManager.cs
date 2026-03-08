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
    void SaveState(DutyState state);
    event EventHandler<DutyState> StateChanged;
    
    SchedulePoolItem? TryUpdateScheduleEntry(DutyState state, SchedulePoolItem updatedEntry);
    bool TrySaveScheduleEntry(string date, SchedulePoolItem updatedEntry);
}

public class DutyStateManager : IStateAndRosterManager, IDisposable
{
    private readonly string _statePath;
    private FileSystemWatcher? _stateWatcher;
    private bool _disposed;
    private readonly SemaphoreSlim _stateLock = new(1, 1);

    public event EventHandler<DutyState>? StateChanged;

    public DutyStateManager()
    {
        var basePath = Path.GetDirectoryName(typeof(DutyStateManager).Assembly.Location) ?? AppContext.BaseDirectory;
        var dataDir = Path.Combine(basePath, "Assets_Duty", "data");
        Directory.CreateDirectory(dataDir);
        _statePath = Path.Combine(dataDir, "state.json");

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

    public void SaveState(DutyState state)
    {
        _stateLock.Wait();
        try
        {
            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_statePath, json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error saving state file: {ex.Message}");
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public SchedulePoolItem? TryUpdateScheduleEntry(DutyState state, SchedulePoolItem updatedEntry)
    {
        for (var i = 0; i < state.SchedulePool.Count; i++)
        {
            if (state.SchedulePool[i].Date == updatedEntry.Date)
            {
                state.SchedulePool[i] = updatedEntry;
                return state.SchedulePool[i];
            }
        }
        return null;
    }

    public bool TrySaveScheduleEntry(string date, SchedulePoolItem updatedEntry)
    {
        if (date != updatedEntry.Date)
        {
            return false;
        }

        _stateLock.Wait();
        try
        {
            var state = LoadStateInternalUnsafe();
            var success = false;
            for (var i = 0; i < state.SchedulePool.Count; i++)
            {
                if (state.SchedulePool[i].Date == date)
                {
                    state.SchedulePool[i] = updatedEntry;
                    success = true;
                    break;
                }
            }

            if (success)
            {
                var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_statePath, json);
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error TrySaveScheduleEntry: {ex.Message}");
            return false;
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
