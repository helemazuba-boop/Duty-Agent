using System.ComponentModel;
using System.IO;
using System.Text.Json;
using ClassIsland.Core;
using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Extensions.Registry;
using ClassIsland.Shared;
using ClassIsland.Shared.Helpers;
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
    public BridgeSettings Settings { get; private set; } = new();

    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        // 官方设置模式（ExamplePlugins/PluginWithSettingsPage）：设置存放在宿主分配的
        // PluginConfigFolder，ConfigureFileHelper 读写，INPC 变更即自动落盘，
        // 从而纳入宿主的配置备份体系。
        var settingsPath = Path.Combine(PluginConfigFolder, "Settings.json");
        Settings = ConfigureFileHelper.LoadConfig<BridgeSettings>(settingsPath);

        var paths = new BridgePaths(this);
        MigrateLegacySettings(paths, settingsPath);

        Settings.PropertyChanged += (_, _) =>
            ConfigureFileHelper.SaveConfig(settingsPath, Settings);

        services.AddSingleton<IBridgePaths>(paths);
        services.AddSingleton(Settings);
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

        services.AddSettingsPageGroup("duty-agent-bridge.group", "\uE31E", "Duty-Agent \u6865\u63A5");

        AppBase.Current.AppStarted += (_, _) =>
        {
            Diagnostics.Initialize(paths.LogsDirectory);

            if (Settings.AutoConnect)
            {
                _ = IAppHost.GetService<IIpcBridgeService>().ConnectAsync();
            }
            IAppHost.GetService<IHealthMonitorService>().Start();
            IAppHost.GetService<DutyRuleHandlerService>().Register();
        };

        AppBase.Current.AppStopping += (_, _) =>
        {
            IAppHost.GetService<IHealthMonitorService>().Stop();
            IAppHost.GetService<IIpcBridgeService>().Disconnect();
        };
    }

    /// <summary>
    /// 0.50.x 曾把设置自管在 SharedConfigFolder\bridge-settings.json（绕过宿主配置体系）。
    /// 迁移其取值到宿主 Settings.json 后删除旧文件。
    /// </summary>
    private void MigrateLegacySettings(BridgePaths paths, string newSettingsPath)
    {
        try
        {
            var legacyPath = Path.Combine(paths.LegacySharedConfigFolder, "bridge-settings.json");
            if (!File.Exists(legacyPath) || File.Exists(newSettingsPath))
            {
                // 新设置已存在时不做迁移（以宿主文件为准）。
                return;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(legacyPath));
            var root = document.RootElement;
            if (root.TryGetProperty("auto_connect", out var autoConnect) &&
                (autoConnect.ValueKind == JsonValueKind.True || autoConnect.ValueKind == JsonValueKind.False))
            {
                Settings.AutoConnect = autoConnect.GetBoolean();
            }
            if (root.TryGetProperty("connect_timeout_seconds", out var timeout) &&
                timeout.TryGetInt32(out var timeoutValue))
            {
                Settings.ConnectTimeoutSeconds = timeoutValue;
            }
            if (root.TryGetProperty("health_check_interval_ms", out var interval) &&
                interval.TryGetInt32(out var intervalValue))
            {
                Settings.HealthCheckIntervalMs = intervalValue;
            }

            // 迁移发生在 PropertyChanged 挂接之前，这里显式落盘一次。
            ConfigureFileHelper.SaveConfig(newSettingsPath, Settings);
            File.Delete(legacyPath);
            Diagnostics.Log("Plugin", "Migrated legacy bridge-settings.json into host Settings.json.");
        }
        catch (Exception ex)
        {
            Diagnostics.Log("Plugin", $"Legacy settings migration skipped: {ex.Message}", "WARN");
        }
    }
}

/// <summary>
/// 桥接路径：全部由宿主已知的固定布局推导，不再自建发现链。
/// </summary>
public interface IBridgePaths
{
    /// <summary>宿主为本插件分配的配置目录（&lt;AppConfig&gt;\Plugins\duty-agent-bridge）。</summary>
    string PluginConfigFolder { get; }

    /// <summary>与独立软件共享的桥接目录（&lt;AppConfig&gt;\DutyAgentBridge），承载 meta 文件契约。</summary>
    string SharedConfigFolder { get; }

    /// <summary>独立软件写入的 meta 文件完整路径。</summary>
    string MetaFilePath { get; }

    /// <summary>插件日志目录（PluginConfigFolder\logs）。</summary>
    string LogsDirectory { get; }

    /// <summary>meta 根目录的来源（host-layout / CLASSISLAND_CONFIG_PATH），用于诊断。</summary>
    string MetaSource { get; }
}

public sealed class BridgePaths : IBridgePaths
{
    private const string MetaFileName = ".duty-agent-meta.json";
    private const string EnvVarName = "CLASSISLAND_CONFIG_PATH";
    private const string SharedFolderName = "DutyAgentBridge";

    /// <summary>0.50.x 时代的共享目录（仅用于旧设置迁移）。</summary>
    internal string LegacySharedConfigFolder { get; }

    public string PluginConfigFolder { get; }
    public string SharedConfigFolder { get; }
    public string MetaFilePath { get; }
    public string LogsDirectory { get; }
    public string MetaSource { get; }

    public BridgePaths(Plugin plugin)
    {
        // 宿主 PluginService 固定按 <AppConfig>\Plugins\<manifest.id> 分配（源码已验证），
        // Initialize 调用时该属性已赋值，因此可确定性推出配置根。
        PluginConfigFolder = plugin.PluginConfigFolder;
        if (string.IsNullOrWhiteSpace(PluginConfigFolder))
        {
            PluginConfigFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ClassIsland", "Config", "Plugins", "duty-agent-bridge");
        }

        var appConfigRoot = Path.GetFullPath(Path.Combine(PluginConfigFolder, "..", ".."));
        SharedConfigFolder = Path.Combine(appConfigRoot, SharedFolderName);
        MetaFilePath = Path.Combine(SharedConfigFolder, MetaFileName);
        MetaSource = "host-layout";
        LegacySharedConfigFolder = SharedConfigFolder;

        // 便携/特殊部署逃生舱：显式指定 ClassIsland 配置目录时优先生效。
        if (Environment.GetEnvironmentVariable(EnvVarName) is { Length: > 0 } overrideRoot)
        {
            var configFolder = Path.GetFullPath(Environment.ExpandEnvironmentVariables(overrideRoot));
            SharedConfigFolder = Path.Combine(configFolder, SharedFolderName);
            MetaFilePath = Path.Combine(SharedConfigFolder, MetaFileName);
            MetaSource = EnvVarName;
        }

        LogsDirectory = Path.Combine(PluginConfigFolder, "logs");
        Directory.CreateDirectory(PluginConfigFolder);
        Directory.CreateDirectory(LogsDirectory);
    }
}
