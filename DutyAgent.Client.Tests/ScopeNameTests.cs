using System.Threading;
using DutyAgent.Client;
using Xunit;

namespace DutyAgent.Client.Tests;

/// <summary>
/// 命名对象作用域回归。
/// 线上事故形态：商店版客户端启动即崩，日志只有第一行，dump 是
/// <c>DirectoryNotFoundException: Could not find a part of the path
/// 'DutyAgent_xxx\Local\DutyAgent.Client.SingleInstance'</c>。
/// 根因是包名前缀被拼到了 <c>Local\</c> <b>之前</b>，Win32 于是把名字当文件路径。
/// zip 版取裸名无此症状，所以只在商店版暴露。
/// </summary>
public sealed class ScopeNameTests
{
    private const string Family = "DutyAgent_3ecm5aq137n80";

    [Fact]
    public void Unpackaged_ReturnsBareName()
    {
        Assert.Equal(@"Local\DutyAgent.Client.SingleInstance",
            PackageIdentityHelper.ApplyScope(@"Local\DutyAgent.Client.SingleInstance", null));
    }

    [Theory]
    [InlineData(@"Local\DutyAgent.Client.SingleInstance", @"Local\")]
    [InlineData(@"Local\DutyAgent.Client.Wake", @"Local\")]
    [InlineData(@"Global\something", @"Global\")]
    public void Packaged_InsertsScopeAfterRootPrefix(string name, string root)
    {
        var scoped = PackageIdentityHelper.ApplyScope(name, Family);

        // ① 根前缀必须留在最前——这是 DirectoryNotFoundException 的直接防线。
        Assert.StartsWith(root, scoped, StringComparison.Ordinal);
        // ② 包名紧跟前缀，之后才是原名（中间用点分隔，肉眼可辨）。
        Assert.Equal($"{root}{Family}.{name[root.Length..]}", scoped);
    }

    [Fact]
    public void Packaged_NoRootPrefix_AppendsFamily()
    {
        Assert.Equal($"BareName.{Family}",
            PackageIdentityHelper.ApplyScope("BareName", Family));
    }

    [Fact]
    public void Packaged_NeverStartsWithFamilyName()
    {
        // 这句话就是本次回归的全部意义：作用域名不得以包系列名开头。
        var scoped = PackageIdentityHelper.ApplyScope(@"Local\DutyAgent.Client.SingleInstance", Family);
        Assert.False(scoped.StartsWith(Family, StringComparison.Ordinal));
    }

    [Fact]
    public void ScopedNames_AreRealUsableSynchronizationObjects()
    {
        // 真正会崩的那一行就在这儿：名字不对劲时 new Mutex / new EventWaitHandle
        // 直接抛 DirectoryNotFoundException，连日志都来不及写第二行。
        var mutexName = PackageIdentityHelper.ApplyScope(
            @"Local\DutyAgent.Client.SingleInstance-Test", Family);
        var eventName = PackageIdentityHelper.ApplyScope(
            @"Local\DutyAgent.Client.Wake-Test", Family);

        using var mutex = new Mutex(initiallyOwned: true, mutexName, out var isFirst);
        using var wakeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);

        Assert.True(isFirst);
        Assert.True(Mutex.TryOpenExisting(mutexName, out _));
        Assert.True(EventWaitHandle.TryOpenExisting(eventName, out _));
    }

    [Fact]
    public void DifferentFamilies_YieldDifferentNames()
    {
        var a = PackageIdentityHelper.ApplyScope(@"Local\DutyAgent.Client.SingleInstance", "PkgA_111");
        var b = PackageIdentityHelper.ApplyScope(@"Local\DutyAgent.Client.SingleInstance", "PkgB_222");

        Assert.NotEqual(a, b);
    }
}
