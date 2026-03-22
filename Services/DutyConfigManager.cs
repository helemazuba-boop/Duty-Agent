using DutyAgent.Models;

namespace DutyAgent.Services;

public interface IConfigManager
{
    DutyConfig Config { get; }
    void LoadConfig();
    void SaveConfig();
    DutyConfig UpdateConfig(Action<DutyConfig> update);
    event EventHandler<DutyConfig> ConfigChanged;
}

public sealed class DutyConfigManager : IConfigManager, IDisposable
{
    private readonly IDutySettingsRepository _repository;
    private readonly object _configLock = new();
    private DutyConfig _config = new();
    private bool _disposed;

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

    public DutyConfigManager(IDutySettingsRepository repository)
    {
        _repository = repository;
        _repository.SettingsChanged += OnRepositorySettingsChanged;
        LoadConfigInternal(raiseEvent: false);
    }

    public void LoadConfig() => LoadConfigInternal(raiseEvent: true);

    public void SaveConfig()
    {
        DutyConfig savedConfig;
        lock (_configLock)
        {
            savedConfig = _repository.ReplaceFromProjectedConfig(CloneConfig(_config));
            _config = CloneConfig(savedConfig);
        }

        ConfigChanged?.Invoke(this, savedConfig);
    }

    public DutyConfig UpdateConfig(Action<DutyConfig> update)
    {
        ArgumentNullException.ThrowIfNull(update);

        DutyConfig updatedConfig;
        lock (_configLock)
        {
            var next = CloneConfig(_config);
            update(next);
            updatedConfig = _repository.ReplaceFromProjectedConfig(next);
            _config = CloneConfig(updatedConfig);
        }

        ConfigChanged?.Invoke(this, updatedConfig);
        return updatedConfig;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _repository.SettingsChanged -= OnRepositorySettingsChanged;
    }

    private void LoadConfigInternal(bool raiseEvent)
    {
        DutyConfig loaded;
        lock (_configLock)
        {
            loaded = _repository.LoadProjectedHostConfig();
            _config = CloneConfig(loaded);
        }

        if (raiseEvent)
        {
            ConfigChanged?.Invoke(this, loaded);
        }
    }

    private void OnRepositorySettingsChanged(object? sender, DutySettingsChangedEventArgs e)
    {
        if (_disposed || (!e.HostSettingsChanged && !e.RuntimeStateChanged))
        {
            return;
        }

        DutyConfig current;
        lock (_configLock)
        {
            _config = CloneConfig(e.ProjectedHostConfig);
            current = _config;
        }

        ConfigChanged?.Invoke(this, current);
    }

    private static DutyConfig CloneConfig(DutyConfig source)
    {
        return new DutyConfig
        {
            Version = source.Version,
            PythonPath = source.PythonPath,
            AutoRunMode = source.AutoRunMode,
            AutoRunParameter = source.AutoRunParameter,
            AccessTokenMode = source.AccessTokenMode,
            StaticAccessTokenVerifier = source.StaticAccessTokenVerifier,
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
}
