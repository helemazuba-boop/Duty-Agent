using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DutyAgentBridge.Models;
using DutyAgentBridge.Services;

namespace DutyAgentBridge.Services;

public interface IIpcBridgeService : IDisposable
{
    IpcBridgeState State { get; }
    string? LastError { get; }
    StandaloneMeta? CurrentMeta { get; }

    Task ConnectAsync(CancellationToken cancellationToken = default);
    void Disconnect();
    void Reconnect();
    Task SendHeartbeatAsync(CancellationToken cancellationToken = default);
    Task ListenNotificationsAsync(
        Func<DutyNotificationEvent, Task> onNotification,
        CancellationToken cancellationToken = default);

    Task<CoreRunResult> RunScheduleAsync(
        string instruction,
        Action<CoreRunProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<CoreRunResult> RollbackAsync(CancellationToken cancellationToken = default);
    Task CancelAsync(CancellationToken cancellationToken = default);

    Task<DutyBackendSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
    Task<DutyBackendConfig> GetConfigAsync(CancellationToken cancellationToken = default);
    Task<DutyBackendConfig> UpdateConfigAsync(DutyBackendConfigPatch patch, CancellationToken cancellationToken = default);
    Task SaveScheduleEntryAsync(DutyScheduleEntrySaveRequest request, CancellationToken cancellationToken = default);

    event EventHandler<IpcBridgeState>? StateChanged;
    event EventHandler<string>? ErrorOccurred;
}

public sealed class IpcBridgeService : IIpcBridgeService
{
    private readonly IBridgePaths _paths;
    private readonly HttpClient _httpClient;

    private IpcBridgeState _state = IpcBridgeState.Initial;
    private string? _lastError;
    private StandaloneMeta? _currentMeta;
    private string _accessToken = "";
    private CancellationTokenSource? _connectCts;

    private ClientWebSocket? _controlSocket;
    private string? _activeRunClientChangeId;
    private readonly SemaphoreSlim _socketGate = new(1, 1);
    private readonly SemaphoreSlim _stateGate = new(1, 1);

    private readonly TimeSpan _connectTimeout = TimeSpan.FromSeconds(15);
    private readonly TimeSpan _requestTimeout = TimeSpan.FromMinutes(5);

    private const string AuthorizationHeader = "Authorization";
    private const string TraceHeader = "X-Duty-Trace-Id";
    private const string RequestSourceHeader = "X-Duty-Request-Source";
    private const string TraceHeaderValue = "bridge";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public IpcBridgeState State
    {
        get => _state;
        private set
        {
            if (_state != value)
            {
                _state = value;
                StateChanged?.Invoke(this, value);
            }
        }
    }

    public string? LastError => _lastError;
    public StandaloneMeta? CurrentMeta => _currentMeta;

    public event EventHandler<IpcBridgeState>? StateChanged;
    public event EventHandler<string>? ErrorOccurred;

    public IpcBridgeService(IBridgePaths paths)
    {
        _paths = paths;
        _httpClient = new HttpClient { Timeout = _requestTimeout };
        LoadSettings();
    }

    private BridgeSettings _settings = new();

    private void LoadSettings()
    {
        try
        {
            var settingsPath = Path.Combine(_paths.SharedConfigFolder, "bridge-settings.json");
            if (File.Exists(settingsPath))
            {
                var json = File.ReadAllText(settingsPath);
                _settings = JsonSerializer.Deserialize<BridgeSettings>(json, JsonOptions) ?? new BridgeSettings();
            }
        }
        catch
        {
            _settings = new BridgeSettings();
        }
    }

    private void SaveSettings()
    {
        try
        {
            var settingsPath = Path.Combine(_paths.SharedConfigFolder, "bridge-settings.json");
            var json = JsonSerializer.Serialize(_settings, JsonOptions);
            File.WriteAllText(settingsPath, json);
        }
        catch
        {
            // 静默忽略设置保存失败
        }
    }

