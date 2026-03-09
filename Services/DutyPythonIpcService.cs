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

public interface IPythonIpcService: IDisposable
{
    Task<CoreRunResult> RunScheduleAsync(object requestPayload, Action<CoreRunProgress>? progressCallback, CancellationToken cancellationToken = default);
    Task EnsureReadyAsync(CancellationToken cancellationToken = default);
    bool IsReady { get; }
    string? LastErrorMessage { get; }
}

public enum EngineState
{
    NotStarted,
    Initializing,
    Ready,
    Faulted
}

public class DutyPythonIpcService : IPythonIpcService
{
    private readonly IConfigManager _configManager;
    private readonly string _basePath;
    private readonly string _dataDir;
    private Process? _pythonProcess;
    private int _serverPort = 0;
    private readonly HttpClient _httpClient;
    private bool _disposed;

    private readonly TaskCompletionSource<int> _portTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private EngineState _state = EngineState.NotStarted;
    public bool IsReady => _state == EngineState.Ready;
    public string? LastErrorMessage { get; private set; }

    public DutyPythonIpcService(IConfigManager configManager)
    {
        _configManager = configManager;
        _basePath = Path.GetDirectoryName(typeof(DutyPythonIpcService).Assembly.Location) ?? AppContext.BaseDirectory;
        _dataDir = Path.Combine(_basePath, "Assets_Duty", "data");
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        
        AppDomain.CurrentDomain.ProcessExit += (_, _) => ShutdownPythonServer();
        
        // Background initialization - Fire and forget
        _ = InitializeBackgroundAsync();
    }

    private async Task InitializeBackgroundAsync()
    {
        if (_state != EngineState.NotStarted) return;
        _state = EngineState.Initializing;

        try
        {
            var pythonPath = DutyScheduleOrchestrator.ValidatePythonPath(_configManager.Config.PythonPath, _basePath, _basePath);
            var scriptPath = Path.Combine(_basePath, "Assets_Duty", "core.py");
            
            if (!File.Exists(scriptPath))
            {
                throw new FileNotFoundException($"Core script not found at {scriptPath}");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = pythonPath,
                Arguments = $"\"{scriptPath}\" --data-dir \"{_dataDir}\" --server --port 0",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8
            };

            _pythonProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            
            _pythonProcess.OutputDataReceived += (s, e) =>
            {
                if (string.IsNullOrEmpty(e.Data)) return;
                Debug.WriteLine($"[Python] {e.Data}");
                if (e.Data.StartsWith("__DUTY_SERVER_PORT__:"))
                {
                    if (int.TryParse(e.Data.Split(':')[1], out var port))
                    {
                        _serverPort = port;
                        _portTcs.TrySetResult(port);
                    }
                }
            };

            _pythonProcess.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    Debug.WriteLine($"[Python ERR] {e.Data}");
                }
            };

            _pythonProcess.Exited += (s, e) =>
            {
                _portTcs.TrySetException(new Exception($"Python engine process exited unexpectedly with code {_pythonProcess?.ExitCode ?? -1}"));
            };

            if (!_pythonProcess.Start())
            {
                throw new Exception("Failed to start Python process.");
            }

            _pythonProcess.BeginOutputReadLine();
            _pythonProcess.BeginErrorReadLine();
            PythonProcessTracker.Register(_pythonProcess);

            // Wait for port mapping with 15s timeout
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using (cts.Token.Register(() => _portTcs.TrySetException(new TimeoutException("Python engine initialization timed out (15s)."))))
            {
                await _portTcs.Task;
            }

            _state = EngineState.Ready;
            Debug.WriteLine($"Python Engine effectively ready on port {_serverPort}");
        }
        catch (Exception ex)
        {
            _state = EngineState.Faulted;
            LastErrorMessage = ex.Message;
            _portTcs.TrySetException(ex);
            ShutdownPythonServer();
        }
    }

    public async Task EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        if (_state == EngineState.Ready) return;
        if (_state == EngineState.Faulted) throw new Exception($"AI Engine failed to start: {LastErrorMessage}");
        
        await _portTcs.Task.WaitAsync(cancellationToken);
    }

    public async Task<CoreRunResult> RunScheduleAsync(object requestPayload, Action<CoreRunProgress>? progressCallback, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken);

        var url = $"http://127.0.0.1:{_serverPort}/api/v1/duty/schedule";
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
                    var dataContent = line.Substring(6);
                    try
                    {
                        var evt = JsonSerializer.Deserialize<JsonElement>(dataContent);
                        var phase = evt.GetProperty("phase").GetString() ?? "";
                        var message = evt.GetProperty("message").GetString() ?? "";
                        var chunk = evt.TryGetProperty("stream_chunk", out var cp) ? cp.GetString() : null;
                        progressCallback?.Invoke(new CoreRunProgress(phase, message, chunk));
                    }
                    catch { }
                }
                else if (line.StartsWith("event: complete"))
                {
                    var dataLine = await reader.ReadLineAsync();
                    if (dataLine != null && dataLine.StartsWith("data: "))
                    {
                        var dataContent = dataLine.Substring(6);
                        var evt = JsonSerializer.Deserialize<JsonElement>(dataContent);
                        var status = evt.GetProperty("status").GetString();
                        if (status == "success")
                        {
                            var aiResponse = evt.TryGetProperty("ai_response", out var ap) ? ap.GetString() : null;
                            finalResult = CoreRunResult.Ok("Success", aiResponse);
                        }
                        else
                        {
                            var msg = evt.GetProperty("message").GetString() ?? "Unknown error";
                            finalResult = CoreRunResult.Fail(msg);
                        }
                    }
                }
            }

            return finalResult ?? CoreRunResult.Fail("Engine stream closed prematurely.");
        }
        catch (Exception ex)
        {
            if (_pythonProcess == null || _pythonProcess.HasExited)
            {
                _state = EngineState.Faulted;
                LastErrorMessage = "Engine process lost during request.";
                throw new Exception(LastErrorMessage, ex);
            }
            return CoreRunResult.Fail($"Network error: {ex.Message}");
        }
    }

    private void ShutdownPythonServer()
    {
        if (_pythonProcess == null || _pythonProcess.HasExited) return;
        try
        {
            if (_serverPort > 0)
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
                _ = client.PostAsync($"http://127.0.0.1:{_serverPort}/shutdown", null);
            }
        }
        catch { }
        
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
    }
}
