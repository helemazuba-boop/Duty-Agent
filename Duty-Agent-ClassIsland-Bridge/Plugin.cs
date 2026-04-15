using ClassIsland.Core;
using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Extensions.Registry;
using DutyAgentBridge.Controls;
using DutyAgentBridge.Models;
using DutyAgentBridge.Services;
using DutyAgentBridge.Services.Automations.Actions;
using DutyAgentBridge.Services.Automations.Rules;
using DutyAgentBridge.Views;
using ClassIsland.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.IO;

namespace DutyAgentBridge;

[PluginEntrance]
public class Plugin : PluginBase
{
    private const string MetaFileName = ".duty-agent-meta.json";

    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        // 注册路径服务
        services.AddSingleton<IBridgePaths, BridgePaths>();

        // 注册 IPC 桥接服务（核心）
        services.AddSingleton<IIpcBridgeService, IpcBridgeService>();
        services.AddSingleton<IHealthMonitorService, HealthMonitorService>();

        // 注册通知提供者（继承 ClassIsland 通知系统）
        services.AddNotificationProvider<DutyNotificationProvider>();

        // 注册桌面组件（继承 ClassIsland 组件系统）
        services.AddComponent<DutyComponent, DutyComponentSettingsControl>();

        // 注册设置页（简化版：仅显示联动状态）
        services.AddSettingsPage<DutyBridgeSettingsPage>();

        // 注册自动化 Action
        services.AddAction<DutyRunScheduleAction, DutyRunScheduleActionSettingsControl>();

        // 注册自动化 Rule
        services.AddRule<DutyAssignedStudentRuleSettings, DutyAssignedStudentRuleSettingsControl>(
            DutyAutomationIds.TodayAssignedRule,
            "\u4ECA\u65E5\u503C\u65E5\u5339\u914D",
            "\uE8D4");

        // 动态分组注入（兼容低版本 SDK）
        InjectSettingsPageGroup(services);

        // 应用启动：连接独立软件
        AppBase.Current.AppStarted += (_, _) =>
        {
            _ = IAppHost.GetService<IIpcBridgeService>().ConnectAsync();
            IAppHost.GetService<IHealthMonitorService>().Start();
            IAppHost.GetService<DutyRuleHandlerService>().Register();
        };

        // 应用停止：断开连接
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
                .Where(info => info.Id.StartsWith("duty-agent-bridge"))
                .ToList();

            if (InjectService.TryGetAddSettingsPageGroupMethod(out var addSettingsPageGroupMethod))
            {
                addSettingsPageGroupMethod.Invoke(
                    typeof(SettingsWindowRegistryExtensions),
                    [services, "duty-agent-bridge.group", "\uE31E", "Duty-Agent 桥接"]);

                var groupIdProperty = InjectService.GetSettingsPageInfoGroupIdProperty();
                foreach (var info in registeredSettingsPageInfos)
                {
                    groupIdProperty.SetValue(info, "duty-agent-bridge.group");
                }
            }
        }
        catch
        {
            // 低版本 SDK 不支持分组时静默忽略
        }
    }
}

/// <summary>
/// 桥接插件路径管理器
/// </summary>
public interface IBridgePaths
{
    /// <summary>插件配置文件夹</summary>
    string PluginConfigFolder { get; }

    /// <summary>ClassIsland 共享配置目录（用于存放元数据文件）</summary>
    string SharedConfigFolder { get; }

    /// <summary>元数据文件完整路径</summary>
    string MetaFilePath { get; }

    /// <summary>日志目录</summary>
    string LogsDirectory { get; }
}

public sealed class BridgePaths : IBridgePaths
{
    public string PluginConfigFolder { get; }

    public string SharedConfigFolder { get; }

    public string MetaFilePath { get; }

    public string LogsDirectory { get; }

    public BridgePaths()
    {
        // 获取 ClassIsland 配置目录（通过反射或环境变量）
        var configFolder = GetClassIslandConfigFolder();
        SharedConfigFolder = Path.Combine(configFolder, "DutyAgentBridge");
        PluginConfigFolder = SharedConfigFolder; // 桥接插件与独立软件共用同一目录

        Directory.CreateDirectory(SharedConfigFolder);
        Directory.CreateDirectory(LogsDirectory);

        MetaFilePath = Path.Combine(SharedConfigFolder, MetaFileName);
    }

    private static string GetClassIslandConfigFolder()
    {
        // 尝试从环境变量获取 ClassIsland 配置目录
        var envPath = Environment.GetEnvironmentVariable("CLASSISLAND_CONFIG_PATH");
        if (!string.IsNullOrWhiteSpace(envPath) && Directory.Exists(envPath))
        {
            return envPath;
        }

        // 回退到 AppData
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appData, "ClassIsland", "Config");
    }
}

/// <summary>
/// 反射工具（来自原始项目的 InjectService）
/// </summary>
internal static class InjectService
{
    private static MethodInfo? _addSettingsPageGroupMethod;
    private static PropertyInfo? _settingsPageInfoGroupIdProperty;
    private static bool _initialized;

    public static bool TryGetAddSettingsPageGroupMethod(out MethodInfo? method)
    {
        EnsureInitialized();
        method = _addSettingsPageGroupMethod;
        return method != null;
    }

    public static PropertyInfo? GetSettingsPageInfoGroupIdProperty()
    {
        EnsureInitialized();
        return _settingsPageInfoGroupIdProperty;
    }

    private static void EnsureInitialized()
    {
        if (_initialized) return;
        _initialized = true;

        try
        {
            var extType = typeof(SettingsWindowRegistryExtensions);
            _addSettingsPageGroupMethod = extType.GetMethod(
                "AddSettingsPageGroup",
                new[] { typeof(IServiceCollection), typeof(string), typeof(string), typeof(string) });

            var infoType = Type.GetType("ClassIsland.Core.Services.Registry.SettingsPageInfo, ClassIsland.Core")!;
            _settingsPageInfoGroupIdProperty = infoType.GetProperty("GroupId");
        }
        catch
        {
            // 反射失败时不设置，调用方会检查 null
        }
    }
}
