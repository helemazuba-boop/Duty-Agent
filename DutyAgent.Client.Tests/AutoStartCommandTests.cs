using System;
using DutyAgent.Client;
using Xunit;

namespace DutyAgent.Client.Tests;

/// <summary>
/// 开机自启 Run 键的构造规则与收敛决策。
/// 线上背景：
///  <list type="bullet">
///    <item>商店版未声明 desktop6:startupTask 扩展时（本机工具链无法校验该扩展），
///    自启回退到 HKCU Run 键；命令必须是 AUMID 激活而非安装目录 exe 直跑，
///    并经实测（2026-10-13）确认可从非包进程拉起。</item>
///    <item>收敛规则（<see cref="AutoStartManager.PlanRunKeyAction"/>）与状态分类
///    （<see cref="AutoStartManager.ClassifyRunKeyValue"/>）是 2026-10 加固新增的纯函数，
///    用来消灭"自启开着但不生效"和"卸载后留 AUMID 残留"两类问题。</item>
///  </list>
/// </summary>
public sealed class AutoStartCommandTests
{
    private const string UnpackagedExpected = "\"C:\\Apps\\DutyAgent\\duty-agent.exe\" --silent";
    private const string PackagedExpected =
        "explorer.exe shell:AppsFolder\\DutyAgent_3ecm5aq137n80!App";

