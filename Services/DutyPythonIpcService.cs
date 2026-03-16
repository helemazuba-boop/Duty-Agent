using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using DutyAgent.Models;

namespace DutyAgent.Services;

public interface IPythonIpcService: IDisposable
{
    Task<CoreRunResult> RunScheduleAsync(object requestPayload, Action<CoreRunProgress>? progressCallback, CancellationToken cancellationToken = default);
    Task<DutyBackendConfig> GetBackendConfigAsync(string requestSource = "host_settings", string? traceId = null, CancellationToken cancellationToken = default);
    Task<DutyBackendConfig> UpdateBackendConfigAsync(DutyBackendConfigPatch patch, string requestSource = "host_settings", string? traceId = null, CancellationToken cancellationToken = default);
    Task<DutyBackendSnapshot> GetBackendSnapshotAsync(string requestSource = "host_settings", string? traceId = null, CancellationToken cancellationToken = default);
    Task EnsureReadyAsync(CancellationToken cancellationToken = default);
    Task RestartEngineAsync();
    Task StopAsync();
    bool IsReady { get; }
    EngineState State { get; }
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
    private readonly DutyPluginPaths _pluginPaths;
    private readonly string _processSnapshotPath;
    private Process? _pythonProcess;
    private IntPtr _pythonJobHandle = IntPtr.Zero;
    private int _serverPort = 0;
    private readonly HttpClient _httpClient;
    private bool _disposed;
    private readonly StringBuilder _errorBuffer = new();
    private const int EngineStartupTimeoutSeconds = 15;
    private const string TraceHeaderName = "X-Duty-Trace-Id";
    private const string RequestSourceHeaderName = "X-Duty-Request-Source";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private TaskCompletionSource<int> _portTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile EngineState _state = EngineState.NotStarted;
    public EngineState State => _state;
    public bool IsReady => _state == EngineState.Ready;
    public string? LastErrorMessage { get; private set; }
    private readonly object _stateLock = new();
    private Task? _initializeTask;

    public DutyPythonIpcService(IConfigManager configManager, DutyPluginPaths pluginPaths)
    {
        _configManager = configManager;
        _pluginPaths = pluginPaths;
        _processSnapshotPath = pluginPaths.ProcessSnapshotPath;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    }

