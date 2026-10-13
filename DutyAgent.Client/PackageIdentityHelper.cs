using System;
using System.Text;
using Windows.ApplicationModel;

namespace DutyAgent.Client;

/// <summary>
/// 包身份探测：客户端既能以传统桌面程序（zip 分发）运行，也能以 MSIX/微软商店
/// 包的形式运行。凡是"注册表、开始菜单、快捷方式、单实例命名"这类在两种形态下
/// 语义不同的能力，都先经过这里分流。
/// </summary>
internal static class PackageIdentityHelper
{
    private static readonly Lazy<bool> IsPackagedLazy = new(DetectPackaged);

    private static readonly Lazy<string?> PackageFamilyNameLazy = new(DetectPackageFamilyName);

    /// <summary>当前是否运行在 MSIX 包内。</summary>
    public static bool IsPackaged => IsPackagedLazy.Value;

    /// <summary>包系列名；非包身份时为 null。</summary>
    public static string? PackageFamilyName => PackageFamilyNameLazy.Value;

    /// <summary>
    /// 把裸名加上包前缀，避免商店版与 zip 版在同机并存时把对方
    /// 当成"已有实例"（Mutex / 命名事件 / 日志分区）。
    /// </summary>
    /// <remarks>
    /// 坑：Windows 命名对象的 <c>Local\</c> / <c>Global\</c> 前缀必须位于最前，
    /// 包名前缀只能插在它<b>之后</b>。若直接拼成 <c>&lt;PFN&gt;\Local\xxx</c>，
    /// Win32 会把整个名字当文件路径解析，开机即抛
    /// <see cref="System.IO.DirectoryNotFoundException"/>——商店版（有包身份）
    /// 才会触发，zip 版因为返回裸名而毫无症状，纯靠读代码看不出来。
    /// </remarks>
    public static string ScopeName(string rootedName) => ApplyScope(rootedName, PackageFamilyName);

    /// <summary>
    /// 纯函数形式：作用域由调用方注入，便于在无包身份的构建机上做回归测试。
    /// </summary>
    internal static string ApplyScope(string rootedName, string? family)
    {
        if (string.IsNullOrWhiteSpace(family))
        {
            return rootedName;
        }

        var separator = rootedName.IndexOf('\\');
        return separator <= 0
            ? $"{rootedName}.{family}"
            : $@"{rootedName[..separator]}\{family}.{rootedName[(separator + 1)..]}";
    }

    /// <summary>
    /// 一行可读的运行环境摘要，写进客户端日志。
    /// MSIX 与 zip 分发下 LocalAppData / LocalState 的语义不同，而"桥接进程到底
    /// 看到哪个目录"只能靠日志对账——所以这里必须把身份与两个已知文件夹的
    /// 解析结果原样打出来，而不是等到握手失败后再去猜。
    /// </summary>
    public static string Describe()
    {
        var builder = new StringBuilder();
        builder.Append("包身份=").Append(IsPackaged ? "是(MSIX)" : "否(传统桌面)");
        builder.Append(", PFN=").Append(
            string.IsNullOrWhiteSpace(PackageFamilyName) ? "<none>" : PackageFamilyName);

        builder.Append(", LocalAppData=").Append(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

        builder.Append(", LocalState=");
        try
        {
            builder.Append(Windows.Storage.ApplicationData.Current.LocalFolder.Path);
        }
        catch (Exception ex)
        {
            // 非包身份下 ApplicationData.Current 不可用（或抛访问被拒）。
            builder.Append("<不可用: ").Append(ex.GetType().Name).Append('>');
        }

        return builder.ToString();
    }

    private static bool DetectPackaged()
    {
        try
        {
            return Package.Current.Id is not null;
        }
        catch
        {
            // 非包身份时 Package.Current 抛 InvalidOperationException / 访问被拒。
            return false;
        }
    }

    private static string? DetectPackageFamilyName()
    {
        try
        {
            return Package.Current.Id.FamilyName;
        }
        catch
        {
            return null;
        }
    }
}
