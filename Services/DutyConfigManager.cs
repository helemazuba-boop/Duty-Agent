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
    event EventHandler<DutyConfig> ConfigChanged;
}

public class DutyConfigManager : IConfigManager, IDisposable
{
    private const string DefaultDutyReminderTime = "07:40";
    private readonly string _configPath;
    private FileSystemWatcher? _watcher;
    private DutyConfig _config = new();
    private bool _disposed;
    private readonly object _configLock = new(); // Required for atomic swaps of the configure reference

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
        _configPath = pluginPaths.ConfigPath;

        LoadConfigInternal();
        InitializeWatcher(dataDir);
    }

    private void LoadConfigInternal()
    {
        lock (_configLock)
        {
            if (_config != null)
            {
                _config.PropertyChanged -= OnConfigPropertyChangedCurrent;
            }

            if (!File.Exists(_configPath))
            {
                _config = new DutyConfig();
                _config.DutyReminderTimes = NormalizeDutyReminderTimes(_config.DutyReminderTimes);
                SaveConfigInternal();
            }
            else
            {
                try
                {
                    var loaded = ConfigureFileHelper.LoadConfig<DutyConfig>(_configPath);
                    loaded.DutyReminderTimes = NormalizeDutyReminderTimes(loaded.DutyReminderTimes);

                    var migrated = false;
                    if (string.IsNullOrWhiteSpace(loaded.PlainApiKey) &&
                        !string.IsNullOrWhiteSpace(loaded.EncryptedApiKey))
                    {
                        if (SecurityHelper.IsCurrentEncryptionFormat(loaded.EncryptedApiKey))
                        {
                            try
                            {
                                loaded.PlainApiKey = SecurityHelper.DecryptString(loaded.EncryptedApiKey);
                            }
                            catch
                            {
                                loaded.PlainApiKey = loaded.EncryptedApiKey;
                            }
                        }
                        else
                        {
                            // Compatibility fallback: legacy builds might have stored plain text here.
                            loaded.PlainApiKey = loaded.EncryptedApiKey;
                        }

                        loaded.EncryptedApiKey = string.Empty;
                        migrated = true;
                    }
                    else if (!string.IsNullOrWhiteSpace(loaded.EncryptedApiKey))
                    {
                        // Keep one source of truth while plaintext mode is enabled.
                        loaded.EncryptedApiKey = string.Empty;
                        migrated = true;
                    }

                    _config = loaded;
                    if (migrated)
                    {
                        SaveConfigInternal();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"LoadConfig Error: {ex.Message}");
                    _config = new DutyConfig();
                    _config.DutyReminderTimes = NormalizeDutyReminderTimes(_config.DutyReminderTimes);
                    SaveConfigInternal();
                }
            }
            
            _config.PropertyChanged += OnConfigPropertyChangedCurrent;
        }

        ConfigChanged?.Invoke(this, _config!);
    }

    private void SaveConfigInternal()
    {
        ConfigureFileHelper.SaveConfig(_configPath, _config);
    }

    public void SaveConfig()
    {
        lock (_configLock)
        {
            SaveConfigInternal();
        }
    }

    private void OnConfigPropertyChangedCurrent(object? sender, PropertyChangedEventArgs e)
    {
        SaveConfig();
    }

    private void InitializeWatcher(string dataDir)
    {
        try
        {
            _watcher = new FileSystemWatcher(dataDir, "config.json")
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
        Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            LoadConfigInternal();
        });
    }

    private static System.Collections.Generic.List<string> NormalizeDutyReminderTimes(
        System.Collections.Generic.List<string>? times)
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
        if (val.Length != 5) return null;
        if (!TimeSpan.TryParse(val + ":00", out var ts)) return null;

        var parts = val.Split(':');
        if (parts.Length != 2) return null;
        if (!int.TryParse(parts[0], out var h) || h < 0 || h > 23) return null;
        if (!int.TryParse(parts[1], out var m) || m < 0 || m > 59) return null;
        return val;
    }

    public void Dispose()
    {
        if (_disposed) return;
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
