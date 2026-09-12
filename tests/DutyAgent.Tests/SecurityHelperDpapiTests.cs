using DutyAgent.Services;
using Xunit;

namespace DutyAgent.Tests;

/// <summary>
/// SecurityHelper DPAPI（REPAIR_PLAN §0 D4 / §2 C2）：
/// ProtectForCurrentUser → "dpapi:v1:&lt;base64>"，仅本机本用户可解；
/// UnprotectCurrentUser 对格式不符/损坏密文返回 null 不抛（对齐 Python
/// dpapi_compat.unprotect 的容错语义）。
/// </summary>
public class SecurityHelperDpapiTests
{
    [Fact]
    public void Protect_then_Unprotect_roundtrips()
    {
        foreach (var plain in new[] { "sk-abc123", "中文密钥123", "p@$$w0rd!\"\\", new string('x', 512) })
        {
            var cipher = SecurityHelper.ProtectForCurrentUser(plain);
            Assert.StartsWith("dpapi:v1:", cipher, StringComparison.Ordinal);
            Assert.DoesNotContain(plain, cipher, StringComparison.Ordinal);
            Assert.Equal(plain, SecurityHelper.UnprotectCurrentUser(cipher));
        }
    }

    [Fact]
    public void Ciphertext_is_not_deterministic()
    {
        // DPAPI 每次加密产出不同密文（内部随机密钥/熵），防止用密文比对等价性。
        var a = SecurityHelper.ProtectForCurrentUser("same-input");
        var b = SecurityHelper.ProtectForCurrentUser("same-input");
        Assert.NotEqual(a, b);
        Assert.Equal("same-input", SecurityHelper.UnprotectCurrentUser(a));
        Assert.Equal("same-input", SecurityHelper.UnprotectCurrentUser(b));
    }

    [Fact]
    public void IsDpapiProtected_discriminates_formats()
    {
        Assert.True(SecurityHelper.IsDpapiProtected(
            SecurityHelper.ProtectForCurrentUser("plain")));
        Assert.False(SecurityHelper.IsDpapiProtected("plain-key"));
        Assert.False(SecurityHelper.IsDpapiProtected(""));
        Assert.False(SecurityHelper.IsDpapiProtected(null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("legacy-plain-key")]
    [InlineData("dpapi:v1:%%%not-base64%%%")]
    [InlineData("dpapi:v1:AAAA")]
    public void UnprotectCurrentUser_degrades_to_null_without_throwing(string? cipherText)
    {
        Assert.Null(SecurityHelper.UnprotectCurrentUser(cipherText));
    }

    [Fact]
    public void ProtectForCurrentUser_rejects_empty_input()
    {
        Assert.ThrowsAny<ArgumentException>(() => SecurityHelper.ProtectForCurrentUser(""));
        Assert.ThrowsAny<ArgumentException>(() => SecurityHelper.ProtectForCurrentUser(null!));
    }
}
