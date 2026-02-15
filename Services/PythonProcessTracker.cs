using System.Collections.Concurrent;
using System.Diagnostics;

namespace DutyAgent.Services;

internal static class PythonProcessTracker
{
    private static readonly ConcurrentDictionary<int, byte> RunningProcessIds = new();

    public static void Register(Process process)
    {
        try
        {
            RunningProcessIds.TryAdd(process.Id, 0);
        }
        catch
        {
        }
    }

    public static void Unregister(Process process)
    {
        try
        {
            RunningProcessIds.TryRemove(process.Id, out _);
        }
        catch
        {
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
}
