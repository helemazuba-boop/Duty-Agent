using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Extensions.Registry;
using DutyIsland.Controls.Components;
using DutyIsland.Models;
using DutyIsland.Services;
using DutyIsland.Services.NotificationProviders;
using DutyIsland.Views.SettingPages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DutyIsland;

[PluginEntrance]
public class Plugin : PluginBase
{
    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        services.AddSingleton<DutyLocalPreviewHostedService>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<DutyLocalPreviewHostedService>());
        services.AddSingleton<DutyBackendService>();
        services.AddSingleton<DutyNotificationService>();
        services.AddComponent<DutyComponent, DutyComponentSettings>();
        services.AddNotificationProvider<DutyNotificationProvider>();
        services.AddSettingsPage<DutyWebSettingsPage>();

        AppDomain.CurrentDomain.ProcessExit += (_, _) => CleanupPythonProcesses();
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
}