    private Task EnsureStartedAsync()
    {
        lock (_stateLock)
        {
            if (_state == EngineState.Ready)
            {
                return Task.CompletedTask;
            }

            if (_state == EngineState.Initializing && _initializeTask != null)
            {
                return _initializeTask;
            }

            if (_state == EngineState.Faulted)
            {
                return Task.CompletedTask;
            }

            _state = EngineState.Initializing;
            if (_portTcs.Task.IsCompleted)
            {
                _portTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            _initializeTask = InitializeBackgroundAsync();
            return _initializeTask;
        }
    }

    private async Task InitializeBackgroundAsync()
    {

        try
        {
            var pythonPath = DutyScheduleOrchestrator.ValidatePythonPath(
                _configManager.Config.PythonPath,
                _pluginPaths.PluginFolderPath,
                _pluginPaths.AssetsDirectory);
            var scriptPath = _pluginPaths.CoreScriptPath;
            
            if (!File.Exists(scriptPath))
            {
                throw new FileNotFoundException($"Core script not found at {scriptPath}");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = pythonPath,
                Arguments = $"\"{scriptPath}\" --data-dir \"{_pluginPaths.DataDirectory}\" --server --port 0",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8
            };

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _pythonProcess = process;
            var startupPortTcs = _portTcs;

            process.OutputDataReceived += (s, e) =>
            {
                if (string.IsNullOrEmpty(e.Data)) return;
                Debug.WriteLine($"[Python] {e.Data}");
                if (e.Data.StartsWith("__DUTY_SERVER_PORT__:"))
                {
                    if (int.TryParse(e.Data.Split(':')[1], out var port))
                    {
                        lock (_stateLock)
                        {
                            if (!ReferenceEquals(_pythonProcess, process))
                            {
                                return;
                            }

                            _serverPort = port;
                        }

                        startupPortTcs.TrySetResult(port);
                    }
                }
            };

            process.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    Debug.WriteLine($"[Python ERR] {e.Data}");
                    lock (_errorBuffer)
                    {
                        if (_errorBuffer.Length < 10000)
                        {
                            _errorBuffer.AppendLine(e.Data);
                        }
                    }
                }
            };

            process.Exited += (_, _) =>
            {
                var exitCode = -1;
                try { exitCode = process.ExitCode; } catch { }
                PythonProcessTracker.Unregister(process, _processSnapshotPath);

                var shouldFaultActiveEngine = false;
                lock (_stateLock)
                {
                    shouldFaultActiveEngine = ReferenceEquals(_pythonProcess, process) &&
                                              (_state == EngineState.Ready || _state == EngineState.Initializing);
                    if (shouldFaultActiveEngine)
                    {
                        _state = EngineState.Faulted;
                        LastErrorMessage = $"Engine process exited unexpectedly with code {exitCode}";
                    }
                }

                startupPortTcs.TrySetException(new Exception($"Python engine process exited unexpectedly with code {exitCode}"));

                if (shouldFaultActiveEngine)
                {
                    lock (_stateLock)
                    {
                        if (ReferenceEquals(_pythonProcess, process))
                        {
                            _serverPort = 0;
                        }
                    }
                }
            };

            if (!process.Start())
            {
                throw new Exception("Failed to start Python process.");
            }

            EnsureProcessBoundToJob(process);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            PythonProcessTracker.Register(process, _processSnapshotPath);

            // Wait for port mapping with 15s timeout
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(EngineStartupTimeoutSeconds));
            using (cts.Token.Register(() => startupPortTcs.TrySetException(new TimeoutException($"Python engine initialization timed out ({EngineStartupTimeoutSeconds}s)."))))
            {
                await startupPortTcs.Task;
            }

