using ClassIsland.Core;
using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Extensions.Registry;
using DutyAgent.Controls.Automations.ActionSettingsControls;
using DutyAgent.Controls.Automations.RuleSettingsControls;
using DutyAgent.Controls.Components;
using DutyAgent.Models;
using DutyAgent.Models.Automations.Rules;
using DutyAgent.Services;
using DutyAgent.Services.NotificationProviders;
using DutyAgent.Services.Automations.Actions;
using DutyAgent.Services.Automations.Triggers;
using DutyAgent.Views.SettingPages;
using ClassIsland.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Text.Json;
using DutyAgent.Shared;

namespace DutyAgent;

[PluginEntrance]
public class Plugin : PluginBase
{
    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        var pluginPaths = DutyPluginPaths.CreatePrepared(PluginConfigFolder, Info.PluginFolderPath);
        DutyDiagnosticsLogger.Configure(pluginPaths.LogsDirectory);

        services.AddSingleton(pluginPaths);
        services.AddSingleton<IConfigManager, DutyConfigManager>();
        services.AddSingleton<IStateAndRosterManager, DutyStateManager>();
        services.AddSingleton<IPythonIpcService, DutyPythonIpcService>();
        services.AddSingleton<DutyScheduleOrchestrator>();
        services.AddSingleton<DutyAutomationBridgeService>();
        services.AddSingleton<DutyRuleHandlerService>();
        services.AddSingleton<DutyPluginLifecycle>();
        services.AddSingleton<DutyNotificationService>();
        services.AddComponent<DutyComponent, DutyComponentSettingsControl>();
        services.AddNotificationProvider<DutyNotificationProvider>();
        services.AddSettingsPage<DutyMainSettingsPage>();
        services.AddAction<RunDutyScheduleAction, RunDutyScheduleActionSettingsControl>();
        services.AddTrigger<DutyScheduleRunSucceededTrigger>();
        services.AddTrigger<DutyScheduleRunFailedTrigger>();
        services.AddTrigger<DutyScheduleUpdatedTrigger>();
        services.AddRule<DutyAssignedStudentRuleSettings, DutyAssignedStudentRuleSettingsControl>(
            DutyAutomationIds.TodayAssignedRule,
            "\u4eca\u65e5\u503c\u65e5\u5339\u914d",
            "\uE8D4");

        var bootstrap = ReadBootstrapFlags(pluginPaths);
        if (bootstrap.EnableWebViewDebugLayer)
        {
            services.AddSettingsPage<DutyWebSettingsPage>();
        }

        // 动态反射，实现在低 PluginSdk 上使用高版本的分组功能
        var registeredSettingsPageInfos = ClassIsland.Core.Services.Registry.SettingsWindowRegistryService.Registered
            .Where(info => info.Id.StartsWith("duty-agent"))
            .ToList();

        if (InjectService.TryGetAddSettingsPageGroupMethod(out var addSettingsPageGroupMethod))
        {
            addSettingsPageGroupMethod.Invoke(typeof(SettingsWindowRegistryExtensions), [services, "duty-agent.group", "\uE31E", "Duty-Agent"]);
            var groupIdProperty = InjectService.GetSettingsPageInfoGroupIdProperty();
            foreach (var info in registeredSettingsPageInfos)
            {
                groupIdProperty.SetValue(info, "duty-agent.group");
            }
        }
        // fallback: 低版本 SDK 无分组 API 时不修改名称

        AppBase.Current.AppStarted += (_, _) =>
        {
            _ = IAppHost.GetService<DutyPluginLifecycle>().StartAsync();
            IAppHost.GetService<DutyRuleHandlerService>().Register();
        };

        AppBase.Current.AppStopping += (_, _) =>
        {
            try
            {
                IAppHost.GetService<DutyPluginLifecycle>().StopAsync().GetAwaiter().GetResult();
            }
            catch
            {
                CleanupPythonProcesses();
            }
        };
    }

    private static BootstrapFlags ReadBootstrapFlags(DutyPluginPaths pluginPaths)
    {
        try
        {
            var configPath = File.Exists(pluginPaths.HostConfigPath)
                ? pluginPaths.HostConfigPath
                : pluginPaths.ConfigPath;
            if (!File.Exists(configPath))
            {
                return BootstrapFlags.Disabled;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            var root = doc.RootElement;
            return new BootstrapFlags(
                EnableWebViewDebugLayer: TryReadBoolean(root, "enable_webview_debug_layer"));
        }
        catch
        {
            return BootstrapFlags.Disabled;
        }
    }

    private static bool TryReadBoolean(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(propertyName, out var element))
        {
            return false;
        }

        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => element.TryGetInt64(out var number) && number != 0,
            JsonValueKind.String => ParseBooleanText(element.GetString()),
            _ => false
        };
    }

    private static bool ParseBooleanText(string? raw)
    {
        var text = (raw ?? string.Empty).Trim();
        if (bool.TryParse(text, out var value))
        {
            return value;
        }

        if (long.TryParse(text, out var number))
        {
            return number != 0;
        }

        return text.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
               text.Equals("on", StringComparison.OrdinalIgnoreCase) ||
               text.Equals("enabled", StringComparison.OrdinalIgnoreCase);
    }

    private static void CleanupPythonProcesses()
    {
        try
        {
            PythonProcessTracker.CleanupTrackedProcesses();
        }
        catch
        {
        }
    }

    private readonly record struct BootstrapFlags(bool EnableWebViewDebugLayer)
    {
        public static BootstrapFlags Disabled => new(false);
    }
}
