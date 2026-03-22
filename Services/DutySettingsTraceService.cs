using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;

namespace DutyAgent.Services;

public sealed class DutySettingsTraceService
{
    private const int KeepDays = 14;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly DutyPluginPaths _pluginPaths;
    private readonly object _gate = new();

    public DutySettingsTraceService(DutyPluginPaths pluginPaths)
    {
        _pluginPaths = pluginPaths;
        Directory.CreateDirectory(_pluginPaths.LogsDirectory);
        Directory.CreateDirectory(GetDiagnosticsDirectory());
        PruneExpiredTraceFiles();
    }

    public string CurrentTracePath => GetTracePath(DateTimeOffset.Now);

    public void Info(string eventType, object? data = null)
    {
        Append("INFO", eventType, data);
    }

    public void Warn(string eventType, object? data = null)
    {
        Append("WARN", eventType, data);
    }

    public void Error(string eventType, object? data = null)
    {
        Append("ERROR", eventType, data);
    }

    public void Invariant(string code, string message, object? data = null)
    {
        var payload = new
        {
            code,
            message,
            data
        };
        Append("WARN", "settings_invariant", payload);
        DutyDiagnosticsLogger.Warn("SettingsTrace", message, payload);
    }

    public object CaptureFileSnapshot(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new
                {
                    path,
                    exists = false
                };
            }