            _state = EngineState.Ready;
            Debug.WriteLine($"Python Engine effectively ready on port {_serverPort}");
            lock (_errorBuffer) _errorBuffer.Clear();
        }
        catch (Exception ex)
        {
            _state = EngineState.Faulted;
            string forensicLog;
            lock (_errorBuffer) forensicLog = _errorBuffer.ToString();
            
            LastErrorMessage = string.IsNullOrWhiteSpace(forensicLog) 
                ? ex.Message 
                : $"{ex.Message}\n--- Python Error ---\n{forensicLog}";
                
            _portTcs.TrySetException(new Exception(LastErrorMessage));
            ShutdownPythonServer();
        }
        finally
        {
            lock (_stateLock)
            {
                _initializeTask = null;
            }
        }
    }

    public async Task EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        if (_state == EngineState.Ready)
        {
            return;
        }
        
        Task<int> waitTask;
        lock (_stateLock)
        {
            if (_state == EngineState.Faulted)
            {
                throw new Exception($"AI Engine failed to start: {LastErrorMessage}");
            }

            waitTask = _portTcs.Task;
        }

        await EnsureStartedAsync();
        
        await waitTask.WaitAsync(cancellationToken);
    }

    public async Task RestartEngineAsync()
    {
        await StopAsync();
        lock (_stateLock)
        {
            _state = EngineState.NotStarted;
            LastErrorMessage = null;
            if (_portTcs.Task.IsCompleted)
            {
                _portTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
        await EnsureReadyAsync();
    }

    public Task StopAsync()
    {
        ShutdownPythonServer();

        lock (_stateLock)
        {
            _state = EngineState.NotStarted;
            LastErrorMessage = null;
            if (_portTcs.Task.IsCompleted)
            {
                _portTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        return Task.CompletedTask;
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
            string? currentEvent = null;
            var dataBuffer = new StringBuilder();

            while (!reader.EndOfStream)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync();
                
                if (string.IsNullOrEmpty(line))
                {
                    // Full message received, dispatch
                    if (currentEvent == "complete")
                    {
                        try
                        {
                            var evt = JsonSerializer.Deserialize<JsonElement>(dataBuffer.ToString());
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
                        catch { }
                    }
                    else if (dataBuffer.Length > 0)
                    {
                        // Progress data
                        try
                        {
                            var evt = JsonSerializer.Deserialize<JsonElement>(dataBuffer.ToString());
                            var phase = evt.GetProperty("phase").GetString() ?? "";
                            var message = evt.GetProperty("message").GetString() ?? "";
                            var chunk = evt.TryGetProperty("stream_chunk", out var cp) ? cp.GetString() : null;
                            progressCallback?.Invoke(new CoreRunProgress(phase, message, chunk));
                        }
                        catch { }
                    }

                    // Reset for next message
                    currentEvent = null;
                    dataBuffer.Clear();
                    continue;
                }

                if (line.StartsWith("event: "))
                {
                    currentEvent = line.Substring(7).Trim();
                }
                else if (line.StartsWith("data: "))
                {
                    dataBuffer.Append(line.Substring(6));
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

    public async Task<DutyBackendConfig> GetBackendConfigAsync(
        string requestSource = "host_settings",
        string? traceId = null,
        CancellationToken cancellationToken = default)
    {
        return await SendJsonAsync<DutyBackendConfig>(HttpMethod.Get, "/api/v1/config", null, requestSource, traceId, cancellationToken);
    }

    public async Task<DutyBackendConfig> UpdateBackendConfigAsync(
        DutyBackendConfigPatch patch,
        string requestSource = "host_settings",
        string? traceId = null,
        CancellationToken cancellationToken = default)
    {
        return await SendJsonAsync<DutyBackendConfig>(HttpMethod.Patch, "/api/v1/config", patch, requestSource, traceId, cancellationToken);
    }

    public async Task<DutyBackendSnapshot> GetBackendSnapshotAsync(
        string requestSource = "host_settings",
        string? traceId = null,
        CancellationToken cancellationToken = default)
    {
        return await SendJsonAsync<DutyBackendSnapshot>(HttpMethod.Get, "/api/v1/snapshot", null, requestSource, traceId, cancellationToken);
    }

    private async Task<T> SendJsonAsync<T>(
        HttpMethod method,
        string relativePath,
        object? payload,
        string requestSource,
        string? traceId,
        CancellationToken cancellationToken)
    {
        var effectiveTraceId = string.IsNullOrWhiteSpace(traceId)
            ? DutyDiagnosticsLogger.CreateTraceId("backend")
            : traceId.Trim();
        var effectiveRequestSource = string.IsNullOrWhiteSpace(requestSource) ? "host_settings" : requestSource.Trim();
        var stopwatch = Stopwatch.StartNew();

        DutyDiagnosticsLogger.Info("BackendConfigHttp", "Sending backend HTTP request.",
            new
            {
                traceId = effectiveTraceId,
                requestSource = effectiveRequestSource,
                method = method.Method,
                relativePath,
                payload = SummarizePayload(payload)
            });

        await EnsureReadyAsync(cancellationToken);

        using var request = new HttpRequestMessage(method, $"http://127.0.0.1:{_serverPort}{relativePath}");
        request.Headers.TryAddWithoutValidation(TraceHeaderName, effectiveTraceId);
        request.Headers.TryAddWithoutValidation(RequestSourceHeaderName, effectiveRequestSource);
        if (payload != null)
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(payload, JsonOptions),
                Encoding.UTF8,
                "application/json");
        }

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
            {
                DutyDiagnosticsLogger.Warn("BackendConfigHttp", "Backend HTTP request failed.",
                    new
                    {
                        traceId = effectiveTraceId,
                        requestSource = effectiveRequestSource,
                        method = method.Method,
                        relativePath,
                        statusCode = (int)response.StatusCode,
                        durationMs = stopwatch.ElapsedMilliseconds,
                        responsePreview = TruncateForLog(responseText, 320)
                    });
                response.EnsureSuccessStatusCode();
            }

            var parsed = JsonSerializer.Deserialize<T>(responseText, JsonOptions);
            if (parsed == null)
            {
                throw new InvalidOperationException($"Failed to parse backend response for {relativePath}.");
            }

            DutyDiagnosticsLogger.Info("BackendConfigHttp", "Backend HTTP request completed.",
                new
                {
                    traceId = effectiveTraceId,
                    requestSource = effectiveRequestSource,
                    method = method.Method,
                    relativePath,
                    statusCode = (int)response.StatusCode,
                    durationMs = stopwatch.ElapsedMilliseconds,
                    responseLength = responseText.Length
                });

            return parsed;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            DutyDiagnosticsLogger.Error("BackendConfigHttp", "Backend HTTP request threw exception.", ex,
                new
                {
                    traceId = effectiveTraceId,
                    requestSource = effectiveRequestSource,
                    method = method.Method,
                    relativePath,
                    durationMs = stopwatch.ElapsedMilliseconds,
                    payload = SummarizePayload(payload)
                });
            throw;
        }
    }

    private static object? SummarizePayload(object? payload)
    {
        return payload switch
        {
            null => null,
            DutyBackendConfigPatch patch => new
            {
                selectedPlanId = patch.SelectedPlanId ?? "<unchanged>",
                planPresetCount = patch.PlanPresets?.Count.ToString() ?? "<unchanged>",
                dutyRule = patch.DutyRule is null ? "<unchanged>" : TruncateForLog(patch.DutyRule, 160)
            },
            _ => new
            {
                type = payload.GetType().Name
            }
        };
    }

    private static string TruncateForLog(string? value, int maxLength)
    {
        var normalized = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    private void ShutdownPythonServer()
    {
        var process = _pythonProcess;
        if (process == null)
        {
            DisposeJobObject();
            return;
        }

        try
        {
            if (_serverPort > 0 && !process.HasExited)
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                var shutdownTask = client.PostAsync($"http://127.0.0.1:{_serverPort}/shutdown", null);
                shutdownTask.Wait(2000); // Wait up to 2s for the request to be sent
            }
        }
        catch { }
        
        try
        {
            if (!process.HasExited && !process.WaitForExit(3000))
            {
                process.Kill(true);
            }
        }
        catch { }
        finally
        {
            PythonProcessTracker.Unregister(process, _processSnapshotPath);
            process.Dispose();
            _pythonProcess = null;
            _serverPort = 0;
            DisposeJobObject();
        }
    }

    private void EnsureProcessBoundToJob(Process process)
    {
        try
        {
            if (_pythonJobHandle == IntPtr.Zero)
            {
                _pythonJobHandle = CreateJobObject(IntPtr.Zero, null);
                if (_pythonJobHandle == IntPtr.Zero)
                {
                    return;
                }

                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                {
                    BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                    {
                        LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                    }
                };

                var length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
                var ptr = Marshal.AllocHGlobal(length);
                try
                {
                    Marshal.StructureToPtr(info, ptr, fDeleteOld: false);
                    if (!SetInformationJobObject(
                            _pythonJobHandle,
                            JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation,
                            ptr,
                            (uint)length))
                    {
                        CloseHandle(_pythonJobHandle);
                        _pythonJobHandle = IntPtr.Zero;
                        return;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(ptr);
                }
            }

            if (_pythonJobHandle != IntPtr.Zero)
            {
                AssignProcessToJobObject(_pythonJobHandle, process.Handle);
            }
        }
        catch
        {
        }
    }

    private void DisposeJobObject()
    {
        if (_pythonJobHandle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            CloseHandle(_pythonJobHandle);
        }
        catch
        {
        }
        finally
        {
            _pythonJobHandle = IntPtr.Zero;
        }
    }

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

    private enum JOBOBJECTINFOCLASS
    {
        JobObjectExtendedLimitInformation = 9
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob,
        JOBOBJECTINFOCLASS jobObjectInfoClass,
        IntPtr lpJobObjectInfo,
        uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopAsync().GetAwaiter().GetResult();
        DisposeJobObject();
        _httpClient.Dispose();
    }
}
