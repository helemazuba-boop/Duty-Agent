using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Extensions.Registry;
using DutyAgent.Controls.Components;
using DutyAgent.Models;
using DutyAgent.Services;
using DutyAgent.Services.NotificationProviders;
using DutyAgent.Views.SettingPages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Text.Json;

namespace DutyAgent;

[PluginEntrance]
public class Plugin : PluginBase
{
    private static readonly string PluginBaseDirectory =
        Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? AppContext.BaseDirectory;

    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        services.AddSingleton<DutyBackendService>();
        services.AddSingleton<DutyNotificationService>();
        services.AddComponent<DutyComponent, DutyComponentSettings>();
        services.AddNotificationProvider<DutyNotificationProvider>();
        services.AddSettingsPage<DutyMainSettingsPage>();

        var bootstrap = ReadBootstrapFlags();
        if (bootstrap.EnableMcp || bootstrap.EnableWebViewDebugLayer)
        {
            services.AddSingleton<DutyLocalPreviewHostedService>();
            services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<DutyLocalPreviewHostedService>());
        }

        if (bootstrap.EnableWebViewDebugLayer)
        {
            services.AddSettingsPage<DutyWebSettingsPage>();
        }

        AppDomain.CurrentDomain.ProcessExit += (_, _) => CleanupPythonProcesses();
    }

    private static BootstrapFlags ReadBootstrapFlags()
    {
        try
        {
            var configPath = Path.Combine(PluginBaseDirectory, "Assets_Duty", "data", "config.json");
            if (!File.Exists(configPath))
            {
                return BootstrapFlags.Disabled;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            var root = doc.RootElement;
            return new BootstrapFlags(
                EnableMcp: TryReadBoolean(root, "enable_mcp"),
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

    private readonly record struct BootstrapFlags(bool EnableMcp, bool EnableWebViewDebugLayer)
    {
        public static BootstrapFlags Disabled => new(false, false);
    }
}
