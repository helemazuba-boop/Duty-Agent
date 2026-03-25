using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
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
    Task<DutyScheduleEntrySaveResponse> SaveScheduleEntryAsync(DutyScheduleEntrySaveRequest request, string requestSource = "host_settings", string? traceId = null, CancellationToken cancellationToken = default);
    Task<DutyBackendSnapshot> GetBackendSnapshotAsync(string requestSource = "host_settings", string? traceId = null, CancellationToken cancellationToken = default);
    Task EnsureReadyAsync(CancellationToken cancellationToken = default);
    Task RestartEngineAsync();
    Task StopAsync();
    DutyAccessTokenRuntimeStatus GetAccessTokenStatus();
    DutyServiceEndpointRuntimeStatus GetServiceEndpointStatus();
    string? GetCurrentAccessToken();
    bool IsReady { get; }
    EngineState State { get; }
    string? LastErrorMessage { get; }
    string ServerBaseUrl { get; }
    string WebAppUrl { get; }
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
    private readonly IDutySettingsRepository _settingsRepository;
    private readonly DutyPluginPaths _pluginPaths;
    private readonly string _processSnapshotPath;
    private Process? _pythonProcess;
    private IntPtr _pythonJobHandle = IntPtr.Zero;
    private int _serverPort = 0;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _controlSocketGate = new(1, 1);
    private bool _disposed;
    private readonly StringBuilder _errorBuffer = new();
    private const int EngineStartupTimeoutSeconds = 15;
    private const string TraceHeaderName = "X-Duty-Trace-Id";
    private const string RequestSourceHeaderName = "X-Duty-Request-Source";
    private const string AuthorizationHeaderName = "Authorization";
    private const string PortBootstrapPrefix = "__DUTY_SERVER_PORT__:";
    private const string TokenModeBootstrapPrefix = "__DUTY_SERVER_TOKEN_MODE__:";
    private const string TokenBootstrapPrefix = "__DUTY_SERVER_TOKEN__:";
    private const int MinServicePort = 1024;
    private const int MaxServicePort = 65535;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private TaskCompletionSource<int> _portTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<string> _tokenModeTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<string> _tokenTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile EngineState _state = EngineState.NotStarted;
    private ClientWebSocket? _controlSocket;
    public EngineState State => _state;
    public bool IsReady => _state == EngineState.Ready;
    public string? LastErrorMessage { get; private set; }
    public string ServerBaseUrl => _serverPort > 0 ? $"http://127.0.0.1:{_serverPort}" : string.Empty;
    public string WebAppUrl => string.IsNullOrWhiteSpace(ServerBaseUrl)
        ? string.Empty
        : string.IsNullOrWhiteSpace(_accessToken)
            ? $"{ServerBaseUrl}/app/"
            : $"{ServerBaseUrl}/app/#access_token={Uri.EscapeDataString(_accessToken)}";
    private readonly object _stateLock = new();
    private Task? _initializeTask;
    private string? _accessToken;
    private string _configuredAccessTokenMode = DutyAccessTokenModes.Dynamic;
    private string _activeAccessTokenMode = DutyAccessTokenModes.Dynamic;
    private string _configuredServerPortMode = DutyServerPortModes.Random;
    private int? _configuredFixedServerPort;
    private bool _staticAccessTokenConfigured;
    private bool _enableMcpConfigured;
    private bool _runtimeMcpEnabled;
    private bool _portConflictFallbackActive;

    public DutyPythonIpcService(IConfigManager configManager, IDutySettingsRepository settingsRepository, DutyPluginPaths pluginPaths)
    {
        _configManager = configManager;
        _settingsRepository = settingsRepository;
        _pluginPaths = pluginPaths;
        _processSnapshotPath = pluginPaths.ProcessSnapshotPath;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        RefreshConfiguredAccessTokenState();
        RefreshConfiguredEndpointState();
    }

    public DutyAccessTokenRuntimeStatus GetAccessTokenStatus()
    {
        lock (_stateLock)
        {
            var host = _settingsRepository.LoadLocalSettings().Host;
            RefreshConfiguredAccessTokenState(host);
            RefreshConfiguredEndpointState(host);
            return new DutyAccessTokenRuntimeStatus
            {
                ConfiguredMode = _configuredAccessTokenMode,
                ActiveMode = _activeAccessTokenMode,
                StaticTokenConfigured = _staticAccessTokenConfigured,
                CanCopyCurrentToken = !string.IsNullOrWhiteSpace(GetCurrentAccessTokenUnsafe()),
                BackendReady = _state == EngineState.Ready
            };
        }
    }

    public DutyServiceEndpointRuntimeStatus GetServiceEndpointStatus()
    {
        lock (_stateLock)
        {
            var host = _settingsRepository.LoadLocalSettings().Host;
            RefreshConfiguredAccessTokenState(host);
            RefreshConfiguredEndpointState(host);

            var actualPort = _serverPort > 0 ? _serverPort : (int?)null;
            var serverBaseUrl = actualPort.HasValue ? $"http://127.0.0.1:{actualPort.Value}" : string.Empty;
            var enableMcpActive = _state == EngineState.Ready && _runtimeMcpEnabled && actualPort.HasValue;
            var mcpUrl = enableMcpActive ? $"{serverBaseUrl}/mcp/" : string.Empty;

            return new DutyServiceEndpointRuntimeStatus
            {
                ConfiguredPortMode = _configuredServerPortMode,
                ConfiguredFixedPort = _configuredFixedServerPort,
                ActualPort = actualPort,
                ServerBaseUrl = serverBaseUrl,
                McpUrl = mcpUrl,
                EnableMcpConfigured = _enableMcpConfigured,
                EnableMcpActive = enableMcpActive,
                PortConflictFallbackActive = _portConflictFallbackActive,
                StatusMessage = BuildEndpointStatusMessage(actualPort, mcpUrl, enableMcpActive)
            };
        }
    }

    public string? GetCurrentAccessToken()
    {
        lock (_stateLock)
        {
            var host = _settingsRepository.LoadLocalSettings().Host;
            RefreshConfiguredAccessTokenState(host);
            RefreshConfiguredEndpointState(host);
            return GetCurrentAccessTokenUnsafe();
        }
    }

    private string? GetCurrentAccessTokenUnsafe()
    {
        if (_configuredAccessTokenMode == DutyAccessTokenModes.Static)
        {
            return ResolveConfiguredStaticAccessToken();
        }

        return _state == EngineState.Ready && _activeAccessTokenMode == DutyAccessTokenModes.Dynamic
            ? _accessToken
            : null;
    }

    private void RefreshConfiguredAccessTokenState(DutyPersistedHostSettings? host = null)
    {
        host ??= _settingsRepository.LoadLocalSettings().Host;
        _configuredAccessTokenMode = DutyAccessTokenModes.Normalize(host.AccessTokenMode);
        _staticAccessTokenConfigured = HasConfiguredStaticAccessToken(host);
        if (_configuredAccessTokenMode != DutyAccessTokenModes.Static && _state != EngineState.Ready)
        {
            _activeAccessTokenMode = _configuredAccessTokenMode;
        }
    }

    private void RefreshConfiguredEndpointState(DutyPersistedHostSettings? host = null)
    {
        host ??= _settingsRepository.LoadLocalSettings().Host;
        _configuredServerPortMode = DutyServerPortModes.Normalize(host.ServerPortMode);
        _configuredFixedServerPort = NormalizeConfiguredServicePort(host.FixedServerPort);
        _enableMcpConfigured = host.EnableMcp;
    }

    private string BuildEndpointStatusMessage(int? actualPort, string mcpUrl, bool enableMcpActive)
    {
        if (_portConflictFallbackActive)
        {
            var fixedPortText = _configuredFixedServerPort?.ToString() ?? "未配置";
            var actualPortText = actualPort?.ToString() ?? "待分配";
            return _enableMcpConfigured
                ? $"固定服务端口 {fixedPortText} 已冲突，后端已回退到随机端口 {actualPortText}，本次运行 MCP 已禁用。请修复端口配置并重启应用。"
                : $"固定服务端口 {fixedPortText} 已冲突，后端已回退到随机端口 {actualPortText}。";
        }

        if (_state == EngineState.Faulted)
        {
            return _enableMcpConfigured
                ? "后端启动失败，MCP 地址当前不可用。"
                : "后端启动失败。";
        }

        if (_state != EngineState.Ready)
        {
            return _enableMcpConfigured
                ? "后端尚未就绪，MCP 地址暂不可用。"
                : "MCP 未启用。";
        }

        if (!_enableMcpConfigured && enableMcpActive)
        {
            return $"MCP 已保存为关闭；当前运行仍启用，地址：{mcpUrl}。重启后关闭。";
        }

        if (_enableMcpConfigured && !enableMcpActive)
        {
            return "MCP 已保存为启用；当前运行尚未启用，重启后生效。";
        }

        if (enableMcpActive)
        {
            var restartHint = _configuredServerPortMode == DutyServerPortModes.Fixed &&
                              _configuredFixedServerPort.HasValue &&
                              actualPort.HasValue &&
                              _configuredFixedServerPort.Value != actualPort.Value
                ? $" 已保存固定端口 {_configuredFixedServerPort.Value}，重启后切换。"
                : string.Empty;
            return $"MCP 已启用，当前地址：{mcpUrl}。{restartHint}".Trim();
        }

        return string.IsNullOrWhiteSpace(ServerBaseUrl)
            ? "MCP 未启用。"
            : $"MCP 未启用；当前服务地址：{ServerBaseUrl}";
    }

    private string? ResolveConfiguredStaticAccessToken(DutyPersistedHostSettings? host = null)
    {
        host ??= _settingsRepository.LoadLocalSettings().Host;
        if (!HasConfiguredStaticAccessToken(host))
        {
            return null;
        }

        try
        {
            return SecurityHelper.DecryptString(host.StaticAccessTokenEncrypted);
        }
        catch
        {
            return null;
        }
    }

    private string ResolveRequiredStaticAccessToken(DutyPersistedHostSettings? host = null)
    {
        host ??= _settingsRepository.LoadLocalSettings().Host;
        var verifier = (host.StaticAccessTokenVerifier ?? string.Empty).Trim();
        var encrypted = (host.StaticAccessTokenEncrypted ?? string.Empty).Trim();
        if (_configuredAccessTokenMode != DutyAccessTokenModes.Static ||
            verifier.Length == 0 ||
            encrypted.Length == 0)
        {
            throw new InvalidOperationException("Static token mode is selected, but no valid static token is configured locally.");
        }

        var plainText = SecurityHelper.DecryptString(encrypted);
        if (!SecurityHelper.VerifyPasswordVerifier(plainText, verifier))
        {
            throw new InvalidOperationException("The configured static token does not match its verifier. Please set it again.");
        }

        return plainText;
    }

    private static bool HasConfiguredStaticAccessToken(DutyPersistedHostSettings? host)
    {
        return !string.IsNullOrWhiteSpace(host?.StaticAccessTokenEncrypted) &&
               !string.IsNullOrWhiteSpace(host?.StaticAccessTokenVerifier);
    }

    private static int? NormalizeConfiguredServicePort(int? port)
    {
        return port is >= MinServicePort and <= MaxServicePort ? port : null;
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
            if (_tokenModeTcs.Task.IsCompleted)
            {
                _tokenModeTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            if (_tokenTcs.Task.IsCompleted)
            {
                _tokenTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            _initializeTask = InitializeBackgroundAsync();
            return _initializeTask;
        }
    }

    private sealed record EngineLaunchOptions(int RequestedPort, bool DisableMcpRuntime, bool PortConflictFallbackActive);

    private sealed class PythonStartupException : Exception
    {
        public PythonStartupException(string message, bool isPortBindConflict, Exception? innerException = null)
            : base(message, innerException)
        {
            IsPortBindConflict = isPortBindConflict;
        }

        public bool IsPortBindConflict { get; }
    }

    private async Task InitializeBackgroundAsync()
    {
        try
        {
            DutyPersistedHostSettings hostSettings;
            lock (_stateLock)
            {
                hostSettings = _settingsRepository.LoadLocalSettings().Host;
                RefreshConfiguredAccessTokenState(hostSettings);
                RefreshConfiguredEndpointState(hostSettings);
                _activeAccessTokenMode = _configuredAccessTokenMode;
                _accessToken = _configuredAccessTokenMode == DutyAccessTokenModes.Static
                    ? ResolveRequiredStaticAccessToken(hostSettings)
                    : null;
                _runtimeMcpEnabled = _enableMcpConfigured;
                _portConflictFallbackActive = false;
            }

            var pythonPath = DutyScheduleOrchestrator.ValidatePythonPath(
                _configManager.Config.PythonPath,
                _pluginPaths.PluginFolderPath,
                _pluginPaths.AssetsDirectory);
            var scriptPath = _pluginPaths.CoreScriptPath;
            
            if (!File.Exists(scriptPath))
            {
                throw new FileNotFoundException($"Core script not found at {scriptPath}");
            }

            var launchOptions = BuildInitialLaunchOptions();
            try
            {
                await StartPythonProcessAsync(pythonPath, scriptPath, launchOptions).ConfigureAwait(false);
            }
            catch (PythonStartupException ex) when (ex.IsPortBindConflict &&
                                                    launchOptions.RequestedPort > 0 &&
                                                    !launchOptions.PortConflictFallbackActive)
            {
                DutyDiagnosticsLogger.Warn("BackendIpc", "Fixed service port failed during Python startup; retrying with random port and runtime MCP disabled.",
                    new
                    {
                        requestedPort = launchOptions.RequestedPort,
                        error = ex.Message
                    });
                ShutdownPythonServer();
                launchOptions = new EngineLaunchOptions(0, DisableMcpRuntime: true, PortConflictFallbackActive: true);
                await StartPythonProcessAsync(pythonPath, scriptPath, launchOptions).ConfigureAwait(false);
            }

            lock (_stateLock)
            {
                _runtimeMcpEnabled = _enableMcpConfigured && !launchOptions.DisableMcpRuntime;
                _portConflictFallbackActive = launchOptions.PortConflictFallbackActive;
                _state = EngineState.Ready;
            }

            _portTcs.TrySetResult(_serverPort);
            _tokenModeTcs.TrySetResult(_activeAccessTokenMode);
            if (_activeAccessTokenMode == DutyAccessTokenModes.Dynamic)
            {
                if (string.IsNullOrWhiteSpace(_accessToken))
                {
                    throw new InvalidOperationException("Python engine started in dynamic token mode, but no runtime token was received.");
                }

                _tokenTcs.TrySetResult(_accessToken);
            }

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
            _tokenModeTcs.TrySetException(new Exception(LastErrorMessage));
            _tokenTcs.TrySetException(new Exception(LastErrorMessage));
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

    private EngineLaunchOptions BuildInitialLaunchOptions()
    {
        if (_configuredServerPortMode == DutyServerPortModes.Fixed && _configuredFixedServerPort.HasValue)
        {
            if (!IsLoopbackPortAvailable(_configuredFixedServerPort.Value))
            {
                return new EngineLaunchOptions(0, DisableMcpRuntime: true, PortConflictFallbackActive: true);
            }

            return new EngineLaunchOptions(_configuredFixedServerPort.Value, DisableMcpRuntime: false, PortConflictFallbackActive: false);
        }

        return new EngineLaunchOptions(0, DisableMcpRuntime: false, PortConflictFallbackActive: false);
    }

    private async Task StartPythonProcessAsync(string pythonPath, string scriptPath, EngineLaunchOptions launchOptions)
    {
        lock (_errorBuffer)
        {
            _errorBuffer.Clear();
        }

        var arguments = new StringBuilder()
            .Append('"')
            .Append(scriptPath)
            .Append("\" --data-dir \"")
            .Append(_pluginPaths.DataDirectory)
            .Append("\" --server --port ")
            .Append(launchOptions.RequestedPort);
        if (launchOptions.DisableMcpRuntime)
        {
            arguments.Append(" --disable-mcp-runtime");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = pythonPath,
            Arguments = arguments.ToString(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8
        };

        var startupPortTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var startupTokenModeTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var startupTokenTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var startupCompleted = false;

        lock (_stateLock)
        {
            _pythonProcess = process;
        }

        process.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data))
            {
                return;
            }

            Debug.WriteLine($"[Python] {e.Data}");
            if (e.Data.StartsWith(PortBootstrapPrefix, StringComparison.Ordinal))
            {
                var portText = e.Data[PortBootstrapPrefix.Length..];
                if (int.TryParse(portText, out var port))
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

                return;
            }

            if (e.Data.StartsWith(TokenModeBootstrapPrefix, StringComparison.Ordinal))
            {
                var mode = DutyAccessTokenModes.Normalize(e.Data[TokenModeBootstrapPrefix.Length..]);
                lock (_stateLock)
                {
                    if (!ReferenceEquals(_pythonProcess, process))
                    {
                        return;
                    }

                    _activeAccessTokenMode = mode;
                }

                startupTokenModeTcs.TrySetResult(mode);
                return;
            }

            if (e.Data.StartsWith(TokenBootstrapPrefix, StringComparison.Ordinal))
            {
                var token = e.Data[TokenBootstrapPrefix.Length..].Trim();
                if (string.IsNullOrWhiteSpace(token))
                {
                    return;
                }

                lock (_stateLock)
                {
                    if (!ReferenceEquals(_pythonProcess, process))
                    {
                        return;
                    }

                    _accessToken = token;
                }

                startupTokenTcs.TrySetResult(token);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data))
            {
                return;
            }

            Debug.WriteLine($"[Python ERR] {e.Data}");
            lock (_errorBuffer)
            {
                if (_errorBuffer.Length < 10000)
                {
                    _errorBuffer.AppendLine(e.Data);
                }
            }
        };

        process.Exited += (_, _) =>
        {
            var exitCode = -1;
            try { exitCode = process.ExitCode; } catch { }
            PythonProcessTracker.Unregister(process, _processSnapshotPath);

            if (!startupCompleted)
            {
                var startupException = new Exception($"Python engine process exited unexpectedly with code {exitCode}");
                startupPortTcs.TrySetException(startupException);
                startupTokenModeTcs.TrySetException(startupException);
                startupTokenTcs.TrySetException(startupException);
                return;
            }

            var shouldFaultActiveEngine = false;
            lock (_stateLock)
            {
                shouldFaultActiveEngine = ReferenceEquals(_pythonProcess, process) &&
                                          (_state == EngineState.Ready || _state == EngineState.Initializing);
                if (shouldFaultActiveEngine)
                {
                    _state = EngineState.Faulted;
                    LastErrorMessage = $"Engine process exited unexpectedly with code {exitCode}";
                    _serverPort = 0;
                    _runtimeMcpEnabled = false;
                    _activeAccessTokenMode = _configuredAccessTokenMode;
                    _accessToken = _configuredAccessTokenMode == DutyAccessTokenModes.Static
                        ? ResolveConfiguredStaticAccessToken()
                        : null;
                }
            }

            var activeException = new Exception($"Python engine process exited unexpectedly with code {exitCode}");
            _portTcs.TrySetException(activeException);
            _tokenModeTcs.TrySetException(activeException);
            _tokenTcs.TrySetException(activeException);
        };

        try
        {
            if (!process.Start())
            {
                throw new Exception("Failed to start Python process.");
            }

            EnsureProcessBoundToJob(process);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            PythonProcessTracker.Register(process, _processSnapshotPath);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(EngineStartupTimeoutSeconds));
            using (cts.Token.Register(() => startupPortTcs.TrySetException(new TimeoutException($"Python engine initialization timed out ({EngineStartupTimeoutSeconds}s)."))))
            {
                await startupPortTcs.Task.ConfigureAwait(false);
            }

            using (cts.Token.Register(() => startupTokenModeTcs.TrySetException(new TimeoutException($"Python engine token mode bootstrap timed out ({EngineStartupTimeoutSeconds}s)."))))
            {
                var activeMode = await startupTokenModeTcs.Task.ConfigureAwait(false);
                if (activeMode == DutyAccessTokenModes.Dynamic)
                {
                    using (cts.Token.Register(() => startupTokenTcs.TrySetException(new TimeoutException($"Python engine token bootstrap timed out ({EngineStartupTimeoutSeconds}s)."))))
                    {
                        await startupTokenTcs.Task.ConfigureAwait(false);
                    }
                }
                else if (string.IsNullOrWhiteSpace(_accessToken))
                {
                    throw new InvalidOperationException("Python engine started in static token mode, but the local static token is unavailable.");
                }
            }

            startupCompleted = true;
        }
        catch (Exception ex)
        {
            string forensicLog;
            lock (_errorBuffer)
            {
                forensicLog = _errorBuffer.ToString();
            }

            TryTerminateAttempt(process);
            lock (_stateLock)
            {
                if (ReferenceEquals(_pythonProcess, process))
                {
                    _pythonProcess = null;
                    _serverPort = 0;
                    _runtimeMcpEnabled = false;
                    _activeAccessTokenMode = _configuredAccessTokenMode;
                    _accessToken = _configuredAccessTokenMode == DutyAccessTokenModes.Static
                        ? ResolveConfiguredStaticAccessToken()
                        : null;
                }
            }

            var combinedMessage = string.IsNullOrWhiteSpace(forensicLog)
                ? ex.Message
                : $"{ex.Message}\n--- Python Error ---\n{forensicLog}";
            throw new PythonStartupException(combinedMessage, IsPortBindConflict(combinedMessage), ex);
        }
    }

    private static bool IsLoopbackPortAvailable(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static bool IsPortBindConflict(string? message)
    {
        var normalized = (message ?? string.Empty).ToLowerInvariant();
        return normalized.Contains("address already in use", StringComparison.Ordinal) ||
               normalized.Contains("only one usage of each socket address", StringComparison.Ordinal) ||
               normalized.Contains("errno 10048", StringComparison.Ordinal) ||
               normalized.Contains("error while attempting to bind", StringComparison.Ordinal);
    }

    private void TryTerminateAttempt(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
                process.WaitForExit(3000);
            }
        }
        catch
        {
        }
        finally
        {
            PythonProcessTracker.Unregister(process, _processSnapshotPath);
            process.Dispose();
        }
    }

    public async Task EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        if (_state == EngineState.Ready)
        {
            return;
        }
        
        Task<int> waitTask;
        Task<string> tokenModeWaitTask;
        Task<string> tokenWaitTask;
        lock (_stateLock)
        {
            if (_state == EngineState.Faulted)
            {
                throw new Exception($"AI Engine failed to start: {LastErrorMessage}");
            }

            waitTask = _portTcs.Task;
            tokenModeWaitTask = _tokenModeTcs.Task;
            tokenWaitTask = _tokenTcs.Task;
        }

        await EnsureStartedAsync().ConfigureAwait(false);

        await waitTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        var activeMode = await tokenModeWaitTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (activeMode == DutyAccessTokenModes.Dynamic)
        {
            await tokenWaitTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task RestartEngineAsync()
    {
        await StopAsync().ConfigureAwait(false);
        lock (_stateLock)
        {
            _state = EngineState.NotStarted;
            LastErrorMessage = null;
            if (_portTcs.Task.IsCompleted)
            {
                _portTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            if (_tokenModeTcs.Task.IsCompleted)
            {
                _tokenModeTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            if (_tokenTcs.Task.IsCompleted)
            {
                _tokenTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            RefreshConfiguredAccessTokenState();
            RefreshConfiguredEndpointState();
            _activeAccessTokenMode = _configuredAccessTokenMode;
            _accessToken = _configuredAccessTokenMode == DutyAccessTokenModes.Static
                ? ResolveConfiguredStaticAccessToken()
                : null;
            _runtimeMcpEnabled = false;
            _portConflictFallbackActive = false;
        }
        await EnsureReadyAsync().ConfigureAwait(false);
    }

    public Task StopAsync()
    {
        DisposeControlSocket();
        ShutdownPythonServer();

        lock (_stateLock)
        {
            _state = EngineState.NotStarted;
            LastErrorMessage = null;
            if (_portTcs.Task.IsCompleted)
            {
                _portTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            if (_tokenModeTcs.Task.IsCompleted)
            {
                _tokenModeTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            if (_tokenTcs.Task.IsCompleted)
            {
                _tokenTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            RefreshConfiguredAccessTokenState();
            RefreshConfiguredEndpointState();
            _activeAccessTokenMode = _configuredAccessTokenMode;
            _accessToken = _configuredAccessTokenMode == DutyAccessTokenModes.Static
                ? ResolveConfiguredStaticAccessToken()
                : null;
            _runtimeMcpEnabled = false;
            _portConflictFallbackActive = false;
        }

        return Task.CompletedTask;
    }

    public async Task<CoreRunResult> RunScheduleAsync(object requestPayload, Action<CoreRunProgress>? progressCallback, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        await _controlSocketGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                return await RunScheduleViaSocketAsync(requestPayload, progressCallback, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsControlSocketRecoverable(ex))
            {
                DutyDiagnosticsLogger.Warn("BackendIpc", "Schedule run via WebSocket failed; falling back to HTTP SSE.",
                    new { error = ex.Message });
                DisposeControlSocket();
                return await RunScheduleViaHttpAsync(requestPayload, progressCallback, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _controlSocketGate.Release();
        }
    }

    private async Task<CoreRunResult> RunScheduleViaSocketAsync(object requestPayload, Action<CoreRunProgress>? progressCallback, CancellationToken cancellationToken)
    {
        var clientChangeId = Guid.NewGuid().ToString("N");
        var traceId = DutyDiagnosticsLogger.CreateTraceId("schedule");
        var socket = await EnsureControlSocketConnectedAsync("host", traceId, cancellationToken).ConfigureAwait(false);

        // Parse instruction from requestPayload
        var payloadJson = JsonSerializer.Serialize(requestPayload, JsonOptions);
        var payloadElement = JsonSerializer.Deserialize<JsonElement>(payloadJson);
        var instruction = payloadElement.TryGetProperty("instruction", out var instrProp) ? instrProp.GetString() ?? "" : "";
        var requestSource = payloadElement.TryGetProperty("request_source", out var srcProp) ? srcProp.GetString() ?? "host" : "host";

        await SendControlSocketMessageAsync(socket, new
        {
            type = "schedule_run",
            client_change_id = clientChangeId,
            trace_id = traceId,
            request_source = requestSource,
            instruction
        }, cancellationToken).ConfigureAwait(false);

        while (true)
        {
            using var message = await ReceiveControlSocketMessageAsync(socket, cancellationToken).ConfigureAwait(false);
            var root = message.RootElement;
            var messageType = root.TryGetProperty("type", out var typeElement)
                ? (typeElement.GetString() ?? string.Empty).Trim().ToLowerInvariant()
                : string.Empty;
            var messageClientChangeId = root.TryGetProperty("client_change_id", out var changeElement)
                ? (changeElement.GetString() ?? string.Empty).Trim()
                : string.Empty;

            if (messageClientChangeId.Length > 0 && !string.Equals(messageClientChangeId, clientChangeId, StringComparison.Ordinal))
            {
                continue;
            }

            switch (messageType)
            {
                case "accepted":
                case "hello":
                    continue;
                case "schedule_progress":
                {
                    var phase = root.TryGetProperty("phase", out var phaseProp) ? phaseProp.GetString() ?? "" : "";
                    var msg = root.TryGetProperty("message", out var msgProp) ? msgProp.GetString() ?? "" : "";
                    var chunk = root.TryGetProperty("stream_chunk", out var chunkProp) ? chunkProp.GetString() : null;
                    progressCallback?.Invoke(new CoreRunProgress(phase, msg, chunk));
                    continue;
                }
                case "schedule_complete":
                {
                    var status = root.TryGetProperty("status", out var statusProp) ? statusProp.GetString() ?? "" : "";
                    if (string.Equals(status, "success", StringComparison.OrdinalIgnoreCase))
                    {
                        var aiResponse = root.TryGetProperty("ai_response", out var aiProp) ? aiProp.GetString() : null;
                        return CoreRunResult.Ok("Success", aiResponse);
                    }
                    var errMsg = root.TryGetProperty("message", out var errProp) ? errProp.GetString() ?? "Unknown error" : "Unknown error";
                    return CoreRunResult.Fail(errMsg);
                }
                case "error":
                {
                    var errMsg = root.TryGetProperty("message", out var errProp) ? errProp.GetString() ?? "Unknown error" : "Unknown error";
                    return CoreRunResult.Fail(errMsg);
                }
                default:
                    continue;
            }
        }
    }

    private async Task<CoreRunResult> RunScheduleViaHttpAsync(object requestPayload, Action<CoreRunProgress>? progressCallback, CancellationToken cancellationToken)
    {
        var url = $"http://127.0.0.1:{_serverPort}/api/v1/duty/schedule";
        var jsonPayload = JsonSerializer.Serialize(requestPayload);
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        ApplyAuthorizationHeader(request);
        request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

        try
        {
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream);

            CoreRunResult? finalResult = null;
            string? currentEvent = null;
            var dataBuffer = new StringBuilder();

            while (!reader.EndOfStream)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync().ConfigureAwait(false);

                if (string.IsNullOrEmpty(line))
                {
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
                        try
                        {
                            var evt = JsonSerializer.Deserialize<JsonElement>(dataBuffer.ToString());
                            var phase = evt.GetProperty("phase").GetString() ?? "";
                            var progressMessage = evt.GetProperty("message").GetString() ?? "";
                            var chunk = evt.TryGetProperty("stream_chunk", out var cp) ? cp.GetString() : null;
                            progressCallback?.Invoke(new CoreRunProgress(phase, progressMessage, chunk));
                        }
                        catch { }
                    }

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
        return await SendJsonAsync<DutyBackendConfig>(
            HttpMethod.Get,
            "/api/v1/config",
            null,
            requestSource,
            traceId,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<DutyBackendConfig> UpdateBackendConfigAsync(
        DutyBackendConfigPatch patch,
        string requestSource = "host_settings",
        string? traceId = null,
        CancellationToken cancellationToken = default)
    {
        return await SendJsonAsync<DutyBackendConfig>(
            HttpMethod.Patch,
            "/api/v1/config",
            patch,
            requestSource,
            traceId,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<DutyScheduleEntrySaveResponse> SaveScheduleEntryAsync(
        DutyScheduleEntrySaveRequest request,
        string requestSource = "host_settings",
        string? traceId = null,
        CancellationToken cancellationToken = default)
    {
        return await SendJsonAsync<DutyScheduleEntrySaveResponse>(
            HttpMethod.Post,
            "/api/v1/duty/schedule-entry",
            request,
            requestSource,
            traceId,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ClientWebSocket> EnsureControlSocketConnectedAsync(
        string requestSource,
        string traceId,
        CancellationToken cancellationToken)
    {
        if (_controlSocket is { State: WebSocketState.Open })
        {
            return _controlSocket;
        }

        DisposeControlSocket();

        var socket = new ClientWebSocket
        {
            Options =
            {
                KeepAliveInterval = TimeSpan.FromSeconds(20)
            }
        };
        socket.Options.SetRequestHeader(TraceHeaderName, traceId);
        socket.Options.SetRequestHeader(RequestSourceHeaderName, requestSource);
        ApplyAuthorizationHeader(socket.Options);
        var uri = new Uri($"ws://127.0.0.1:{_serverPort}/api/v1/duty/live");
        await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
        _controlSocket = socket;

        await SendControlSocketMessageAsync(socket, new
        {
            type = "hello",
            trace_id = traceId,
            request_source = requestSource
        }, cancellationToken).ConfigureAwait(false);

        using var helloDocument = await ReceiveControlSocketMessageAsync(socket, cancellationToken).ConfigureAwait(false);
        var root = helloDocument.RootElement;
        var messageType = root.TryGetProperty("type", out var typeElement)
            ? (typeElement.GetString() ?? string.Empty).Trim().ToLowerInvariant()
            : string.Empty;
        if (!string.Equals(messageType, "hello", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Duty control channel handshake returned an unexpected message.");
        }

        return socket;
    }

    private static async Task SendControlSocketMessageAsync(
        ClientWebSocket socket,
        object payload,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var buffer = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonDocument> ReceiveControlSocketMessageAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var stream = new MemoryStream();

        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new WebSocketException("Duty control channel was closed by the server.");
            }

            if (result.Count > 0)
            {
                stream.Write(buffer, 0, result.Count);
            }

            if (result.EndOfMessage)
            {
                break;
            }
        }

        stream.Position = 0;
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static bool IsControlSocketRecoverable(Exception ex)
    {
        return ex is WebSocketException ||
               ex is IOException ||
               ex is ObjectDisposedException ||
               ex is InvalidOperationException;
    }

    private void DisposeControlSocket()
    {
        if (_controlSocket == null)
        {
            return;
        }

        try
        {
            _controlSocket.Dispose();
        }
        catch
        {
        }
        finally
        {
            _controlSocket = null;
        }
    }



    public async Task<DutyBackendSnapshot> GetBackendSnapshotAsync(
        string requestSource = "host_settings",
        string? traceId = null,
        CancellationToken cancellationToken = default)
    {
        return await SendJsonAsync<DutyBackendSnapshot>(HttpMethod.Get, "/api/v1/snapshot", null, requestSource, traceId, cancellationToken).ConfigureAwait(false);
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

        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        using var request = new HttpRequestMessage(method, $"http://127.0.0.1:{_serverPort}{relativePath}");
        ApplyAuthorizationHeader(request);
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
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
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
            DutyScheduleEntrySaveRequest request => new
            {
                sourceDate = request.SourceDate ?? "<new>",
                targetDate = request.TargetDate,
                confirmOverwrite = request.ConfirmOverwrite,
                ledgerMode = request.LedgerMode,
                areaCount = request.AreaAssignments?.Count ?? 0
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

    private void ApplyAuthorizationHeader(HttpRequestMessage request)
    {
        if (string.IsNullOrWhiteSpace(_accessToken))
        {
            throw new InvalidOperationException("Backend access token is unavailable.");
        }

        request.Headers.Remove(AuthorizationHeaderName);
        request.Headers.TryAddWithoutValidation(AuthorizationHeaderName, $"Bearer {_accessToken}");
    }

    private void ApplyAuthorizationHeader(ClientWebSocketOptions options)
    {
        if (string.IsNullOrWhiteSpace(_accessToken))
        {
            throw new InvalidOperationException("Backend access token is unavailable.");
        }

        options.SetRequestHeader(AuthorizationHeaderName, $"Bearer {_accessToken}");
    }

    private void ShutdownPythonServer()
    {
        DisposeControlSocket();
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
                using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{_serverPort}/shutdown");
                if (!string.IsNullOrWhiteSpace(_accessToken))
                {
                    request.Headers.TryAddWithoutValidation(AuthorizationHeaderName, $"Bearer {_accessToken}");
                }
                var shutdownTask = client.SendAsync(request);
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
            RefreshConfiguredAccessTokenState();
            RefreshConfiguredEndpointState();
            _activeAccessTokenMode = _configuredAccessTokenMode;
            _accessToken = _configuredAccessTokenMode == DutyAccessTokenModes.Static
                ? ResolveConfiguredStaticAccessToken()
                : null;
            _runtimeMcpEnabled = false;
            _portConflictFallbackActive = false;
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
        _controlSocketGate.Dispose();
        _httpClient.Dispose();
    }
}
