using System;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace DutyAgent.Client;

/// <summary>
/// 开机自启执行器。两种形态分流：
/// <list type="bullet">
///   <item>包身份（微软商店 / MSIX）：走 <see cref="StartupTask"/>，状态由系统
///   "启动应用" 设置管理，卸载时由平台自动清理，不会残留注册表项。</item>
///   <item>传统桌面（zip 分发）：维持原有 HKCU\...\Run 值行为。</item>
/// </list>
/// </summary>
internal static class AutoStartManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DutyAgent";
    private const string StartupTaskId = "DutyAgentStartup";

    private static string ExpectedCommand => $"\"{Application.ExecutablePath}\" --silent";

    public static bool IsEnabled()
    {
        if (PackageIdentityHelper.IsPackaged)
        {
            var state = GetStartupTaskState();
            return state == StartupTaskState.Enabled || state == StartupTaskState.EnabledByPolicy;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string current && !string.IsNullOrWhiteSpace(current);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>幂等应用目标状态；exe 路径变化（如升级换目录）时自动改写命令。</summary>
    /// <returns>false 表示系统侧拒绝了该变更（如被组策略禁用）。</returns>
    public static bool Apply(bool enabled)
    {
        if (PackageIdentityHelper.IsPackaged)
        {
            return ApplyStartupTask(enabled);
        }

        ApplyRunKey(enabled);
        return true;
    }

    private static void ApplyRunKey(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                return;
            }

            if (enabled)
            {
                if (key.GetValue(ValueName) is not string current || current != ExpectedCommand)
                {
                    key.SetValue(ValueName, ExpectedCommand, RegistryValueKind.String);
                }
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // 注册表被组策略锁定等场景：静默失败，不影响主流程。
        }
    }

    private static StartupTaskState GetStartupTaskState()
    {
        try
        {
            var task = StartupTask.GetAsync(StartupTaskId).AsTask().GetAwaiter().GetResult();
            return task.State;
        }
        catch
        {
            return StartupTaskState.Disabled;
        }
    }

    private static bool ApplyStartupTask(bool enabled)
    {
        try
        {
            var task = StartupTask.GetAsync(StartupTaskId).AsTask().GetAwaiter().GetResult();
            if (enabled)
            {
                switch (task.State)
                {
                    case StartupTaskState.Enabled:
                    case StartupTaskState.EnabledByPolicy:
                        return true;
                    case StartupTaskState.DisabledByPolicy:
                        // 组策略禁用，任何调用都无效。
                        return false;
                    default:
                        var result = task.RequestEnableAsync().AsTask().GetAwaiter().GetResult();
                        return result == StartupTaskState.Enabled
                            || result == StartupTaskState.EnabledByPolicy;
                }
            }

            task.Disable();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
