using Microsoft.Win32;

namespace DutyAgent.Client;

/// <summary>
/// 开机自启执行器：管理 HKCU\Software\Microsoft\Windows\CurrentVersion\Run 下的
/// DutyAgent 值（指向当前 exe + --silent，登录后静默驻留托盘）。仅当前用户，无需管理员。
/// </summary>
internal static class AutoStartManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DutyAgent";

    private static string ExpectedCommand => $"\"{Application.ExecutablePath}\" --silent";

    public static bool IsEnabled()
    {
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
    public static void Apply(bool enabled)
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
}
