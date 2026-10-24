using Microsoft.Web.WebView2.Core;

namespace DutyAgent.Client;

/// <summary>
/// WebView2 Runtime 探测。
///
/// 主界面由 WebView2 承载，而 Runtime **不随包分发**（Fixed Version 会让包体再涨
/// 250MB+），只能依赖系统的 Evergreen 运行时：Win11 预装，Win10 绝大多数随 Edge
/// 更新获得，但仍有一部分机器缺失。缺失时 WinForms 控件初始化会抛一个没有上下文
/// 的 COM 异常，落到外层就只剩"启动失败"——用户不知道要装什么，商店审核也拿不到
/// 可判定的信息。所以启动时先显式探测一次，把版本打进日志，缺失时给可操作指引。
/// </summary>
internal static class WebViewRuntime
{
    /// <summary>微软官方 Evergreen Bootstrapper 下载地址（WebView2 分发文档里的短链）。</summary>
    internal const string DownloadUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

    /// <summary>探测已安装的 Evergreen 运行时版本；未安装或损坏时返回 false。</summary>
    internal static bool TryGetVersion(out string version)
    {
        try
        {
            version = CoreWebView2Environment.GetAvailableBrowserVersionString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(version);
        }
        catch (Exception)
        {
            // 未安装时该方法抛 WebView2RuntimeNotFoundException；其它异常（注册表
            // 权限、运行时目录损坏）对用户而言结论相同：主界面打不开。
            version = string.Empty;
            return false;
        }
    }

    /// <summary>缺失时给用户看的完整说明（含官方下载地址，可直接复制）。</summary>
    internal static string BuildMissingMessage()
    {
        return "未检测到 Microsoft Edge WebView2 Runtime，主界面无法显示。\n\n"
            + "Duty-Agent 的界面是一个本地网页，需要系统提供 WebView2 运行时。\n"
            + "Windows 11 通常自带；Windows 10 可通过安装以下运行时补齐：\n\n"
            + DownloadUrl
            + "\n\n安装后重新打开 Duty-Agent 即可（无需重装本软件）。\n"
            + "后台服务（自动排班、提醒、ClassIsland 联动）不受影响，仍会继续运行。";
    }

    /// <summary>用默认浏览器打开官方下载页；失败只记日志，不抛。</summary>
    internal static void OpenDownloadPage()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(DownloadUrl)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ClientLog.Warn($"打开 WebView2 下载页失败：{ex.Message}");
        }
    }
}

/// <summary>WebView2 运行时缺失。单独一个类型，是为了让上层能区分"缺运行时"与"其它启动失败"。</summary>
internal sealed class WebViewRuntimeMissingException : InvalidOperationException
{
    internal WebViewRuntimeMissingException()
        : base(WebViewRuntime.BuildMissingMessage())
    {
    }
}
