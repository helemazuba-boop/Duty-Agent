using System;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace DutyAgent.Client;

/// <summary>
/// 开机自启执行器。三种形态分流：
/// <list type="bullet">
///   <item>包身份 + 清单声明了 <c>desktop6:startupTask</c> 扩展：走
///   <see cref="StartupTask"/>，状态由系统"启动应用"设置管理，卸载时由平台自动清理。</item>
///   <item>包身份 + 清单未声明该扩展（本仓库默认，原因见 Package.appxmanifest 注释）：
///   回退 HKCU\...\Run，命令为 <c>explorer.exe shell:AppsFolder\&lt;PFN&gt;!App</c>。
///   经验证：包身份进程写 HKCU Run 键会落入真实注册表（未重定向），登录时
///   explorer（非包进程）读得到；AUMID 激活可拉起全信任应用。</item>
///   <item>传统桌面（zip 分发）：维持原有 HKCU\...\Run 值行为。</item>
/// </list>
/// </summary>
internal static class AutoStartManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DutyAgent";
    private const string StartupTaskId = "DutyAgentStartup";

    /// <summary>当前形态下"已启用自启"时 Run 键应有的命令；用于判断现值是否仍然有效。</summary>
    private static string ExpectedCommand => PackageIdentityHelper.IsPackaged
        ? BuildRunCommand(WindowsShortcutActivator.AppUserModelId)
        : $"\"{Application.ExecutablePath}\" --silent";

    /// <summary>
    /// 包身份形态的 Run 键命令：经 AUMID 激活，而不是直接执行安装目录里的 exe
    /// （MSIX 安装目录默认不可直接执行）。实测可从非包进程（登录时的 explorer）拉起。
    /// </summary>
    internal static string BuildRunCommand(string appUserModelId) =>
        $"explorer.exe shell:AppsFolder\\{appUserModelId}";

    public static bool IsEnabled()
    {
        if (PackageIdentityHelper.IsPackaged)
        {
            var state = GetStartupTaskState();
            if (state == StartupTaskState.Enabled || state == StartupTaskState.EnabledByPolicy)
            {
                return true;
            }
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string current
                && !string.IsNullOrWhiteSpace(current)
                && string.Equals(current, ExpectedCommand, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>幂等应用目标状态；命令形态变化（升级换目录 / 安装形态切换）时自动改写。</summary>
    /// <returns>false 表示系统侧拒绝了该变更（如被组策略禁用）。</returns>
    public static bool Apply(bool enabled)
    {
        if (PackageIdentityHelper.IsPackaged)
        {
            var startupResult = ApplyStartupTask(enabled);
            if (startupResult.HasValue)
            {
                return startupResult.Value;
            }

            ClientLog.Info("包身份下 startupTask 扩展不可用，自启回退为 HKCU Run 键（AUMID 启动）。");
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
                var command = ExpectedCommand;
                if (key.GetValue(ValueName) is not string current || current != command)
                {
                    key.SetValue(ValueName, command, RegistryValueKind.String);
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
        var task = TryGetStartupTask();
        return task?.State ?? StartupTaskState.Disabled;
    }

    /// <summary>
    /// 取启动任务；清单未声明 desktop6:startupTask 时返回 null（调用方回退 Run 键）。
    /// </summary>
    private static StartupTask? TryGetStartupTask()
    {
        try
        {
            var task = StartupTask.GetAsync(StartupTaskId).AsTask().GetAwaiter().GetResult();
            // 保险：某些路径下查无此任务会返回 TaskId 为空/不匹配的实例而非抛异常。
            if (task is null || !string.Equals(task.TaskId, StartupTaskId, StringComparison.Ordinal))
            {
                return null;
            }

            return task;
        }
        catch
        {
            return null;
        }
    }

    /// <returns>null 表示 startupTask 扩展不可用，调用方应回退 Run 键。</returns>
    private static bool? ApplyStartupTask(bool enabled)
    {
        var task = TryGetStartupTask();
        if (task is null)
        {
            return null;
        }

        try
        {
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
