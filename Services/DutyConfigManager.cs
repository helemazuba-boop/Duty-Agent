using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
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
    private readonly string _backendConfigPath;
    private FileSystemWatcher? _watcher;
    private DutyConfig _config = new();
    private bool _disposed;
    private readonly object _configLock = new();

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
        _backendConfigPath = pluginPaths.ConfigPath;

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

            EnsureSplitConfigFiles();

            if (!File.Exists(_configPath))
            {
                _config = CreateDefaultHostConfig();
                SaveConfigInternal();
            }
            else
            {
                try
                {
                    var loaded = ConfigureFileHelper.LoadConfig<DutyConfig>(_configPath);
                    loaded.DutyReminderTimes = NormalizeDutyReminderTimes(loaded.DutyReminderTimes);
                    _config = loaded;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"LoadConfig Error: {ex.Message}");
                    _config = CreateDefaultHostConfig();
                    SaveConfigInternal();
                }
            }

            _config.PropertyChanged += OnConfigPropertyChangedCurrent;
        }

        ConfigChanged?.Invoke(this, _config!);
    }

    private void EnsureSplitConfigFiles()
    {
        if (File.Exists(_configPath))
        {
            return;
        }

        if (!File.Exists(_backendConfigPath))
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_backendConfigPath));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var hostConfig = ExtractHostConfig(root);
            hostConfig.DutyReminderTimes = NormalizeDutyReminderTimes(hostConfig.DutyReminderTimes);
            ConfigureFileHelper.SaveConfig(_configPath, hostConfig);

            var backendConfig = ExtractBackendConfig(root);
            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(_backendConfigPath, JsonSerializer.Serialize(backendConfig, options));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"EnsureSplitConfigFiles Error: {ex.Message}");
        }
    }

    private static DutyConfig ExtractHostConfig(JsonElement root)
    {
        var config = CreateDefaultHostConfig();
        config.PythonPath = ReadString(root, "python_path", config.PythonPath);
        config.AutoRunMode = ReadString(root, "auto_run_mode", config.AutoRunMode);
        config.AutoRunParameter = ReadString(root, "auto_run_parameter", config.AutoRunParameter);
        config.EnableMcp = ReadBoolean(root, "enable_mcp", config.EnableMcp);
        config.EnableWebViewDebugLayer = ReadBoolean(root, "enable_webview_debug_layer", config.EnableWebViewDebugLayer);
        config.AutoRunTime = ReadString(root, "auto_run_time", config.AutoRunTime);
        config.AutoRunTriggerNotificationEnabled = ReadBoolean(root, "auto_run_trigger_notification_enabled", config.AutoRunTriggerNotificationEnabled);
        config.AutoRunRetryTimes = ReadInt(root, "auto_run_retry_times", config.AutoRunRetryTimes);
        config.AiConsecutiveFailures = ReadInt(root, "ai_consecutive_failures", config.AiConsecutiveFailures);
        config.LastAutoRunDate = ReadString(root, "last_auto_run_date", config.LastAutoRunDate);
        config.ComponentRefreshTime = ReadString(root, "component_refresh_time", config.ComponentRefreshTime);
        config.NotificationDurationSeconds = ReadInt(root, "notification_duration_seconds", config.NotificationDurationSeconds);
        config.DutyReminderEnabled = ReadBoolean(root, "duty_reminder_enabled", config.DutyReminderEnabled);
        config.DutyReminderTimes = ReadStringList(root, "duty_reminder_times", config.DutyReminderTimes);
        return config;
    }

    private static DutyBackendConfig ExtractBackendConfig(JsonElement root)
    {
        var config = new DutyBackendConfig
        {
            ApiKey = ReadString(root, "api_key", string.Empty),
            BaseUrl = ReadString(root, "base_url", "https://integrate.api.nvidia.com/v1"),
            Model = ReadString(root, "model", "moonshotai/kimi-k2-thinking"),
            ModelProfile = DutyScheduleOrchestrator.NormalizeModelProfile(ReadString(root, "model_profile", "auto")),
            OrchestrationMode = DutyScheduleOrchestrator.NormalizeOrchestrationMode(ReadString(root, "orchestration_mode", "auto")),
            ProviderHint = ReadString(root, "provider_hint", string.Empty),
            PerDay = Math.Clamp(ReadInt(root, "per_day", 2), 1, 30),
            DutyRule = ReadString(root, "duty_rule", string.Empty)
        };
        return config;
    }

    private static DutyConfig CreateDefaultHostConfig()
    {
        return new DutyConfig
        {
            DutyReminderTimes = [DefaultDutyReminderTime]
        };
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
        Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
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
        if (val.Length != 5) return null;
        if (!TimeSpan.TryParse(val + ":00", out var ts)) return null;

        var parts = val.Split(':');
        if (parts.Length != 2) return null;
        if (!int.TryParse(parts[0], out var h) || h < 0 || h > 23) return null;
        if (!int.TryParse(parts[1], out var m) || m < 0 || m > 59) return null;
        return val;
    }

    private static string ReadString(JsonElement root, string propertyName, string fallback)
    {
        if (!root.TryGetProperty(propertyName, out var element))
        {
            return fallback;
        }

        return element.ValueKind == JsonValueKind.String ? (element.GetString() ?? fallback) : fallback;
    }

    private static bool ReadBoolean(JsonElement root, string propertyName, bool fallback)
    {
        if (!root.TryGetProperty(propertyName, out var element))
        {
            return fallback;
        }

        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => element.TryGetInt32(out var number) ? number != 0 : fallback,
            JsonValueKind.String when bool.TryParse(element.GetString(), out var parsed) => parsed,
            _ => fallback
        };
    }

    private static int ReadInt(JsonElement root, string propertyName, int fallback)
    {
        if (!root.TryGetProperty(propertyName, out var element))
        {
            return fallback;
        }

        return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value)
            ? value
            : fallback;
    }

    private static List<string> ReadStringList(JsonElement root, string propertyName, List<string> fallback)
    {
        if (!root.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.Array)
        {
            return fallback;
        }

        var values = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var value = item.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    values.Add(value.Trim());
                }
            }
        }

        return values.Count == 0 ? fallback : values;
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
