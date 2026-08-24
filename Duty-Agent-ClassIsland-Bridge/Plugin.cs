using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using ClassIsland.Core;
using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Extensions.Registry;
using ClassIsland.Shared;
using DutyAgentBridge.Controls;
using DutyAgentBridge.Controls.RuleSettingsControls;
using DutyAgentBridge.Models;
using DutyAgentBridge.Services;
using DutyAgentBridge.Services.Automations.Actions;
using DutyAgentBridge.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DutyAgentBridge;

[PluginEntrance]
public class Plugin : PluginBase
{
    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        services.AddSingleton<IBridgePaths, BridgePaths>();
        services.AddSingleton<IIpcBridgeService, IpcBridgeService>();
        services.AddSingleton<IHealthMonitorService, HealthMonitorService>();
        services.AddSingleton<DutyRuleHandlerService>();

        services.AddNotificationProvider<DutyNotificationProvider>();
        services.AddComponent<DutyComponent, DutyComponentSettingsControl>();
        services.AddSettingsPage<DutyBridgeSettingsPage>();
        services.AddAction<DutyRunScheduleAction, DutyRunScheduleActionSettingsControl>();
        services.AddRule<DutyAssignedStudentRuleSettings, DutyAssignedStudentRuleSettingsControl>(
            DutyAutomationIds.TodayAssignedRule,
            "\u4ECA\u65E5\u503C\u65E5\u5339\u914D",
            "\uE8D4");

        InjectSettingsPageGroup(services);

        AppBase.Current.AppStarted += (_, _) =>
        {
            var paths = IAppHost.GetService<IBridgePaths>();
            Diagnostics.Initialize(paths.LogsDirectory);

            _ = IAppHost.GetService<IIpcBridgeService>().ConnectAsync();
            IAppHost.GetService<IHealthMonitorService>().Start();
            IAppHost.GetService<DutyRuleHandlerService>().Register();
        };

        AppBase.Current.AppStopping += (_, _) =>
        {
            IAppHost.GetService<IHealthMonitorService>().Stop();
            IAppHost.GetService<IIpcBridgeService>().Disconnect();
        };
    }

    private static void InjectSettingsPageGroup(IServiceCollection services)
    {
        try
        {
            var registeredSettingsPageInfos = ClassIsland.Core.Services.Registry.SettingsWindowRegistryService.Registered
                .Where(info => info.Id.StartsWith("duty-agent-bridge", StringComparison.Ordinal))
                .ToList();

            if (!InjectService.TryGetAddSettingsPageGroupMethod(out var addSettingsPageGroupMethod) ||
                addSettingsPageGroupMethod is null)
            {
                return;
            }

            addSettingsPageGroupMethod.Invoke(
                typeof(SettingsWindowRegistryExtensions),
                [services, "duty-agent-bridge.group", "\uE31E", "Duty-Agent \u6865\u63A5"]);

            var groupIdProperty = InjectService.GetSettingsPageInfoGroupIdProperty();
            if (groupIdProperty is null)
            {
                return;
            }

            foreach (var info in registeredSettingsPageInfos)
            {
                groupIdProperty.SetValue(info, "duty-agent-bridge.group");
            }
        }
        catch
        {
        }
    }
}

public interface IBridgePaths
{
    string PluginConfigFolder { get; }

    string SharedConfigFolder { get; }

    string MetaFilePath { get; }

    string LogsDirectory { get; }

    string ConfigFolder { get; }

    string ConfigSource { get; }

    IReadOnlyList<BridgeConfigCandidate> ConfigCandidates { get; }
}

public sealed class BridgePaths : IBridgePaths
{
    private const string MetaFileName = ".duty-agent-meta.json";
    private const string EnvVarName = "CLASSISLAND_CONFIG_PATH";

    public string PluginConfigFolder { get; }

    public string SharedConfigFolder { get; }

    public string MetaFilePath { get; }

    public string LogsDirectory { get; }

    public string ConfigFolder { get; }

    public string ConfigSource { get; }

    public IReadOnlyList<BridgeConfigCandidate> ConfigCandidates { get; }

    public BridgePaths()
    {
        var discovery = DiscoverClassIslandConfigFolder();
        ConfigFolder = discovery.ConfigFolder;
        ConfigSource = discovery.Source;
        ConfigCandidates = discovery.Candidates;

        SharedConfigFolder = Path.Combine(ConfigFolder, "DutyAgentBridge");
        PluginConfigFolder = SharedConfigFolder;
        MetaFilePath = Path.Combine(SharedConfigFolder, MetaFileName);
        LogsDirectory = Path.Combine(SharedConfigFolder, "logs");

        Directory.CreateDirectory(SharedConfigFolder);
        Directory.CreateDirectory(LogsDirectory);
    }

