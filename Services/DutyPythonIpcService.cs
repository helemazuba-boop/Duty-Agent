using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DutyAgent.Models;

namespace DutyAgent.Services;

public interface IPythonIpcService
{
    Task<CoreRunResult> RunScheduleAsync(object requestPayload, Action<CoreRunProgress>? progressCallback, CancellationToken cancellationToken = default);
}

public class DutyPythonIpcService : IPythonIpcService, IDisposable
{
    private readonly IConfigManager _configManager;
    private readonly string _basePath;
    private readonly string _dataDir;
    private Process? _pythonProcess;
    private int _serverPort = 0;
    private readonly SemaphoreSlim _processLock = new(1, 1);
    private readonly HttpClient _httpClient;
    private bool _disposed;

    public DutyPythonIpcService(IConfigManager configManager)
    {
        _configManager = configManager;
        _basePath = Path.GetDirectoryName(typeof(DutyPythonIpcService).Assembly.Location) ?? AppContext.BaseDirectory;
        _dataDir = Path.Combine(_basePath, "Assets_Duty", "data");
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        
        AppDomain.CurrentDomain.ProcessExit += (_, _) => ShutdownPythonServer();
    }

    private async Task EnsureServerRunningAsync(CancellationToken cancellationToken)
    {
        await _processLock.WaitAsync(cancellationToken);
        try
        {
            if (_pythonProcess != null && !_pythonProcess.HasExited && _serverPort > 0)
            {
                return;
            }

            ShutdownPythonServer(); // Clean up any zombie or partially started process

            var pythonPath = DutyScheduleOrchestrator.ValidatePythonPath(_configManager.Config.PythonPath, _basePath, _basePath);
            var scriptPath = Path.Combine(_basePath, "Assets_Duty", "core.py");
            
            if (!File.Exists(scriptPath))
            {
                throw new FileNotFoundException($"Core script not found: {scriptPath}");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = pythonPath,
                Arguments = $"\"{scriptPath}\" --data-dir \"{_dataDir}\" --server --port 0",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            _pythonProcess = new Process { StartInfo = startInfo };
            
            var tcs = new TaskCompletionSource<int>();
            _pythonProcess.OutputDataReceived += (sender, args) =>
            {
                if (args.Data != null)
                {
                    if (args.Data.StartsWith("__DUTY_SERVER_PORT__:"))
                    {
                        if (int.TryParse(args.Data.Substring("__DUTY_SERVER_PORT__:".Length), out var port))
                        {
                            tcs.TrySetResult(port);
                        }
                    }
                    else
                    {
                        Debug.WriteLine($"[Python] {args.Data}");
                    }
                }
            };
            
            _pythonProcess.ErrorDataReceived += (sender, args) =>
            {
                if (args.Data != null)
                {
                    Debug.WriteLine($"[Python ERR] {args.Data}");
                }
            };

            if (!_pythonProcess.Start())
            {
                throw new Exception("Failed to start Python IPC Server process.");
            }

            _pythonProcess.BeginOutputReadLine();
            _pythonProcess.BeginErrorReadLine();
            PythonProcessTracker.Register(_pythonProcess);

            // Wait for port mapping with timeout
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            
            try
            {
                _serverPort = await tcs.Task.WaitAsync(cts.Token);
                Debug.WriteLine($"Python server started on port {_serverPort}");
            }
            catch (OperationCanceledException)
            {
                ShutdownPythonServer();
                throw new Exception("Python server process failed to report its port within 10 seconds.");
            }
        }
        finally
        {
            _processLock.Release();
        }
    }

    public async Task<CoreRunResult> RunScheduleAsync(object requestPayload, Action<CoreRunProgress>? progressCallback, CancellationToken cancellationToken = default)
    {
        await EnsureServerRunningAsync(cancellationToken);

        var url = $"http://127.0.0.1:{_serverPort}/schedule";
        var jsonPayload = JsonSerializer.Serialize(requestPayload);
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
        
        try
        {
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);
            
            CoreRunResult? finalResult = null;

            while (!reader.EndOfStream)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(line)) continue;

                if (line.StartsWith("data: "))
                {
                    var dataContent = line.Substring("data: ".Length);
                    try
                    {
                        var evt = JsonSerializer.Deserialize<JsonElement>(dataContent);
                        if (evt.TryGetProperty("phase", out var phaseProp) && evt.TryGetProperty("message", out var msgProp))
                        {
                            var phase = phaseProp.GetString() ?? "";
                            var message = msgProp.GetString() ?? "";
                            var chunk = evt.TryGetProperty("stream_chunk", out var chunkProp) ? chunkProp.GetString() : null;
                            progressCallback?.Invoke(new CoreRunProgress(phase, message, chunk));
                        }
                    }
                    catch
                    {
                        // Ignore malformed progress json
                    }
                }
                else if (line.StartsWith("event: complete"))
                {
                    var dataLine = await reader.ReadLineAsync();
                    if (dataLine != null && dataLine.StartsWith("data: "))
                    {
                        var dataContent = dataLine.Substring("data: ".Length);
                        var evt = JsonSerializer.Deserialize<JsonElement>(dataContent);
                        
                        var status = evt.TryGetProperty("status", out var sProp) ? sProp.GetString() : "error";
                        var msg = evt.TryGetProperty("message", out var mProp) ? mProp.GetString() : "";
                        
                        if (status == "success")
                        {
                            var aiResponse = evt.TryGetProperty("ai_response", out var aiProp) ? aiProp.GetString() : null;
                            finalResult = CoreRunResult.Ok("Success", aiResponse);
                        }
                        else
                        {
                            finalResult = CoreRunResult.Fail(msg ?? "Unknown error");
                        }
                    }
                }
            }

            return finalResult ?? CoreRunResult.Fail("Connection ended without complete event.");
        }
        catch (HttpRequestException ex)
        {
            Debug.WriteLine($"HTTP Request failed: {ex.Message}");
            
            // If connection failed entirely, the python process might have crashed
            if (_pythonProcess == null || _pythonProcess.HasExited)
            {
                ShutdownPythonServer();
                throw new Exception("The native Python engine crashed unexpectedly.", ex);
            }
            
            return CoreRunResult.Fail($"Network communication error: {ex.Message}");
        }
    }

    private void ShutdownPythonServer()
    {
        if (_pythonProcess == null || _pythonProcess.HasExited) return;

        try
        {
            if (_serverPort > 0)
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                client.PostAsync($"http://127.0.0.1:{_serverPort}/shutdown", null).Wait(TimeSpan.FromSeconds(2));
            }
        }
        catch { } // Ignore shutdown request errors
        
        try
        {
            if (!_pythonProcess.HasExited)
            {
                _pythonProcess.Kill(true);
            }
        }
        catch { }
        finally
        {
            _pythonProcess.Dispose();
            _pythonProcess = null;
            _serverPort = 0;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        ShutdownPythonServer();
        _httpClient.Dispose();
        _processLock.Dispose();
    }
}
