namespace DutyAgent.Services;

public sealed class DutyPluginPaths
{
    public string PluginFolderPath { get; }
    public string PluginConfigFolder { get; }

    public string AssetsDirectory => Path.Combine(PluginFolderPath, "Assets_Duty");
    public string DataDirectory => Path.Combine(PluginConfigFolder, "data");
    public string LogsDirectory => Path.Combine(PluginConfigFolder, "logs");

    public string ConfigPath => Path.Combine(DataDirectory, "config.json");
    public string HostConfigPath => Path.Combine(DataDirectory, "host-config.json");
    public string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    public string HostStatePath => Path.Combine(DataDirectory, "host-state.json");
    public string SettingsDraftPath => Path.Combine(DataDirectory, "settings-draft.json");
    public string StatePath => Path.Combine(DataDirectory, "state.json");
    public string RosterPath => Path.Combine(DataDirectory, "roster.csv");
    public string ProcessSnapshotPath => Path.Combine(DataDirectory, ".engine-process.json");
    public string ConfigLockPath => GetLockPath(ConfigPath);
    public string HostConfigLockPath => GetLockPath(HostConfigPath);
    public string SettingsLockPath => GetLockPath(SettingsPath);
    public string HostStateLockPath => GetLockPath(HostStatePath);
    public string SettingsDraftLockPath => GetLockPath(SettingsDraftPath);
    public string StateLockPath => GetLockPath(StatePath);
    public string RosterLockPath => GetLockPath(RosterPath);
    public string ProcessSnapshotLockPath => GetLockPath(ProcessSnapshotPath);

    public string CoreScriptPath => Path.Combine(AssetsDirectory, "core.py");
    public string EmbeddedPythonPath => Path.Combine(AssetsDirectory, "python-embed", "python.exe");
    public string WebDirectory => Path.Combine(AssetsDirectory, "web");
    public string WebIndexPath => Path.Combine(WebDirectory, "index.html");
    public string WebTestHtmlPath => Path.Combine(WebDirectory, "test.html");

    public string LegacyDataDirectory => Path.Combine(AssetsDirectory, "data");
    public string LegacyConfigPath => Path.Combine(LegacyDataDirectory, "config.json");
    public string LegacyStatePath => Path.Combine(LegacyDataDirectory, "state.json");
    public string LegacyRosterPath => Path.Combine(LegacyDataDirectory, "roster.csv");
    public string LegacyProcessSnapshotPath => Path.Combine(LegacyDataDirectory, ".engine-process.json");
    public string LegacyConfigLockPath => GetLockPath(LegacyConfigPath);
    public string LegacyStateLockPath => GetLockPath(LegacyStatePath);
    public string LegacyRosterLockPath => GetLockPath(LegacyRosterPath);
    public string LegacyProcessSnapshotLockPath => GetLockPath(LegacyProcessSnapshotPath);

    private DutyPluginPaths(string pluginConfigFolder, string pluginFolderPath)
    {
        PluginConfigFolder = pluginConfigFolder;
        PluginFolderPath = pluginFolderPath;
    }

    public static DutyPluginPaths CreatePrepared(string pluginConfigFolder, string pluginFolderPath)
    {
        var paths = new DutyPluginPaths(pluginConfigFolder, pluginFolderPath);
        paths.Prepare();
        return paths;
    }

    private void Prepare()
    {
        Directory.CreateDirectory(PluginConfigFolder);
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogsDirectory);

        MigrateWritableFile(LegacyConfigPath, ConfigPath);
        MigrateWritableFile(LegacyStatePath, StatePath);
        MigrateWritableFile(LegacyRosterPath, RosterPath);
    }

    private static void MigrateWritableFile(string sourcePath, string destinationPath)
    {
        if (File.Exists(destinationPath) || !File.Exists(sourcePath))
        {
            return;
        }

        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.Copy(sourcePath, destinationPath, overwrite: false);
        TryDeleteLegacySource(sourcePath);
    }

    private static string GetLockPath(string sourcePath) => sourcePath + ".lock";

    private static void TryDeleteLegacySource(string sourcePath)
    {
        try
        {
            File.Delete(sourcePath);
            var directory = Path.GetDirectoryName(sourcePath);
            if (!string.IsNullOrWhiteSpace(directory) &&
                Directory.Exists(directory) &&
                !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch
        {
        }
    }
}
