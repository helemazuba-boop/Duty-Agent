using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace DutyAgent.Services;

internal static class PythonProcessTracker
{
    private static readonly ConcurrentDictionary<int, byte> RunningProcessIds = new();
    private sealed record TrackedProcessSnapshot(int ProcessId, long StartTimeUtcTicks);

    public static void Register(Process process, string? snapshotPath = null)
    {
        try
        {
            RunningProcessIds.TryAdd(process.Id, 0);
            SaveSnapshot(process, snapshotPath);
        }
        catch
        {
        }
    }

    public static void Unregister(Process process, string? snapshotPath = null)
    {
        try
        {
            RunningProcessIds.TryRemove(process.Id, out _);
            TryDeleteSnapshotIfMatches(process, snapshotPath);
        }
        catch
        {
        }
    }

    public static void CleanupPersistedProcess(string? snapshotPath)
    {
        if (string.IsNullOrWhiteSpace(snapshotPath) || !File.Exists(snapshotPath))
        {
            return;
        }

        try
        {
            var snapshot = ReadSnapshot(snapshotPath);
            if (snapshot == null)
            {
                TryDeleteSnapshot(snapshotPath);
                return;
            }

            using var proc = Process.GetProcessById(snapshot.ProcessId);
            if (proc.HasExited)
            {
                TryDeleteSnapshot(snapshotPath);
                return;
            }

            var startTimeUtcTicks = proc.StartTime.ToUniversalTime().Ticks;
            if (startTimeUtcTicks != snapshot.StartTimeUtcTicks)
            {
                TryDeleteSnapshot(snapshotPath);
                return;
            }

            proc.Kill(entireProcessTree: true);
        }
        catch
        {
        }
        finally
        {
            TryDeleteSnapshot(snapshotPath);
        }
    }

    public static void CleanupTrackedProcesses()
    {
        foreach (var pid in RunningProcessIds.Keys)
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
        }
    }

    private static void SaveSnapshot(Process process, string? snapshotPath)
    {
        if (string.IsNullOrWhiteSpace(snapshotPath))
        {
            return;
        }

        try
        {
            var startTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks;
            var snapshot = new TrackedProcessSnapshot(process.Id, startTimeUtcTicks);
            var directory = Path.GetDirectoryName(snapshotPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(snapshotPath, JsonSerializer.Serialize(snapshot));
        }
        catch
        {
        }
    }

    private static TrackedProcessSnapshot? ReadSnapshot(string snapshotPath)
    {
        try
        {
            var json = File.ReadAllText(snapshotPath);
            return JsonSerializer.Deserialize<TrackedProcessSnapshot>(json);
        }
        catch
        {
            return null;
        }
    }

    private static void TryDeleteSnapshotIfMatches(Process process, string? snapshotPath)
    {
        if (string.IsNullOrWhiteSpace(snapshotPath) || !File.Exists(snapshotPath))
        {
            return;
        }

        try
        {
            var snapshot = ReadSnapshot(snapshotPath);
            if (snapshot == null)
            {
                TryDeleteSnapshot(snapshotPath);
                return;
            }

            if (snapshot.ProcessId == process.Id)
            {
                TryDeleteSnapshot(snapshotPath);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteSnapshot(string snapshotPath)
    {
        try
        {
            if (File.Exists(snapshotPath))
            {
                File.Delete(snapshotPath);
            }
        }
        catch
        {
        }
    }
}
