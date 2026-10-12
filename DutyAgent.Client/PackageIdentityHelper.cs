using System;
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
    public static string ScopeName(string bareName)
    {
        var family = PackageFamilyName;
        return string.IsNullOrWhiteSpace(family) ? bareName : $@"{family}\{bareName}";
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
