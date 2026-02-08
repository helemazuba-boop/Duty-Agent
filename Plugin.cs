using System.Diagnostics;
using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Extensions.Registry;
using DutyIsland.Controls.Components;
using DutyIsland.Models;
using DutyIsland.Services;
using DutyIsland.Views.SettingPages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DutyIsland;

[PluginEntrance]
public class Plugin : PluginBase
{
    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        services.AddSingleton<DutyBackendService>();
        services.AddComponent<DutyComponent, DutyComponentSettings>();
        services.AddSettingsPage<DutyMainSettingsPage>();

        AppDomain.CurrentDomain.ProcessExit += (_, _) => CleanupPythonProcesses();
    }

    private static void CleanupPythonProcesses()
    {
        try
        {
            var targetProcesses = Process.GetProcessesByName("python")
                .Concat(Process.GetProcessesByName("core"));

            foreach (var proc in targetProcesses)
            {
                try
                {
                    if (proc.MainModule?.FileName.Contains("Assets_Duty") == true)
                    {
                        proc.Kill();
                    }
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }
}
