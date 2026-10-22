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
/// Run 键值名在包身份下带 PFN 后缀：包身份进程写 HKCU 不重定向，zip 版与商店版
/// 若共用裸值名会互相覆盖自启开关，卸载一方会把另一方的自启一起带走。
/// </summary>
/// <remarks>
/// 两条线的加固在这里合流（2026-10-21）：
/// <list type="bullet">
///   <item><b>值名作用域与迁移</b>：包身份下用 <c>DutyAgent.&lt;PFN&gt;</c>，并把旧版写在
///   裸名 <c>DutyAgent</c> 上的"本包命令"迁移过来。只删命令完全匹配本包 AUMID 的旧值，
///   同机 zip 版写的 exe 路径值必须保留。</item>
///   <item><b>状态收敛与失败可见</b>：原来 <c>ApplyRunKey</c> 的 catch 是空的，被组策略
///   锁定注册表时界面显示"已开启"但开机不自启、日志里查不到原因；坏值也不会自愈。
///   现在收敛决策抽成纯函数（<see cref="ClassifyRunKeyValue"/> /
///   <see cref="PlanRunKeyAction"/>），坏值一律重写，每一步都留日志。</item>
/// </list>
/// </remarks>
internal static class AutoStartManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>裸值名。zip 版一直用它；旧版商店版也写在这里，是迁移的来源。</summary>
    private const string RunValueName = "DutyAgent";

    private const string StartupTaskId = "DutyAgentStartup";

    /// <summary>Run 键的现值相对目标命令的状态。</summary>
    internal enum RunKeyState
    {
        /// <summary>值不存在或为空。</summary>
        Absent,

        /// <summary>值正好等于目标命令，无需改动。</summary>
        Matches,

        /// <summary>值是 AUMID 激活命令但不是本包的（换签名 / 重装导致 PFN 变化 / 已卸载的商店版残留）。</summary>
        ForeignAumid,

        /// <summary>值是 exe 路径但目标文件已不存在（zip 版被移动或删除）。</summary>
        DeadExecutable,

        /// <summary>其他无法识别的值（旧格式、第三方写入等）。</summary>
        Unrecognised,
    }

    /// <summary>收敛动作。</summary>
    internal enum RunKeyAction
    {
        /// <summary>已经是目标状态，不动。</summary>
        Keep,

        /// <summary>写入目标命令（含自愈）。</summary>
        Write,

        /// <summary>删除值（含残留清理）。</summary>
        Remove,
    }

    /// <summary>当前形态下 Run 键值名的完整形式（包身份带 PFN 后缀）。</summary>
    private static string ScopedRunValueName => BuildRunValueName(PackageIdentityHelper.PackageFamilyName);

    /// <summary>纯函数形式：作用域由调用方注入，便于在无包身份的构建机上做回归测试。</summary>
    internal static string BuildRunValueName(string? family) =>
        string.IsNullOrWhiteSpace(family) ? RunValueName : $"{RunValueName}.{family}";

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

    /// <summary>
    /// 旧版商店版（迁移前）写在裸值名上的命令：本包 AUMID 激活。
    /// 只有确认是"本包自己"的旧值才允许删除，避免误伤同机 zip 版的 exe 启动项。
    /// </summary>
    internal static bool IsLegacyPackagedRunCommand(string? value, string? family) =>
        !string.IsNullOrWhiteSpace(family)
        && string.Equals(value, BuildRunCommand($"{family}!App"), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 分类 Run 键现值。纯函数，便于单测。<paramref name="currentExecutableExists"/>
    /// 由调用方注入（<c>File.Exists</c> 的结果），避免这里直接碰文件系统。
    /// </summary>
    internal static RunKeyState ClassifyRunKeyValue(
        string? current,
        string expectedCommand,
        bool currentExecutableExists)
    {
        if (string.IsNullOrWhiteSpace(current))
        {
            return RunKeyState.Absent;
        }

        if (string.Equals(current, expectedCommand, StringComparison.OrdinalIgnoreCase))
        {
            return RunKeyState.Matches;
        }

        if (IsAumidCommand(current))
        {
            return RunKeyState.ForeignAumid;
        }

        return currentExecutableExists ? RunKeyState.Unrecognised : RunKeyState.DeadExecutable;
    }

    /// <summary>
    /// 由状态 + 用户意图推出动作。核心规则：
    /// <list type="bullet">
    ///   <item>要开且现值坏了（含缺失）→ 写；坏值必须自愈，否则"开着但不生效"。</item>
    ///   <item>要关且现值还在 → 删；自己的值自己清干净。</item>
    ///   <item>其他情况不动，避免每次启动都去碰注册表。</item>
    /// </list>
    /// </summary>
    internal static RunKeyAction PlanRunKeyAction(RunKeyState state, bool desiredEnabled)
    {
        switch (state)
        {
            case RunKeyState.Matches:
                return desiredEnabled ? RunKeyAction.Keep : RunKeyAction.Remove;
            case RunKeyState.Absent:
                return desiredEnabled ? RunKeyAction.Write : RunKeyAction.Keep;
            default:
                return desiredEnabled ? RunKeyAction.Write : RunKeyAction.Remove;
        }
    }

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
            if (key is null)
            {
                return false;
            }

            if (key.GetValue(ScopedRunValueName) is string current
                && !string.IsNullOrWhiteSpace(current)
                && string.Equals(current, ExpectedCommand, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // 迁移期兼容：升级用户还停留在旧值名（无 PFN 后缀）上，视为已启用；
            // Apply 会把它迁移成带后缀的值名。
            return PackageIdentityHelper.IsPackaged
                && IsLegacyPackagedRunCommand(
                    key.GetValue(RunValueName) as string,
                    PackageIdentityHelper.PackageFamilyName);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 幂等应用目标状态；命令形态变化（升级换目录 / 安装形态切换 / 换签名）时自动改写。
    /// </summary>
    /// <returns>false 表示系统侧拒绝了该变更（如被组策略禁用）。</returns>
    public static bool Apply(bool enabled)
    {
        if (PackageIdentityHelper.IsPackaged)
        {
            var startupResult = ApplyStartupTask(enabled);
            if (startupResult.HasValue)
            {
                // startupTask 接管自启后，历史 Run 键必须清掉：否则用户在系统
                // "启动应用"里禁用后，下次登录仍会被 Run 键拉起。
                // 仅在"已启用成功"或"正在关闭"时清理；启用被策略拒绝时保留
                // Run 兜底，避免把用户已经打开的自启静默关掉。
                if (startupResult.Value || !enabled)
                {
                    CleanupRunValues();
                }

                return startupResult.Value;
            }

            ClientLog.Info("包身份下 startupTask 扩展不可用，自启回退为 HKCU Run 键（AUMID 启动）。");
        }

        return ApplyRunKey(enabled);
    }

    /// <summary>startupTask 可用时清理全部 Run 键痕迹（本包作用域值 + 本包旧命令）。</summary>
    private static void CleanupRunValues()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                return;
            }

            var hadScoped = key.GetValue(ScopedRunValueName) is string scoped
                && !string.IsNullOrWhiteSpace(scoped);
            var hadLegacy = IsLegacyPackagedRunCommand(
                key.GetValue(RunValueName) as string,
                PackageIdentityHelper.PackageFamilyName);

            if (!hadScoped && !hadLegacy)
            {
                return;
            }

            key.DeleteValue(ScopedRunValueName, throwOnMissingValue: false);
            if (hadLegacy)
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }

            ClientLog.Info("startupTask 已接管自启，清掉了 Run 键里的历史值（作用域值 / 旧裸值）。");
        }
        catch (Exception ex)
        {
            // 清理路径失败不能反过来阻塞自启生效，但必须留证据。
            ClientLog.Warn($"清理 Run 键历史值失败（不影响 startupTask 自启）：{ex.Message}");
        }
    }

    private static bool ApplyRunKey(bool enabled)
    {
        string? current;
        string? legacy;
        bool currentExecutableExists;
        try
        {
            using var readKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            current = readKey?.GetValue(ScopedRunValueName) as string;
            legacy = readKey?.GetValue(RunValueName) as string;
            currentExecutableExists =
                TryGetExecutablePath(current, out var path) && !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        }
        catch (Exception ex)
        {
            ClientLog.Warn($"读取自启注册表失败，本次跳过收敛：{ex.Message}");
            return false;
        }

        var state = ClassifyRunKeyValue(current, ExpectedCommand, currentExecutableExists);
        var action = PlanRunKeyAction(state, enabled);
        var legacyIsOurs = PackageIdentityHelper.IsPackaged
            && IsLegacyPackagedRunCommand(legacy, PackageIdentityHelper.PackageFamilyName);

        // 状态已正确、也没有待迁移的旧值时，不碰注册表。
        if (action == RunKeyAction.Keep && !legacyIsOurs)
        {
            return true;
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                ClientLog.Warn("打开 HKCU\\...\\Run 写键失败（返回 null），本次跳过收敛。");
                return false;
            }

            if (action == RunKeyAction.Write)
            {
                key.SetValue(ScopedRunValueName, ExpectedCommand, RegistryValueKind.String);
                ClientLog.Info(state == RunKeyState.Matches
                    ? $"开机自启已开启（值名 {ScopedRunValueName}）。"
                    : $"开机自启现值失效（{_stateNames[state]}），已自愈写入 {ScopedRunValueName}：{ExpectedCommand}");
            }
            else if (action == RunKeyAction.Remove)
            {
                key.DeleteValue(ScopedRunValueName, throwOnMissingValue: false);
                ClientLog.Info(state == RunKeyState.Absent
                    ? "开机自启已关闭。"
                    : $"开机自启已关闭，并清理了失效残留（{_stateNames[state]}）。");
            }

            if (legacyIsOurs)
            {
                // 迁移：旧版商店版把值写在裸名上，会和 zip 版互相覆盖。
                // 只删"本包命令"完全匹配的值，zip 版的 exe 路径值必须留着。
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
                ClientLog.Info("旧版裸值名 DutyAgent 上的自启项已迁移到 PFN 作用域值名。");
            }

            return true;
        }
        catch (Exception ex)
        {
            // 旧版本这里是空 catch。组策略锁定 Run 键时，"已开启"是假的，
            // 必须让日志里能查到为什么没生效。
            ClientLog.Error("写入自启注册表失败（可能被组策略锁定或权限不足）", ex);
            return false;
        }
    }

    private static readonly System.Collections.Generic.Dictionary<RunKeyState, string> _stateNames = new()
    {
        [RunKeyState.Absent] = "缺失",
        [RunKeyState.Matches] = "正常",
        [RunKeyState.ForeignAumid] = "失效的旧 AUMID",
        [RunKeyState.DeadExecutable] = "指向已删除的程序",
        [RunKeyState.Unrecognised] = "无法识别的值",
    };

    /// <summary>值是否是 <c>explorer.exe shell:AppsFolder\...</c> 形态的 AUMID 激活命令。</summary>
    private static bool IsAumidCommand(string value) =>
        value.TrimStart('"')
            .StartsWith("explorer.exe shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 从 Run 键命令里取出可执行文件路径。支持 <c>"C:\path\app.exe" --silent</c> 与裸路径两种形态。
    /// </summary>
    internal static bool TryGetExecutablePath(string? command, out string? path)
    {
        path = null;
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        var value = command.Trim();
        if (value.StartsWith("\"", StringComparison.Ordinal))
        {
            var end = value.IndexOf('"', 1);
            if (end <= 1)
            {
                return false;
            }

            path = value[1..end];
            return !string.IsNullOrWhiteSpace(path);
        }

        var space = value.IndexOf(' ');
        path = space < 0 ? value : value[..space];
        return !string.IsNullOrWhiteSpace(path);
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
                        ClientLog.Warn("startupTask 被组策略禁用，开机自启不会生效。");
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
        catch (Exception ex)
        {
            ClientLog.Error("操作 startupTask 失败", ex);
            return false;
        }
    }
}
