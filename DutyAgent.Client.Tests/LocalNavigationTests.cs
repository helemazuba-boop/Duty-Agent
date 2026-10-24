using DutyAgent.Client;
using Xunit;

namespace DutyAgent.Client.Tests;

/// <summary>
/// WebView 导航白名单回归。
///
/// 界面是同源网页，宿主消息通道（install-bridge / open-external / window-command）
/// 等价于"页面能调起本机能力"。一旦 WebView 被导航到外部站点，那条通道就落到别人
/// 手里，所以导航必须只留在本地后端。这里钉住判定规则本身。
/// </summary>
public sealed class LocalNavigationTests
{
    [Theory]
    [InlineData("http://127.0.0.1:51234/index.html", true)]
    [InlineData("http://127.0.0.1:51234/#/settings", true)]
    [InlineData("http://localhost:8080/", true)]
    [InlineData("about:blank", true)]
    [InlineData("https://evil.example.com/", false)]
    [InlineData("http://192.168.1.10:51234/", false)]
    [InlineData("https://github.com/helemazuba-boop/Duty-Agent", false)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyLoopbackHttpAndAboutAreAllowed(string? uri, bool expected)
    {
        Assert.Equal(expected, MainForm.IsLocalAppNavigation(uri));
    }
}
