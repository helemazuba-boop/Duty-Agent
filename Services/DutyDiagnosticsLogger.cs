using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DutyAgent.Services;

internal static class DutyDiagnosticsLogger
{
    private const long MaxLogFileBytes = 5 * 1024 * 1024;
    private const int KeepDays = 14;
    private static readonly object SyncRoot = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private static readonly string SessionId = DateTime.Now.ToString("yyyyMMdd-HHmmss");
    private static readonly string LogDirectory = ResolveLogDirectory();
    private static string _currentLogPath = BuildDefaultLogPath();
    private static bool _initialized;

    public static string CurrentLogPath
    {
        get
        {
            lock (SyncRoot)
            {
                EnsureInitialized();
                return _currentLogPath;
            }
        }
    }

    public static void Info(string scope, string message, object? data = null)
    {
        Write("INFO", scope, message, data, null);
    }

    public static void Warn(string scope, string message, object? data = null)
    {
        Write("WARN", scope, message, data, null);
    }

    public static void Error(string scope, string message, Exception? ex = null, object? data = null)
    {
        Write("ERROR", scope, message, data, ex);
    }

    private static void Write(string level, string scope, string message, object? data, Exception? ex)
    {
        try
        {
            lock (SyncRoot)
            {
                EnsureInitialized();
                RotateIfNeeded();

                var payloadText = SerializePayload(data);
                var exText = ex is null ? string.Empty : $" ex=\"{Sanitize(ex.GetType().Name)}:{Sanitize(ex.Message)}\"";
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [sid:{SessionId}] [pid:{Environment.ProcessId}] [tid:{Environment.CurrentManagedThreadId}] [{Sanitize(scope)}] {Sanitize(message)}{payloadText}{exText}";
                File.AppendAllText(_currentLogPath, line + Environment.NewLine, new UTF8Encoding(false));
                Debug.WriteLine(line);
            }
        }
        catch (Exception logEx)
        {
            Debug.WriteLine($"DutyDiagnosticsLogger failed: {logEx}");
        }
    }

    private static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        Directory.CreateDirectory(LogDirectory);
        PruneExpiredLogs();
        _currentLogPath = BuildDefaultLogPath();
        _initialized = true;
    }

    private static void RotateIfNeeded()
    {
        try
        {
            if (!File.Exists(_currentLogPath))
            {
                return;
            }

            var info = new FileInfo(_currentLogPath);
            if (info.Length <= MaxLogFileBytes)
            {
                return;
            }

            _currentLogPath = Path.Combine(LogDirectory, $"duty-webview-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"DutyDiagnosticsLogger rotate failed: {ex}");
        }
    }

    private static void PruneExpiredLogs()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-KeepDays);
            foreach (var file in Directory.EnumerateFiles(LogDirectory, "duty-webview-*.log"))
            {
                var info = new FileInfo(file);
                if (info.LastWriteTime < cutoff)
                {
                    info.Delete();
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"DutyDiagnosticsLogger prune failed: {ex}");
        }
    }

    private static string BuildDefaultLogPath()
    {
        return Path.Combine(LogDirectory, $"duty-webview-{DateTime.Now:yyyyMMdd}.log");
    }

    private static string ResolveLogDirectory()
    {
        try
        {
            var baseDir = Path.GetDirectoryName(typeof(DutyDiagnosticsLogger).Assembly.Location) ?? AppContext.BaseDirectory;
            return Path.Combine(baseDir, "Assets_Duty", "logs");
        }
        catch
        {
            return Path.Combine(AppContext.BaseDirectory, "Assets_Duty", "logs");
        }
    }

    private static string SerializePayload(object? data)
    {
        if (data is null)
        {
            return string.Empty;
        }

        try
        {
            var json = JsonSerializer.Serialize(data, JsonOptions);
            return $" data={json}";
        }
        catch (Exception ex)
        {
            return $" data=\"<serialize-failed:{Sanitize(ex.Message)}>\"";
        }
    }

    private static string Sanitize(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text.Replace('\r', ' ').Replace('\n', ' ').Trim();
    }
}
