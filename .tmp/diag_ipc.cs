using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

public class Diag
{
    public static async Task Main()
    {
        var basePath = AppContext.BaseDirectory;
        // In the real plugin, it might be different. Let's try to find Assets_Duty
        var assetsPath = Path.Combine(basePath, "Assets_Duty");
        if (!Directory.Exists(assetsPath)) 
        {
            // Try upward search for dev env
            assetsPath = Path.Combine(Directory.GetCurrentDirectory(), "Assets_Duty");
        }
        var dataDir = Path.Combine(assetsPath, "data");
        var scriptPath = Path.Combine(assetsPath, "core.py");
        var pythonPath = "python"; // Assume in PATH or use validated path

        Console.WriteLine($"Script: {scriptPath}");
        Console.WriteLine($"DataDir: {dataDir}");

        var startInfo = new ProcessStartInfo
        {
            FileName = pythonPath,
            Arguments = $"\"{scriptPath}\" --data-dir \"{dataDir}\" --server --port 5051",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        var process = new Process { StartInfo = startInfo };
        var tcs = new TaskCompletionSource<string>();

        process.OutputDataReceived += (s, e) => {
            if (e.Data != null) {
                Console.WriteLine($"[STDOUT] {e.Data}");
                if (e.Data.StartsWith("__DUTY_SERVER_PORT__:")) tcs.TrySetResult(e.Data);
            }
        };
        process.ErrorDataReceived += (s, e) => {
            if (e.Data != null) Console.WriteLine($"[STDERR] {e.Data}");
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        Console.WriteLine("Waiting for port...");
        var waitTask = tcs.Task;
        var timeoutTask = Task.Delay(5000);

        if (await Task.WhenAny(waitTask, timeoutTask) == waitTask)
        {
            Console.WriteLine($"SUCCESS: {waitTask.Result}");
        }
        else
        {
            Console.WriteLine("FAILURE: Timeout");
        }

        try { process.Kill(); } catch {}
    }
}
