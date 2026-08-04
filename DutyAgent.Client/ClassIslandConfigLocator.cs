using System.Diagnostics;
using System.Text.Json;

namespace DutyAgent.Client;

internal sealed record ClassIslandConfigCandidate(
    string ConfigFolder,
    string Source,
    bool Exists);

internal sealed record ClassIslandConfigDiscovery(
    string ConfigFolder,
    string Source,
    IReadOnlyList<ClassIslandConfigCandidate> Candidates);

internal static class ClassIslandConfigLocator
{
    private const string EnvVarName = "CLASSISLAND_CONFIG_PATH";

    public static ClassIslandConfigDiscovery Discover()
    {
        var candidates = new List<(string Path, string Source)>();

        AddCandidate(candidates, Environment.GetEnvironmentVariable(EnvVarName), EnvVarName);

        foreach (var processPath in EnumerateRunningClassIslandPaths())
        {
            AddCandidate(candidates, processPath, "running-process");
        }

        foreach (var configuredPath in ReadConfiguredClassIslandPaths())
        {
            AddCandidate(candidates, configuredPath, "client-settings");
        }

        foreach (var localPath in EnumerateConfigCandidatesFromPath(AppContext.BaseDirectory))
        {
            AddCandidate(candidates, localPath, "client-location");
        }

        AddCandidate(candidates, GetAppDataConfigFolder(), "appdata");

        var normalized = candidates
            .Select(candidate => new ClassIslandConfigCandidate(
                NormalizeConfigFolder(candidate.Path),
                candidate.Source,
                Directory.Exists(NormalizeConfigFolder(candidate.Path))))
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.ConfigFolder))
            .DistinctBy(candidate => candidate.ConfigFolder, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalized.Count == 0)
        {
            var fallback = GetAppDataConfigFolder();
            normalized.Add(new ClassIslandConfigCandidate(fallback, "appdata", Directory.Exists(fallback)));
        }

        var selected =
            normalized.FirstOrDefault(candidate => candidate.Source == EnvVarName) ??
            normalized.FirstOrDefault(candidate => candidate.Source == "running-process") ??
            normalized.FirstOrDefault(candidate => candidate.Source == "client-settings") ??
            normalized.FirstOrDefault(candidate => candidate.Source == "client-location" && candidate.Exists) ??
            normalized.FirstOrDefault(candidate => candidate.Source == "appdata") ??
            normalized.FirstOrDefault(candidate => candidate.Exists) ??
            normalized[0];
        return new ClassIslandConfigDiscovery(selected.ConfigFolder, selected.Source, normalized);
    }

    private static void AddCandidate(List<(string Path, string Source)> candidates, string? path, string source)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            candidates.Add((path, source));
        }
    }

    private static IEnumerable<string> EnumerateRunningClassIslandPaths()
    {
        foreach (var process in Process.GetProcessesByName("ClassIsland"))
        {
            string? executablePath = null;
            try
            {
                executablePath = process.MainModule?.FileName;
            }
            catch
            {
            }

            var directory = Path.GetDirectoryName(executablePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                yield return directory;
            }
        }
    }

    private static IEnumerable<string> ReadConfiguredClassIslandPaths()
    {
        foreach (var configPath in EnumerateClientConfigFiles())
        {
            string? configuredPath = null;
            try
            {
                if (!File.Exists(configPath))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(File.ReadAllText(configPath));
                var root = document.RootElement;
                if (root.TryGetProperty("classisland_config_path", out var snakeCase) &&
                    snakeCase.ValueKind == JsonValueKind.String)
                {
                    configuredPath = snakeCase.GetString();
                }
                else if (root.TryGetProperty("classIslandConfigPath", out var camelCase) &&
                         camelCase.ValueKind == JsonValueKind.String)
                {
                    configuredPath = camelCase.GetString();
                }
            }
            catch
            {
            }

            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                yield return configuredPath;
            }
        }
    }

    private static IEnumerable<string> EnumerateClientConfigFiles()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "duty-agent-client.json");
        yield return Path.Combine(AppContext.BaseDirectory, "client-settings.json");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DutyAgent",
            "client-settings.json");
    }

    private static IEnumerable<string> EnumerateConfigCandidatesFromPath(string? startPath)
    {
        if (string.IsNullOrWhiteSpace(startPath))
        {
            yield break;
        }

        var current = new DirectoryInfo(startPath);
        while (current is not null)
        {
            yield return Path.Combine(current.FullName, "data", "Config");

            if (Directory.Exists(Path.Combine(current.FullName, "Plugins")) ||
                Directory.Exists(Path.Combine(current.FullName, "Config")) ||
                File.Exists(Path.Combine(current.FullName, "Settings.json")))
            {
                yield return Path.Combine(current.FullName, "Config");
            }

            current = current.Parent;
        }
    }

    private static string NormalizeConfigFolder(string rawPath)
    {
        var expanded = Environment.ExpandEnvironmentVariables(rawPath.Trim().Trim('"'));
        var fullPath = Path.GetFullPath(expanded);
        if (File.Exists(fullPath))
        {
            fullPath = Path.GetDirectoryName(fullPath) ?? fullPath;
        }

        var directory = new DirectoryInfo(fullPath);
        if (directory.Name.Equals("DutyAgentBridge", StringComparison.OrdinalIgnoreCase) &&
            directory.Parent is not null)
        {
            return directory.Parent.FullName;
        }

        if (directory.Name.Equals("Config", StringComparison.OrdinalIgnoreCase))
        {
            return directory.FullName;
        }

        var dataConfig = Path.Combine(fullPath, "data", "Config");
        if (Directory.Exists(dataConfig) ||
            Directory.Exists(Path.Combine(fullPath, "data", "Plugins")) ||
            File.Exists(Path.Combine(fullPath, "ClassIsland.exe")))
        {
            return Path.GetFullPath(dataConfig);
        }

        var directConfig = Path.Combine(fullPath, "Config");
        if (Directory.Exists(directConfig) ||
            Directory.Exists(Path.Combine(fullPath, "Plugins")) ||
            File.Exists(Path.Combine(fullPath, "Settings.json")))
        {
            return Path.GetFullPath(directConfig);
        }

        return fullPath;
    }

    private static string GetAppDataConfigFolder()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClassIsland",
            "Config");
    }
}
