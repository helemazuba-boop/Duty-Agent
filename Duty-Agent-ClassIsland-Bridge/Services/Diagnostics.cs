using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using DutyAgentBridge.Models;

namespace DutyAgentBridge.Services;

/// <summary>
/// 桥接插件诊断日志服务
/// </summary>
public static class Diagnostics
{
    private static readonly ConcurrentQueue<BridgeLogEntry> _entries = new();
    private static readonly string _logDirectory;
    private static readonly object _writeLock = new();
    private const int MaxInMemoryEntries = 500;

    static Diagnostics()
    {
        // 尝试获取日志目录
        try
        {
            var configFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ClassIsland", "Config", "DutyAgentBridge");
            _logDirectory = Path.Combine(configFolder, "logs");
            Directory.CreateDirectory(_logDirectory);
        }
        catch
        {
            _logDirectory = Path.GetTempPath();
        }
    }

    public static void Log(string scope, string message, string level = "INFO", object? data = null)
    {
        var entry = new BridgeLogEntry
        {
            Timestamp = DateTimeOffset.Now,
            Level = level,
            Scope = scope,
            Message = message,
            Data = data
        };

        _entries.Enqueue(entry);
        while (_entries.Count > MaxInMemoryEntries && _entries.TryDequeue(out _)) { }

        WriteToFile(entry);
        Debug.WriteLine($"[Bridge/{level}] {scope}: {message}");
    }

    public static void Info(string scope, string message, object? data = null)
        => Log(scope, message, "INFO", data);

    public static void Warn(string scope, string message, object? data = null)
        => Log(scope, message, "WARN", data);

    public static void Error(string scope, string message, Exception? ex = null, object? data = null)
    {
        var msg = ex != null ? $"{message} ({ex.GetType().Name}: {ex.Message})" : message;
        Log(scope, msg, "ERROR", data);
    }

    private static void WriteToFile(BridgeLogEntry entry)
    {
        try
        {
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            var logPath = Path.Combine(_logDirectory, $"bridge-{today}.log");
            var line = $"[{entry.Timestamp:HH:mm:ss.fff}] [{entry.Level}] [{entry.Scope}] {entry.Message}";
            if (entry.Data != null)
            {
                try { line += $" | {System.Text.Json.JsonSerializer.Serialize(entry.Data)}"; } catch { }
            }
            line += Environment.NewLine;

            lock (_writeLock)
            {
                File.AppendAllText(logPath, line);
            }
        }
        catch
        {
            // 静默忽略日志写入失败
        }
    }

    public static IReadOnlyList<BridgeLogEntry> GetRecentEntries(int count = 100)
    {
        return _entries.TakeLast(count).ToList().AsReadOnly();
    }
}
