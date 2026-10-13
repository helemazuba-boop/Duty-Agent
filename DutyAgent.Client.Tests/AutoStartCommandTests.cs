using System;
using DutyAgent.Client;
using Xunit;

namespace DutyAgent.Client.Tests;

/// <summary>
/// 包身份下 Run 键自启命令的构造规则。
/// 线上背景：商店版未声明 desktop6:startupTask 扩展时（本机工具链无法校验该扩展），
/// 自启回退到 HKCU Run 键；命令必须是 AUMID 激活而非安装目录 exe 直跑，
/// 并经实测（2026-10-13）确认可从非包进程拉起。
/// </summary>
public sealed class AutoStartCommandTests
{
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
}
