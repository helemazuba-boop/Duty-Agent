using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ClassIsland.Shared.Helpers;
using DutyAgent.Models;

namespace DutyAgent.Services;

public interface IConfigManager
{
    DutyConfig Config { get; }
    void SaveConfig();
    DutyConfig UpdateConfig(Action<DutyConfig> update);
    event EventHandler<DutyConfig> ConfigChanged;
}

public class DutyConfigManager : IConfigManager, IDisposable
{
    private const string DefaultDutyReminderTime = "07:40";
    private static readonly TimeSpan WatcherIgnoreWindow = TimeSpan.FromMilliseconds(800);

    private readonly string _configPath;
    private readonly object _configLock = new();
    private FileSystemWatcher? _watcher;
    private DutyConfig _config = new();
    private bool _disposed;
    private bool _suppressAutoSave;
    private DateTime _ignoreWatcherEventsUntilUtc = DateTime.MinValue;

    public DutyConfig Config
    {
        get
        {
            lock (_configLock)
            {
                return _config;
            }
        }
    }

    public event EventHandler<DutyConfig>? ConfigChanged;

    public DutyConfigManager(DutyPluginPaths pluginPaths)
    {
        var dataDir = pluginPaths.DataDirectory;
        Directory.CreateDirectory(dataDir);
        _configPath = pluginPaths.HostConfigPath;

        LoadConfigInternal();
        InitializeWatcher(dataDir);
    }

    private void LoadConfigInternal()
    {
        DutyConfig loadedConfig;

        lock (_configLock)
        {
            if (_config != null)
            {
                _config.PropertyChanged -= OnConfigPropertyChangedCurrent;
            }

            if (!File.Exists(_configPath))
            {
                _config = CreateDefaultHostConfig();
                SaveConfigInternal();
            }
            else
            {
                try
                {
                    loadedConfig = ConfigureFileHelper.LoadConfig<DutyConfig>(_configPath);
                    loadedConfig.DutyReminderTimes = NormalizeDutyReminderTimes(loadedConfig.DutyReminderTimes);
                    _config = loadedConfig;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"LoadConfig Error: {ex.Message}");
                    _config = CreateDefaultHostConfig();
                    SaveConfigInternal();
                }
            }

            _config.PropertyChanged += OnConfigPropertyChangedCurrent;
            loadedConfig = _config;
        }

        ConfigChanged?.Invoke(this, loadedConfig);
    }

    private static DutyConfig CreateDefaultHostConfig()
    {
        return new DutyConfig
        {
            DutyReminderTimes = [DefaultDutyReminderTime]
        };
    }

    private static DutyConfig CloneConfig(DutyConfig source)
    {
        return new DutyConfig
        {
            PythonPath = source.PythonPath,
            AutoRunMode = source.AutoRunMode,
            AutoRunParameter = source.AutoRunParameter,
            EnableMcp = source.EnableMcp,
            EnableWebViewDebugLayer = source.EnableWebViewDebugLayer,
            AutoRunTime = source.AutoRunTime,
            AutoRunTriggerNotificationEnabled = source.AutoRunTriggerNotificationEnabled,
            AutoRunRetryTimes = source.AutoRunRetryTimes,
            AiConsecutiveFailures = source.AiConsecutiveFailures,
            LastAutoRunDate = source.LastAutoRunDate,
            ComponentRefreshTime = source.ComponentRefreshTime,
            NotificationDurationSeconds = source.NotificationDurationSeconds,
            DutyReminderEnabled = source.DutyReminderEnabled,
            DutyReminderTimes = [.. (source.DutyReminderTimes ?? [])]
        };
    }

    private void SaveConfigInternal()
    {
        _ignoreWatcherEventsUntilUtc = DateTime.UtcNow.Add(WatcherIgnoreWindow);
        ConfigureFileHelper.SaveConfig(_configPath, _config);
    }

    public void SaveConfig()
    {
        DutyConfig savedConfig;
        lock (_configLock)
        {
            SaveConfigInternal();
            savedConfig = _config;
        }

        ConfigChanged?.Invoke(this, savedConfig);
    }

    public DutyConfig UpdateConfig(Action<DutyConfig> update)
    {
        if (update == null)
        {
            throw new ArgumentNullException(nameof(update));
        }

        DutyConfig updatedConfig;
        lock (_configLock)
        {
            _suppressAutoSave = true;
            try
            {
                var nextConfig = CloneConfig(_config);
                update(nextConfig);
                nextConfig.DutyReminderTimes = NormalizeDutyReminderTimes(nextConfig.DutyReminderTimes);
                _config.PropertyChanged -= OnConfigPropertyChangedCurrent;
                _config = nextConfig;
                _config.PropertyChanged += OnConfigPropertyChangedCurrent;
                SaveConfigInternal();
                updatedConfig = _config;
            }
            finally
            {
                _suppressAutoSave = false;
            }
        }

        ConfigChanged?.Invoke(this, updatedConfig);
        return updatedConfig;
    }

    private void OnConfigPropertyChangedCurrent(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressAutoSave)
        {
            return;
        }

        SaveConfig();
    }

    private void InitializeWatcher(string dataDir)
    {
        try
        {
            _watcher = new FileSystemWatcher(dataDir, Path.GetFileName(_configPath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
            };
            _watcher.Changed += OnConfigFileChanged;
            _watcher.Created += OnConfigFileChanged;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"InitializeWatcher Error: {ex.Message}");
        }
    }

    private void OnConfigFileChanged(object sender, FileSystemEventArgs e)
    {
        if (DateTime.UtcNow < _ignoreWatcherEventsUntilUtc)
        {
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (DateTime.UtcNow < _ignoreWatcherEventsUntilUtc)
            {
                return;
            }

            LoadConfigInternal();
        });
    }

    private static List<string> NormalizeDutyReminderTimes(List<string>? times)
    {
        if (times == null || times.Count == 0)
        {
            return [DefaultDutyReminderTime];
        }

        var normalized = times
            .Select(TryNormalizeDutyReminderTime)
            .Where(x => !string.IsNullOrEmpty(x))
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        if (normalized.Count == 0)
        {
            return [DefaultDutyReminderTime];
        }

        return normalized!;
    }

    private static string? TryNormalizeDutyReminderTime(string? input)
    {
        var val = (input ?? "").Trim();
        if (val.Length != 5)
        {
            return null;
        }

        if (!TimeSpan.TryParse(val + ":00", out _))
        {
            return null;
        }

        var parts = val.Split(':');
        if (parts.Length != 2)
        {
            return null;
        }

        if (!int.TryParse(parts[0], out var h) || h < 0 || h > 23)
        {
            return null;
        }

        if (!int.TryParse(parts[1], out var m) || m < 0 || m > 59)
        {
            return null;
        }

        return val;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnConfigFileChanged;
            _watcher.Created -= OnConfigFileChanged;
            _watcher.Dispose();
        }
    }
}
