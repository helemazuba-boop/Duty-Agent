using DutyAgent.Client;
using Xunit;

namespace DutyAgent.Client.Tests;

/// <summary>
/// Run 键值名作用域回归。
/// 背景：包身份进程写 HKCU\...\Run 不重定向（2026-10-13 实测），zip 版与商店版
/// 因此会写同一把键；共用裸值名会互相覆盖自启开关，升级期旧值名残留还可能
/// 造成同一次登录双启动。值名在包身份下必须带 PFN 后缀，旧值名只允许在
/// "确属本包命令"时迁移删除。
/// </summary>
public sealed class AutoStartRunValueTests
{
    private const string Family = "DutyAgent_3ecm5aq137n80";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Unpackaged_UsesBareValueName(string? family)
    {
        Assert.Equal("DutyAgent", AutoStartManager.BuildRunValueName(family));
    }

    [Fact]
    public void Packaged_AppendsFamily()
    {
        Assert.Equal($"DutyAgent.{Family}", AutoStartManager.BuildRunValueName(Family));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("explorer.exe shell:AppsFolder\\Other.Package_abc!App", false)]
    [InlineData("\"C:\\apps\\duty-agent.exe\" --silent", false)]
    [InlineData("explorer.exe shell:AppsFolder\\DutyAgent_3ecm5aq137n80!App", true)]
    [InlineData("EXPLORER.EXE SHELL:APPSFOLDER\\DUTYAGENT_3ECM5AQ137N80!APP", true)]
    public void LegacyPackagedRunCommand_Detection(string? value, bool expected)
    {
        Assert.Equal(expected, AutoStartManager.IsLegacyPackagedRunCommand(value, Family));
    }

    [Fact]
    public void LegacyDetection_RequiresFamily()
    {
        // 无包身份（zip 版）绝不能把 AUMID 命令判成"自己的旧值"而误删。
        Assert.False(AutoStartManager.IsLegacyPackagedRunCommand(
            "explorer.exe shell:AppsFolder\\DutyAgent_3ecm5aq137n80!App", null));
    }
}
