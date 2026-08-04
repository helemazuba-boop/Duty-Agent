using System.Text;

namespace DutyAgent.Client;

internal sealed record BridgeInstallResult(bool Success, string Message, string? TargetDirectory);

/// <summary>
/// One-click installer that copies the bundled ClassIsland bridge plugin
/// (shipped alongside the client under <c>bridge\</c>) into the discovered
/// ClassIsland plugins directory, so users never have to reason about plugin
/// folders by hand.
/// </summary>
internal static class BridgeInstaller
{
    private const string PluginFolderName = "duty-agent-bridge";

    public static string GetBundledBridgeDirectory()
    {
        return Path.Combine(AppContext.BaseDirectory, "bridge");
    }

    public static BridgeInstallResult Install()
    {
        var sourceDir = GetBundledBridgeDirectory();
        if (!Directory.Exists(sourceDir) ||
            !File.Exists(Path.Combine(sourceDir, "DutyAgentBridge.dll")))
        {
            return new BridgeInstallResult(
                false,
                $"未找到随包的桥接插件目录：{sourceDir}\n请使用完整发布包（其中包含 bridge\\ 目录）。",
                null);
        }

        string pluginsDir;
        try
        {
            pluginsDir = ResolvePluginsDirectory();
        }
        catch (Exception ex)
        {
            return new BridgeInstallResult(false, $"无法定位 ClassIsland 插件目录：{ex.Message}", null);
        }

        var targetDir = Path.Combine(pluginsDir, PluginFolderName);
        try
        {
            Directory.CreateDirectory(targetDir);
            CopyDirectory(sourceDir, targetDir);
        }
        catch (Exception ex)
        {
            return new BridgeInstallResult(
                false,
                $"复制插件文件失败：{ex.Message}\n目标目录：{targetDir}",
                targetDir);
        }

        return new BridgeInstallResult(
            true,
            $"桥接插件已安装到：\n{targetDir}\n\n请重启 ClassIsland 以加载插件。",
            targetDir);
    }

    private static string ResolvePluginsDirectory()
    {
        var discovery = ClassIslandConfigLocator.Discover();
        var configFolder = discovery.ConfigFolder;
        if (string.IsNullOrWhiteSpace(configFolder))
        {
            throw new InvalidOperationException("未发现 ClassIsland 配置目录（请先安装并启动 ClassIsland）。");
        }

        // ConfigFolder is typically "<...>/data/Config"; plugins live next to it
        // as "<...>/data/Plugins". Fall back to "<configFolder>/../Plugins".
        var parent = Path.GetDirectoryName(configFolder.TrimEnd(Path.DirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(parent))
        {
            throw new InvalidOperationException($"无法从配置目录推导插件目录：{configFolder}");
        }

        return Path.Combine(parent, "Plugins");
    }

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var dest = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, dest, overwrite: true);
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            CopyDirectory(dir, Path.Combine(targetDir, Path.GetFileName(dir)));
        }
    }

    public static string DescribeDiscovery()
    {
        try
        {
            var discovery = ClassIslandConfigLocator.Discover();
            var sb = new StringBuilder();
            sb.AppendLine($"选定配置目录：{discovery.ConfigFolder}（来源：{discovery.Source}）");
            sb.AppendLine("候选：");
            foreach (var candidate in discovery.Candidates)
            {
                sb.AppendLine($"  [{(candidate.Exists ? "存在" : "缺失")}] {candidate.Source}: {candidate.ConfigFolder}");
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"发现失败：{ex.Message}";
        }
    }
}