    #region Connection Management

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _stateGate.WaitAsync(cancellationToken);
        try
        {
            _connectCts?.Cancel();
            _connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var linkedCt = _connectCts.Token;

            // 取消已有连接
            if (_controlSocket != null)
            {
                try { _controlSocket.Dispose(); } catch { }
                _controlSocket = null;
            }

            SetState(IpcBridgeState.Checking);
            _lastError = null;

            // 读取元数据文件
            var meta = await LoadMetaWithRetryAsync(linkedCt);
            if (meta == null)
            {
                SetState(IpcBridgeState.NotInstalled);
                SetError("未检测到独立 Duty-Agent 软件。请确保已启动独立软件。");
                return;
            }

            _currentMeta = meta;

            // 等待端口分配（独立软件启动时 port=0，分配后写入）
            var port = meta.Port;
            var timeoutAt = DateTime.UtcNow.Add(_connectTimeout);

            while (port <= 0 && DateTime.UtcNow < timeoutAt && !linkedCt.IsCancellationRequested)
            {
                await Task.Delay(500, linkedCt);
                var reloaded = ReloadMeta();
                if (reloaded != null && reloaded.Port > 0)
                {
                    port = reloaded.Port;
                    _currentMeta = reloaded;
                    break;
                }
            }

            if (port <= 0)
            {
                SetState(IpcBridgeState.Error);
                SetError("独立软件启动超时（未分配端口）。请检查独立软件是否正常运行。");
                return;
            }

            // 读取 Token
            if (!string.IsNullOrWhiteSpace(_currentMeta.Token))
            {
                _accessToken = _currentMeta.Token;
            }
            else
            {
                _accessToken = "";
            }

            SetState(IpcBridgeState.Connecting);

            await SendHeartbeatCoreAsync(port, linkedCt);
            SetState(IpcBridgeState.Connected);
        }
        catch (OperationCanceledException)
        {
            SetState(IpcBridgeState.Disconnected);
        }
        catch (Exception ex)
        {
            SetState(IpcBridgeState.Error);
            SetError($"连接失败: {ex.Message}");
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public void Disconnect()
    {
        _stateGate.Wait();
        try
        {
            _connectCts?.Cancel();

            if (_controlSocket != null)
            {
                try
                {
                    if (_controlSocket.State == WebSocketState.Open)
                    {
                        _controlSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bridge-disconnect", CancellationToken.None).Wait(1000);
                    }
                    _controlSocket.Dispose();
                }
                catch { }
                _controlSocket = null;
            }

            SetState(IpcBridgeState.Disconnected);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public void Reconnect()
    {
        Disconnect();
        _ = ConnectAsync();
    }

    public async Task SendHeartbeatAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        await SendHeartbeatCoreAsync(_currentMeta!.Port, cancellationToken);
    }

    public async Task ListenNotificationsAsync(
        Func<DutyNotificationEvent, Task> onNotification,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"http://127.0.0.1:{_currentMeta!.Port}/api/v1/notifications/stream");
        ApplyAuth(request);
        request.Headers.TryAddWithoutValidation(TraceHeader, CreateTraceId());
        request.Headers.TryAddWithoutValidation(RequestSourceHeader, "bridge");

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        string? eventName = null;
        var dataBuffer = new StringBuilder();

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line == null)
            {
                break;
            }

            if (line.Length == 0)
            {
                if (string.Equals(eventName, "notification", StringComparison.OrdinalIgnoreCase) &&
                    dataBuffer.Length > 0)
                {
                    var notification = JsonSerializer.Deserialize<DutyNotificationEvent>(
                        dataBuffer.ToString(),
                        JsonOptions);
                    if (notification != null &&
                        notification.Targets.Any(target => string.Equals(target, "classisland", StringComparison.OrdinalIgnoreCase)))
                    {
                        await onNotification(notification);
                    }
                }

                eventName = null;
                dataBuffer.Clear();
                continue;
            }

            if (line.StartsWith("event: ", StringComparison.OrdinalIgnoreCase))
            {
                eventName = line.Substring(7).Trim();
            }
            else if (line.StartsWith("data: ", StringComparison.OrdinalIgnoreCase))
            {
                dataBuffer.Append(line.Substring(6));
            }
        }
    }

