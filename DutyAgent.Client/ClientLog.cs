using System.Text;

namespace DutyAgent.Client;

/// <summary>
/// 极简客户端文件日志：追加写入 <c>&lt;data&gt;\logs\client-yyyyMMdd.log</c>。
/// 任何写日志失败都必须静默吞掉——日志助手绝不能反过来影响主流程。
/// </summary>
internal static class ClientLog
{
    private static readonly object WriteLock = new();

    /// <summary>数据目录（与后端 --data-dir 一致），作为路径唯一来源供 BackendProcessManager 复用。</summary>
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DutyAgent",
        "data");

    public static void Info(string message) => Append("INFO", message);

    public static void Warn(string message) => Append("WARN", message);

    public static void Error(string message) => Append("ERROR", message);

    public static void Error(string message, Exception exception) =>
        Append("ERROR", $"{message} {exception.GetType().Name}: {exception.Message}");

    private static void Append(string level, string message)
    {
        try
        {
            var logDirectory = Path.Combine(DataDirectory, "logs");
            Directory.CreateDirectory(logDirectory);
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
            lock (WriteLock)
            {
                File.AppendAllText(
                    Path.Combine(logDirectory, $"client-{DateTime.Now:yyyyMMdd}.log"),
                    line,
                    new UTF8Encoding(false));
            }
        }
        catch
        {
            // 日志写失败静默：不影响主流程。
        }
    }
}