    private static BridgeConfigDiscovery DiscoverClassIslandConfigFolder()
    {
        var candidates = new List<(string Path, string Source)>();
        AddCandidate(candidates, Environment.GetEnvironmentVariable(EnvVarName), EnvVarName);

        var assemblyDirectory = Path.GetDirectoryName(typeof(BridgePaths).Assembly.Location);
        foreach (var candidate in EnumerateConfigCandidatesFromPath(assemblyDirectory))
        {
            AddCandidate(candidates, candidate, "plugin-location");
        }

        foreach (var process in Process.GetProcessesByName("ClassIsland"))
        {
            string? executablePath = null;
            try
            {
                executablePath = process.MainModule?.FileName;
            }
            catch
            {
            }

            foreach (var candidate in EnumerateConfigCandidatesFromPath(Path.GetDirectoryName(executablePath)))
            {
                AddCandidate(candidates, candidate, "running-process");
            }
        }

        AddCandidate(candidates, GetAppDataConfigFolder(), "appdata");

        var normalized = candidates
            .Select(candidate => NormalizeCandidate(candidate.Path, candidate.Source))
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.ConfigFolder))
            .DistinctBy(candidate => candidate.ConfigFolder, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalized.Count == 0)
        {
            var fallback = GetAppDataConfigFolder();
            normalized.Add(new BridgeConfigCandidate(
                fallback,
                "appdata",
                Directory.Exists(fallback),
                HasLiveMetaFile(fallback)));
        }

        var selected =
            normalized.FirstOrDefault(candidate => candidate.Source == EnvVarName) ??
            normalized.FirstOrDefault(candidate => candidate.Source == "plugin-location" && candidate.Exists) ??
            normalized.FirstOrDefault(candidate => candidate.Source == "running-process" && candidate.Exists) ??
            normalized.FirstOrDefault(candidate => candidate.HasLiveMeta) ??
            normalized.FirstOrDefault(candidate => candidate.Source == "appdata") ??
            normalized.FirstOrDefault(candidate => candidate.Exists) ??
            normalized[0];

        return new BridgeConfigDiscovery(selected.ConfigFolder, selected.Source, normalized);
    }

    private static void AddCandidate(List<(string Path, string Source)> candidates, string? path, string source)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            candidates.Add((path, source));
        }
    }

    private static BridgeConfigCandidate NormalizeCandidate(string rawPath, string source)
    {
        var configFolder = NormalizeConfigFolder(rawPath);
        return new BridgeConfigCandidate(
            configFolder,
            source,
            Directory.Exists(configFolder),
            HasLiveMetaFile(configFolder));
    }

    private static bool HasLiveMetaFile(string configFolder)
    {
        try
        {
            var metaPath = Path.Combine(configFolder, "DutyAgentBridge", MetaFileName);
            if (!File.Exists(metaPath))
            {
                return false;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(metaPath));
            if (!document.RootElement.TryGetProperty("pid", out var pidElement) ||
                !pidElement.TryGetInt32(out var pid) ||
                pid <= 0)
            {
                return false;
            }

            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<string> EnumerateConfigCandidatesFromPath(string? startPath)
    {
        if (string.IsNullOrWhiteSpace(startPath))
        {
            yield break;
        }

        var current = new DirectoryInfo(startPath);
        while (current is not null)
        {
            yield return Path.Combine(current.FullName, "data", "Config");

            if (Directory.Exists(Path.Combine(current.FullName, "Plugins")) ||
                Directory.Exists(Path.Combine(current.FullName, "Config")) ||
                File.Exists(Path.Combine(current.FullName, "Settings.json")))
            {
                yield return Path.Combine(current.FullName, "Config");
            }

            current = current.Parent;
        }
    }

    private static string NormalizeConfigFolder(string rawPath)
    {
        var expanded = Environment.ExpandEnvironmentVariables(rawPath.Trim().Trim('"'));
        var fullPath = Path.GetFullPath(expanded);
        if (File.Exists(fullPath))
        {
            fullPath = Path.GetDirectoryName(fullPath) ?? fullPath;
        }

        var directory = new DirectoryInfo(fullPath);
        if (directory.Name.Equals("DutyAgentBridge", StringComparison.OrdinalIgnoreCase) &&
            directory.Parent is not null)
        {
            return directory.Parent.FullName;
        }

        if (directory.Name.Equals("Config", StringComparison.OrdinalIgnoreCase))
        {
            return directory.FullName;
        }

        var dataConfig = Path.Combine(fullPath, "data", "Config");
        if (Directory.Exists(dataConfig) ||
            Directory.Exists(Path.Combine(fullPath, "data", "Plugins")) ||
            File.Exists(Path.Combine(fullPath, "ClassIsland.exe")))
        {
            return Path.GetFullPath(dataConfig);
        }

        var directConfig = Path.Combine(fullPath, "Config");
        if (Directory.Exists(directConfig) ||
            Directory.Exists(Path.Combine(fullPath, "Plugins")) ||
            File.Exists(Path.Combine(fullPath, "Settings.json")))
        {
            return Path.GetFullPath(directConfig);
        }

        return fullPath;
    }

    private static string GetAppDataConfigFolder()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClassIsland",
            "Config");
    }
}

internal static class InjectService
{
    private static MethodInfo? _addSettingsPageGroupMethod;
    private static PropertyInfo? _settingsPageInfoGroupIdProperty;
    private static bool _initialized;

    public static bool TryGetAddSettingsPageGroupMethod(out MethodInfo? method)
    {
        EnsureInitialized();
        method = _addSettingsPageGroupMethod;
        return method is not null;
    }

    public static PropertyInfo? GetSettingsPageInfoGroupIdProperty()
    {
        EnsureInitialized();
        return _settingsPageInfoGroupIdProperty;
    }

    private static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        try
        {
            var extType = typeof(SettingsWindowRegistryExtensions);
            _addSettingsPageGroupMethod = extType.GetMethod(
                "AddSettingsPageGroup",
                [typeof(IServiceCollection), typeof(string), typeof(string), typeof(string)]);

            var infoType = Type.GetType("ClassIsland.Core.Services.Registry.SettingsPageInfo, ClassIsland.Core")!;
            _settingsPageInfoGroupIdProperty = infoType.GetProperty("GroupId");
        }
        catch
        {
        }
    }
}