    private async Task<StandaloneMeta?> LoadMetaWithRetryAsync(CancellationToken cancellationToken)
    {
        var timeoutAt = DateTime.UtcNow.Add(_connectTimeout);

        while (DateTime.UtcNow < timeoutAt && !cancellationToken.IsCancellationRequested)
        {
            var meta = ReloadMeta();
            if (meta != null)
            {
                return meta;
            }

            try
            {
                await Task.Delay(1000, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return null;
    }

    private StandaloneMeta? ReloadMeta()
    {
        try
        {
            var metaPath = _paths.MetaFilePath;
            if (!File.Exists(metaPath))
            {
                return null;
            }

            // 检查进程是否存活
            var json = File.ReadAllText(metaPath);
            var meta = JsonSerializer.Deserialize<StandaloneMeta>(json, JsonOptions);
            if (meta == null || meta.ProcessId <= 0)
            {
                return null;
            }

            // 验证进程存活
            if (!IsProcessRunning(meta.ProcessId))
            {
                return null;
            }

            return meta;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsProcessRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    #endregion

    #region Schedule Operations

    public async Task<CoreRunResult> RunScheduleAsync(
        string instruction,
        Action<CoreRunProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        await _socketGate.WaitAsync(cancellationToken);
        try
        {
            // 优先使用 WebSocket，失败时回退到 HTTP SSE
            try
            {
                return await RunScheduleViaSocketAsync(instruction, progress, cancellationToken);
            }
            catch (Exception ex) when (IsRecoverableSocketError(ex))
            {
                Diagnostics.Log("IpcBridge", "WebSocket failed, falling back to HTTP SSE.", "WARN");
                DisposeSocket();
                return await RunScheduleViaHttpAsync(instruction, progress, cancellationToken);
            }
        }
        finally
        {
            _socketGate.Release();
        }
    }

    private async Task<CoreRunResult> RunScheduleViaSocketAsync(
        string instruction,
        Action<CoreRunProgress>? progress,
        CancellationToken cancellationToken)
    {
        var socket = await EnsureSocketConnectedAsync(cancellationToken);
        var clientChangeId = Guid.NewGuid().ToString("N");
        var traceId = CreateTraceId();

        await SendSocketMessageAsync(socket, new
        {
            type = "schedule_run",
            client_change_id = clientChangeId,
            trace_id = traceId,
            request_source = "bridge",
            instruction
        }, cancellationToken);

        _activeRunClientChangeId = clientChangeId;

        try
        {
            while (true)
            {
                using var doc = await ReceiveSocketMessageAsync(socket, cancellationToken);
                var root = doc.RootElement;

                var msgType = root.TryGetProperty("type", out var t) ? (t.GetString() ?? "").Trim().ToLowerInvariant() : "";
                var msgChangeId = root.TryGetProperty("client_change_id", out var c) ? (c.GetString() ?? "").Trim() : "";

                if (!string.IsNullOrEmpty(msgChangeId) && msgChangeId != clientChangeId)
                {
                    continue;
                }

                switch (msgType)
                {
                    case "accepted":
                    case "hello":
                        continue;

                    case "schedule_progress":
                    {
                        var phase = root.TryGetProperty("phase", out var p) ? (p.GetString() ?? "") : "";
                        var msg = root.TryGetProperty("message", out var m) ? (m.GetString() ?? "") : "";
                        var chunk = root.TryGetProperty("stream_chunk", out var ch) ? ch.GetString() : null;
                        progress?.Invoke(new CoreRunProgress(phase, msg, chunk));
                        continue;
                    }

                    case "schedule_complete":
                    {
                        var status = root.TryGetProperty("status", out var s) ? (s.GetString() ?? "") : "";
                        if (string.Equals(status, "success", StringComparison.OrdinalIgnoreCase))
                        {
                            var aiResponse = root.TryGetProperty("ai_response", out var a) ? a.GetString() : null;
                            return CoreRunResult.Ok("Success", aiResponse);
                        }
                        var errMsg = root.TryGetProperty("message", out var e) ? (e.GetString() ?? "Unknown error") : "Unknown error";
                        return CoreRunResult.Fail(errMsg);
                    }

                    case "schedule_cancelled":
                        return CoreRunResult.Fail("已取消排班执行。", code: "cancelled");

                    case "error":
                    {
                        var errMsg = root.TryGetProperty("message", out var e) ? (e.GetString() ?? "Unknown error") : "Unknown error";
                        return CoreRunResult.Fail(errMsg);
                    }
                }
            }
        }
        finally
        {
            _activeRunClientChangeId = null;
            // Release the backend single-owner claim as soon as this run ends;
            // keeping the cached socket open would lock Web UI / host runs out
            // of /api/v1/duty/live for the whole bridge uptime (4409 busy).
            DisposeSocket();
        }
    }

    private async Task<CoreRunResult> RunScheduleViaHttpAsync(
        string instruction,
        Action<CoreRunProgress>? progress,
        CancellationToken cancellationToken)
    {
        EnsureConnected();
        var port = _currentMeta!.Port;
        var url = $"http://127.0.0.1:{port}/api/v1/duty/schedule";

        var payload = new { instruction, request_source = "bridge" };
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        ApplyAuth(request);

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
            var line = await reader.ReadLineAsync(cancellationToken);

            if (string.IsNullOrEmpty(line))
            {
                if (currentEvent == "complete" && dataBuffer.Length > 0)
                {
                    try
                    {
                        var evt = JsonSerializer.Deserialize<JsonElement>(dataBuffer.ToString());
                        var status = evt.GetProperty("status").GetString();
                        if (status == "success")
                        {
                            var aiResponse = evt.TryGetProperty("ai_response", out var a) ? a.GetString() : null;
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
                        var phase = evt.TryGetProperty("phase", out var p) ? (p.GetString() ?? "") : "";
                        var msg = evt.TryGetProperty("message", out var m) ? (m.GetString() ?? "") : "";
                        var chunk = evt.TryGetProperty("stream_chunk", out var c) ? c.GetString() : null;
                        progress?.Invoke(new CoreRunProgress(phase, msg, chunk));
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

    public async Task<CoreRunResult> RollbackAsync(CancellationToken cancellationToken = default)
    {
        return await SendJsonAsync<CoreRunResult>(
            HttpMethod.Post,
            "/api/v1/duty/schedule-rollback",
            null,
            cancellationToken);
    }

    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        await _socketGate.WaitAsync(cancellationToken);
        try
        {
            var socket = _controlSocket;
            var changeId = _activeRunClientChangeId;
            if (socket is not { State: WebSocketState.Open } || string.IsNullOrEmpty(changeId))
            {
                return;
            }

            await SendSocketMessageAsync(socket, new
            {
                type = "schedule_cancel",
                client_change_id = changeId,
                trace_id = CreateTraceId(),
                request_source = "bridge"
            }, cancellationToken);
        }
        finally
        {
            _socketGate.Release();
        }
    }

    #endregion

    #region Data Operations

    public async Task<DutyBackendSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        return await SendJsonAsync<DutyBackendSnapshot>(HttpMethod.Get, "/api/v1/snapshot", null, cancellationToken);
    }

    public async Task<DutyBackendConfig> GetConfigAsync(CancellationToken cancellationToken = default)
    {
        return await SendJsonAsync<DutyBackendConfig>(HttpMethod.Get, "/api/v1/config", null, cancellationToken);
    }

    public async Task<DutyBackendConfig> UpdateConfigAsync(DutyBackendConfigPatch patch, CancellationToken cancellationToken = default)
    {
        return await SendJsonAsync<DutyBackendConfig>(HttpMethod.Patch, "/api/v1/config", patch, cancellationToken);
    }

    public async Task SaveScheduleEntryAsync(DutyScheduleEntrySaveRequest request, CancellationToken cancellationToken = default)
    {
        await SendJsonAsync<object>(HttpMethod.Post, "/api/v1/duty/schedule-entry", request, cancellationToken);
    }

    #endregion

    #region HTTP/Socket Helpers

    private async Task<T> SendJsonAsync<T>(
        HttpMethod method,
        string path,
        object? payload,
        CancellationToken cancellationToken)
    {
        EnsureConnected();
        var port = _currentMeta!.Port;

        using var request = new HttpRequestMessage(method, $"http://127.0.0.1:{port}{path}");
        ApplyAuth(request);
        request.Headers.TryAddWithoutValidation(TraceHeader, CreateTraceId());
        request.Headers.TryAddWithoutValidation(RequestSourceHeader, "bridge");

        if (payload != null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"HTTP {response.StatusCode}: {responseText}");
        }

        if (string.IsNullOrWhiteSpace(responseText))
        {
            return default!;
        }

        return JsonSerializer.Deserialize<T>(responseText, JsonOptions) ?? default!;
    }

    private async Task SendHeartbeatCoreAsync(int port, CancellationToken cancellationToken)
    {
        if (port <= 0)
        {
            throw new InvalidOperationException("Duty-Agent backend port is not available.");
        }

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/api/v1/bridge/heartbeat");
        ApplyAuth(request);
        request.Headers.TryAddWithoutValidation(TraceHeader, CreateTraceId());
        request.Headers.TryAddWithoutValidation(RequestSourceHeader, "bridge");
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Bridge heartbeat timed out.");
        }

        using (response)
        {
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Bridge heartbeat failed with HTTP {response.StatusCode}: {responseText}");
            }
        }
    }

    private async Task<ClientWebSocket> EnsureSocketConnectedAsync(CancellationToken cancellationToken)
    {
        if (_controlSocket is { State: WebSocketState.Open })
        {
            return _controlSocket;
        }

        DisposeSocket();
        EnsureConnected();

        var port = _currentMeta!.Port;
        var socket = new ClientWebSocket
        {
            Options = { KeepAliveInterval = TimeSpan.FromSeconds(20) }
        };

        socket.Options.SetRequestHeader(TraceHeader, CreateTraceId());
        socket.Options.SetRequestHeader(RequestSourceHeader, "bridge");
        ApplyAuth(socket.Options);

        var uri = new Uri($"ws://127.0.0.1:{port}/api/v1/duty/live");
        try
        {
            await socket.ConnectAsync(uri, cancellationToken);
            _controlSocket = socket;
        }
        catch
        {
            // Don't leak the handle when the engine refuses/drops the connect:
            // crash-restart cycles would accumulate one socket per failure.
            socket.Dispose();
            throw;
        }

        // Handshake
        await SendSocketMessageAsync(socket, new
        {
            type = "hello",
            trace_id = CreateTraceId(),
            request_source = "bridge"
        }, cancellationToken);

        using var helloDoc = await ReceiveSocketMessageAsync(socket, cancellationToken);
        var helloType = helloDoc.RootElement.TryGetProperty("type", out var t)
            ? (t.GetString() ?? "").Trim().ToLowerInvariant()
            : "";
        if (!string.Equals(helloType, "hello", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Duty control channel handshake failed.");
        }

        return socket;
    }

    private static async Task SendSocketMessageAsync(
        ClientWebSocket socket,
        object payload,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var buffer = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, cancellationToken);
    }

    // Idle guard mirroring the host side: a wedged engine that neither answers
    // nor closes must not hang a schedule run (and its busy gate) forever.
    private static readonly TimeSpan SocketIdleReceiveTimeout = TimeSpan.FromMinutes(15);

    private static async Task<JsonDocument> ReceiveSocketMessageAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var stream = new MemoryStream();
        using var idleCts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idleCts.CancelAfter(SocketIdleReceiveTimeout);

        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), idleCts.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new WebSocketException("WebSocket closed by server.");
                }

                if (result.Count > 0)
                {
                    stream.Write(buffer, 0, result.Count);
                    idleCts.CancelAfter(SocketIdleReceiveTimeout);
                }

                if (result.EndOfMessage)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Duty control socket received no complete message within {(int)SocketIdleReceiveTimeout.TotalMinutes} minutes; treating the engine as unresponsive.");
        }

        stream.Position = 0;
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private void ApplyAuth(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(_accessToken))
        {
            request.Headers.Remove(AuthorizationHeader);
            request.Headers.TryAddWithoutValidation(AuthorizationHeader, $"Bearer {_accessToken}");
        }
    }

    private void ApplyAuth(ClientWebSocketOptions options)
    {
        if (!string.IsNullOrWhiteSpace(_accessToken))
        {
            options.SetRequestHeader(AuthorizationHeader, $"Bearer {_accessToken}");
        }
    }

    private static bool IsRecoverableSocketError(Exception ex)
    {
        return ex is WebSocketException ||
               ex is IOException ||
               ex is ObjectDisposedException ||
               ex is InvalidOperationException;
    }

    private void DisposeSocket()
    {
        if (_controlSocket == null) return;
        try { _controlSocket.Dispose(); } catch { }
        _controlSocket = null;
    }

    private void EnsureConnected()
    {
        if (State != IpcBridgeState.Connected || _currentMeta == null || _currentMeta.Port <= 0)
        {
            throw new InvalidOperationException($"IpcBridge is not connected. Current state: {State}");
        }
    }

    private void SetState(IpcBridgeState newState)
    {
        State = newState;
    }

    private void SetError(string error)
    {
        _lastError = error;
        ErrorOccurred?.Invoke(this, error);
        Diagnostics.Log("IpcBridge", error, "ERROR");
    }

    private static string CreateTraceId() => $"bridge-{Guid.NewGuid():N}";

    #endregion

    public void Dispose()
    {
        Disconnect();
        _httpClient.Dispose();
        _socketGate.Dispose();
        _stateGate.Dispose();
    }
}