    [Fact]
    public void PackagedRunCommand_UsesAumidActivation()
    {
        var command = AutoStartManager.BuildRunCommand("DutyAgent_3ecm5aq137n80!App");

        // 走 shell:AppsFolder AUMID 激活，不能出现安装目录 exe 直跑。
        Assert.Contains("shell:AppsFolder\\", command, StringComparison.Ordinal);
        Assert.DoesNotContain("duty-agent.exe", command, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("DutyAgent_3ecm5aq137n80!App", "explorer.exe shell:AppsFolder\\DutyAgent_3ecm5aq137n80!App")]
    [InlineData("Contoso.App_abc123!App", "explorer.exe shell:AppsFolder\\Contoso.App_abc123!App")]
    public void PackagedRunCommand_ExactFormat(string aumid, string expected)
    {
        Assert.Equal(expected, AutoStartManager.BuildRunCommand(aumid));
    }

    // ======== ClassifyRunKeyValue ========

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Classify_MissingValue_IsAbsent(string? current)
    {
        Assert.Equal(
            AutoStartManager.RunKeyState.Absent,
            AutoStartManager.ClassifyRunKeyValue(current, PackagedExpected, currentExecutableExists: false));
    }

    [Fact]
    public void Classify_ExactlyExpectedValue_IsMatches()
    {
        Assert.Equal(
            AutoStartManager.RunKeyState.Matches,
            AutoStartManager.ClassifyRunKeyValue(PackagedExpected, PackagedExpected, currentExecutableExists: false));
    }

    [Fact]
    public void Classify_MatchIgnoresCase()
    {
        // 注册表字符串比较走 OrdinalIgnoreCase；大小写不同不应误判成坏值。
        Assert.Equal(
            AutoStartManager.RunKeyState.Matches,
            AutoStartManager.ClassifyRunKeyValue(
                "EXPLORER.EXE SHELL:APPSFOLDER\\DUTYAGENT_3ECM5AQ137N80!APP",
                PackagedExpected,
                currentExecutableExists: false));
    }

    [Fact]
    public void Classify_AumidFromDifferentPackage_IsForeignAumid()
    {
        // 换签名 / 重装导致 PFN 变化后，旧 AUMID 一定拉起失败；必须是 ForeignAumid 才能被清。
        Assert.Equal(
            AutoStartManager.RunKeyState.ForeignAumid,
            AutoStartManager.ClassifyRunKeyValue(
                "explorer.exe shell:AppsFolder\\DutyAgent_deadbeefdead!App",
                PackagedExpected,
                currentExecutableExists: false));
    }

    [Fact]
    public void Classify_AumidWhenExpectedIsExePath_IsForeignAumid()
    {
        // 商店版残留：当前跑 zip 版（期望是 exe 路径），值里却是 AUMID。
        // 这条路径决定"从商店版退回 zip 版时能不能自愈"。
        Assert.Equal(
            AutoStartManager.RunKeyState.ForeignAumid,
            AutoStartManager.ClassifyRunKeyValue(
                PackagedExpected,
                UnpackagedExpected,
                currentExecutableExists: false));
    }

    [Fact]
    public void Classify_ExePathPointingNowhere_IsDeadExecutable()
    {
        Assert.Equal(
            AutoStartManager.RunKeyState.DeadExecutable,
            AutoStartManager.ClassifyRunKeyValue(
                "\"C:\\Old\\duty-agent.exe\" --silent",
                UnpackagedExpected,
                currentExecutableExists: false));
    }

    [Fact]
    public void Classify_ExePathStillPresent_IsUnrecognised()
    {
        // 程序还在，但既不是期望值也不是 AUMID：不猜，交给上层覆盖。
        Assert.Equal(
            AutoStartManager.RunKeyState.Unrecognised,
            AutoStartManager.ClassifyRunKeyValue(
                "\"D:\\Other\\duty-agent.exe\" --silent",
                UnpackagedExpected,
                currentExecutableExists: true));
    }

    [Fact]
    public void Classify_RandomJunk_FallsBackToDeadExecutable()
    {
        // 既不是期望值也不是 AUMID 的杂串，按"失效值"处理（TryGetExecutablePath 解出的
        // 路径不存在）：宁可自愈覆盖掉，也绝不能当成"已启用"。
        Assert.Equal(
            AutoStartManager.RunKeyState.DeadExecutable,
            AutoStartManager.ClassifyRunKeyValue(
                "not a command at all",
                UnpackagedExpected,
                currentExecutableExists: false));
    }

    // ======== PlanRunKeyAction ========

    [Theory]
    [InlineData(AutoStartManager.RunKeyState.Matches, true, AutoStartManager.RunKeyAction.Keep)]
    [InlineData(AutoStartManager.RunKeyState.Matches, false, AutoStartManager.RunKeyAction.Remove)]
    [InlineData(AutoStartManager.RunKeyState.Absent, true, AutoStartManager.RunKeyAction.Write)]
    [InlineData(AutoStartManager.RunKeyState.Absent, false, AutoStartManager.RunKeyAction.Keep)]
    // 自愈：坏了就必须重写，不能只报"未启用"。
    [InlineData(AutoStartManager.RunKeyState.DeadExecutable, true, AutoStartManager.RunKeyAction.Write)]
    [InlineData(AutoStartManager.RunKeyState.ForeignAumid, true, AutoStartManager.RunKeyAction.Write)]
    [InlineData(AutoStartManager.RunKeyState.Unrecognised, true, AutoStartManager.RunKeyAction.Write)]
    // 清理：用户不要自启时，自己的失效残留也要一并删掉。
    [InlineData(AutoStartManager.RunKeyState.DeadExecutable, false, AutoStartManager.RunKeyAction.Remove)]
    [InlineData(AutoStartManager.RunKeyState.ForeignAumid, false, AutoStartManager.RunKeyAction.Remove)]
    [InlineData(AutoStartManager.RunKeyState.Unrecognised, false, AutoStartManager.RunKeyAction.Remove)]
    public void Plan_ActionMatrix(
        AutoStartManager.RunKeyState state,
        bool desiredEnabled,
        AutoStartManager.RunKeyAction expected)
    {
        Assert.Equal(expected, AutoStartManager.PlanRunKeyAction(state, desiredEnabled));
    }

    [Fact]
    public void Plan_EveryState_WithEitherIntent_IsDefined()
    {
        // 将来新增 RunKeyState 而忘了补 Plan，这里会失败（提醒必须显式表态）。
        foreach (var state in Enum.GetValues<AutoStartManager.RunKeyState>())
        {
            Assert.True(Enum.IsDefined(typeof(AutoStartManager.RunKeyAction),
                (object)AutoStartManager.PlanRunKeyAction(state, desiredEnabled: true)));
            Assert.True(Enum.IsDefined(typeof(AutoStartManager.RunKeyAction),
                (object)AutoStartManager.PlanRunKeyAction(state, desiredEnabled: false)));
        }
    }

    // ======== TryGetExecutablePath ========

    [Fact]
    public void TryGetExecutablePath_QuotedPathWithArgs()
    {
        Assert.True(AutoStartManager.TryGetExecutablePath("\"C:\\Program Files\\Duty\\duty-agent.exe\" --silent", out var path));
        Assert.Equal(@"C:\Program Files\Duty\duty-agent.exe", path);
    }

    [Fact]
    public void TryGetExecutablePath_BarePath()
    {
        Assert.True(AutoStartManager.TryGetExecutablePath(@"C:\Duty\duty-agent.exe", out var path));
        Assert.Equal(@"C:\Duty\duty-agent.exe", path);
    }

    [Fact]
    public void TryGetExecutablePath_UnquotedPathWithSpace_StopsAtFirstSpace()
    {
        // 记录当前行为：不加引号的含空格路径会被截断。调用方拿到的串
        // File.Exists 为 false，会被归为 DeadExecutable —— 宁可自愈也不误判为有效。
        Assert.True(AutoStartManager.TryGetExecutablePath(@"C:\Program Files\duty-agent.exe", out var path));
        Assert.Equal(@"C:\Program", path);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"\" --silent")]
    public void TryGetExecutablePath_EmptyForms_ReturnFalse(string? command)
    {
        Assert.False(AutoStartManager.TryGetExecutablePath(command, out var path));
        Assert.Null(path);
    }
}