            var info = new FileInfo(path);
            using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            return new
            {
                path,
                exists = true,
                size = info.Length,
                last_write_time_utc = info.LastWriteTimeUtc,
                sha256 = hash
            };
        }
        catch (Exception ex)
        {
            return new
            {
                path,
                exists = false,
                error = ex.Message
            };
        }
    }

    public string ExportDiagnosticsBundle(object? uiSnapshot = null)
    {
        var exportDirectory = GetDiagnosticsDirectory();
        Directory.CreateDirectory(exportDirectory);

        var timestamp = DateTimeOffset.Now;
        var bundleName = $"settings-diagnostics-{timestamp:yyyyMMdd-HHmmss}";
        var tempDirectory = Path.Combine(exportDirectory, bundleName);
        var zipPath = Path.Combine(exportDirectory, bundleName + ".zip");

        if (Directory.Exists(tempDirectory))
        {
            Directory.Delete(tempDirectory, recursive: true);
        }

        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        Directory.CreateDirectory(tempDirectory);
        Directory.CreateDirectory(Path.Combine(tempDirectory, "logs"));
        Directory.CreateDirectory(Path.Combine(tempDirectory, "data"));

        try
        {
            CopyJsonWithRedaction(
                _pluginPaths.SettingsPath,
                Path.Combine(tempDirectory, "data", "settings.json"),
                ["host", "static_access_token_encrypted"],
                ["host", "static_access_token_verifier"]);
            CopyIfExists(_pluginPaths.HostStatePath, Path.Combine(tempDirectory, "data", "host-state.json"));
            CopyJsonWithRedaction(
                _pluginPaths.HostConfigPath,
                Path.Combine(tempDirectory, "data", "host-config.json"),
                ["static_access_token_verifier"]);
            CopyIfExists(_pluginPaths.ConfigPath, Path.Combine(tempDirectory, "data", "config.json"));
            CopyIfExists(_pluginPaths.StatePath, Path.Combine(tempDirectory, "data", "state.json"));
            CopyIfExists(_pluginPaths.RosterPath, Path.Combine(tempDirectory, "data", "roster.csv"));

            foreach (var logFile in EnumerateDiagnosticFiles())
            {
                var destination = Path.Combine(tempDirectory, "logs", Path.GetFileName(logFile));
                CopyIfExists(logFile, destination);
            }

            if (uiSnapshot != null)
            {
                WriteJson(Path.Combine(tempDirectory, "ui-snapshot.json"), uiSnapshot);
            }

            WriteJson(Path.Combine(tempDirectory, "manifest.json"), new
            {
                created_at_utc = DateTimeOffset.UtcNow,
                trace_path = CurrentTracePath,
                current_log_path = DutyDiagnosticsLogger.CurrentLogPath,
                runtime_data_directory = _pluginPaths.DataDirectory,
                runtime_logs_directory = _pluginPaths.LogsDirectory
            });

            ZipFile.CreateFromDirectory(tempDirectory, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
            return zipPath;
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private void Append(string level, string eventType, object? data)
    {
        try
        {
            var record = new
            {
                at_utc = DateTimeOffset.UtcNow,
                level,
                pid = Environment.ProcessId,
                tid = Environment.CurrentManagedThreadId,
                event_type = eventType,
                data
            };
            var line = JsonSerializer.Serialize(record, JsonOptions);
            lock (_gate)
            {
                File.AppendAllText(CurrentTracePath, line + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        catch (Exception ex)
        {
            DutyDiagnosticsLogger.Warn("SettingsTrace", "Failed to append settings trace entry.",
                new { eventType, error = ex.Message });
        }
    }

    private IEnumerable<string> EnumerateDiagnosticFiles()
    {
        var cutoff = DateTime.Now.AddDays(-2);
        return Directory.EnumerateFiles(_pluginPaths.LogsDirectory)
            .Where(path =>
            {
                var name = Path.GetFileName(path);
                return name.StartsWith("duty-agent-", StringComparison.OrdinalIgnoreCase) ||
                       name.StartsWith("duty-backend-", StringComparison.OrdinalIgnoreCase) ||
                       name.StartsWith("settings-trace-", StringComparison.OrdinalIgnoreCase);
            })
            .Where(path =>
            {
                try
                {
                    return File.GetLastWriteTime(path) >= cutoff;
                }
                catch
                {
                    return false;
                }
            })
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Take(12)
            .ToList();
    }

    private void PruneExpiredTraceFiles()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-KeepDays);
            foreach (var path in Directory.EnumerateFiles(_pluginPaths.LogsDirectory, "settings-trace-*.jsonl"))
            {
                var info = new FileInfo(path);
                if (info.LastWriteTime < cutoff)
                {
                    info.Delete();
                }
            }
        }
        catch (Exception ex)
        {
            DutyDiagnosticsLogger.Warn("SettingsTrace", "Failed to prune old settings trace files.",
                new { error = ex.Message });
        }
    }

    private string GetTracePath(DateTimeOffset timestamp)
    {
        return Path.Combine(_pluginPaths.LogsDirectory, $"settings-trace-{timestamp:yyyyMMdd}.jsonl");
    }

    private string GetDiagnosticsDirectory()
    {
        return Path.Combine(_pluginPaths.LogsDirectory, "diagnostics");
    }

    private static void CopyIfExists(string sourcePath, string destinationPath)
    {
        if (!File.Exists(sourcePath))
        {
            return;
        }

        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.Copy(sourcePath, destinationPath, overwrite: true);
    }

    private static void CopyJsonWithRedaction(string sourcePath, string destinationPath, params string[][] propertyPaths)
    {
        if (!File.Exists(sourcePath))
        {
            return;
        }

        try
        {
            var node = JsonNode.Parse(File.ReadAllText(sourcePath));
            if (node == null)
            {
                CopyIfExists(sourcePath, destinationPath);
                return;
            }

            foreach (var propertyPath in propertyPaths)
            {
                RedactJsonValue(node, propertyPath);
            }

            WriteJson(destinationPath, node);
        }
        catch
        {
            CopyIfExists(sourcePath, destinationPath);
        }
    }

    private static void RedactJsonValue(JsonNode? node, IReadOnlyList<string> propertyPath)
    {
        if (node == null || propertyPath.Count == 0)
        {
            return;
        }

        JsonNode? current = node;
        for (var i = 0; i < propertyPath.Count - 1; i++)
        {
            if (current is not JsonObject currentObject ||
                !currentObject.TryGetPropertyValue(propertyPath[i], out current))
            {
                return;
            }
        }

        if (current is JsonObject targetObject &&
            targetObject.ContainsKey(propertyPath[^1]))
        {
            targetObject[propertyPath[^1]] = "<redacted>";
        }
    }

    private static void WriteJson<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, value, JsonOptions);
        stream.Flush(flushToDisk: true);
    }
}
