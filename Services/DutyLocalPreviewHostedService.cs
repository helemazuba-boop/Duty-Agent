using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DutyIsland.Models;
using Microsoft.Extensions.Hosting;

namespace DutyIsland.Services;

public sealed class DutyLocalPreviewHostedService : IHostedService, IDisposable
{
    private static readonly int[] CandidatePorts = [48380, 48381, 48382, 48383, 48384];
    private static readonly TimeSpan McpKeepAliveInterval = TimeSpan.FromSeconds(20);
    private const string McpProtocolVersion = "2024-11-05";
    private const string McpToolScheduleOverwrite = "schedule_overwrite";
    private const string McpToolRosterImportStudents = "roster_import_students";
    private const string McpToolConfigUpdateSettings = "config_update_settings";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly DutyBackendService _backendService;
    private readonly string _testFilePath;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenLoopTask;
    private readonly ConcurrentDictionary<string, McpSseSession> _mcpSessions = new(StringComparer.Ordinal);

    public string PreviewUrl { get; private set; } = string.Empty;

    public string ApiOverwriteUrl
    {
        get
        {
            if (string.IsNullOrWhiteSpace(PreviewUrl) || !Uri.TryCreate(PreviewUrl, UriKind.Absolute, out var uri))
            {
                return string.Empty;
            }

            return $"{uri.GetLeftPart(UriPartial.Authority)}/api/v1/schedule/overwrite";
        }
    }

    public string McpUrl
    {
        get
        {
            if (string.IsNullOrWhiteSpace(PreviewUrl) || !Uri.TryCreate(PreviewUrl, UriKind.Absolute, out var uri))
            {
                return string.Empty;
            }

            return $"{uri.GetLeftPart(UriPartial.Authority)}/mcp";
        }
    }

    public DutyLocalPreviewHostedService(DutyBackendService backendService)
    {
        _backendService = backendService;
        var baseDir = Path.GetDirectoryName(typeof(DutyLocalPreviewHostedService).Assembly.Location) ?? AppContext.BaseDirectory;
        _testFilePath = Path.Combine(baseDir, "Assets_Duty", "web", "test.html");
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_testFilePath))
        {
            DutyDiagnosticsLogger.Warn("WebPreview", "test.html not found, local preview server disabled.",
                new { path = _testFilePath });
            return Task.CompletedTask;
        }

        foreach (var port in CandidatePorts)
        {
            var prefix = $"http://127.0.0.1:{port}/";
            var listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            try
            {
                listener.Start();
                _listener = listener;
                PreviewUrl = $"{prefix}test.html";
                break;
            }
            catch (Exception ex)
            {
                listener.Close();
                DutyDiagnosticsLogger.Warn("WebPreview", "Failed to bind preview port.",
                    new { port, message = ex.Message });
            }
        }

        if (_listener == null)
        {
            DutyDiagnosticsLogger.Warn("WebPreview", "No available local preview port.");
            return Task.CompletedTask;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listenLoopTask = Task.Run(() => ListenLoopAsync(_cts.Token), _cts.Token);
        DutyDiagnosticsLogger.Info("WebPreview", "Local preview server started.",
            new { url = PreviewUrl, apiOverwriteUrl = ApiOverwriteUrl, mcpUrl = McpUrl, testFile = _testFilePath });
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts != null && !_cts.IsCancellationRequested)
        {
            _cts.Cancel();
        }

        if (_listener?.IsListening == true)
        {
            _listener.Stop();
        }

        CloseAllMcpSessions();

        if (_listenLoopTask != null)
        {
            try
            {
                await _listenLoopTask.WaitAsync(cancellationToken);
            }
            catch
            {
            }
        }

        _listener?.Close();
        _listener = null;

        _cts?.Dispose();
        _cts = null;
        _listenLoopTask = null;
        PreviewUrl = string.Empty;
    }

    private async Task ListenLoopAsync(CancellationToken cancellationToken)
    {
        var listener = _listener;
        if (listener == null)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext? context = null;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (HttpListenerException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                continue;
            }
            catch (InvalidOperationException)
            {
                break;
            }

            if (context == null)
            {
                continue;
            }

            _ = Task.Run(() => HandleRequest(context));
        }
    }

    private void HandleRequest(HttpListenerContext context)
    {
        try
        {
            SetCorsHeaders(context.Response);

            if (string.Equals(context.Request.HttpMethod, "OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                WriteText(context.Response, string.Empty, "text/plain; charset=utf-8", 204);
                return;
            }

            var path = context.Request.Url?.AbsolutePath ?? "/";
            if (path.Equals("/", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("/test.html", StringComparison.OrdinalIgnoreCase))
            {
                WriteFile(context.Response, _testFilePath, "text/html; charset=utf-8");
                return;
            }

            if (path.Equals("/health", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(context.Response, new
                {
                    status = "ok",
                    preview_url = PreviewUrl,
                    api_overwrite_url = ApiOverwriteUrl,
                    mcp_url = McpUrl
                }, 200);
                return;
            }

            if (path.Equals("/mcp", StringComparison.OrdinalIgnoreCase))
            {
                HandleStreamableMcp(context);
                return;
            }

            if (path.Equals("/api/v1/schedule/overwrite", StringComparison.OrdinalIgnoreCase))
            {
                HandleOverwriteScheduleRequest(context);
                return;
            }

            WriteText(context.Response, "Not Found", "text/plain; charset=utf-8", 404);
        }
        catch (Exception ex)
        {
            DutyDiagnosticsLogger.Error("WebPreview", "Failed to handle preview request.", ex);
            TryCloseResponse(context.Response);
        }
    }

    private void HandleOverwriteScheduleRequest(HttpListenerContext context)
    {
        if (!string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
        {
            WriteApiError(context.Response, 405, "method_not_allowed", "Use POST /api/v1/schedule/overwrite.");
            return;
        }

        OverwriteScheduleRequest? request;
        try
        {
            var body = ReadRequestBody(context.Request);
            request = JsonSerializer.Deserialize<OverwriteScheduleRequest>(body, JsonOptions);
        }
        catch (Exception ex)
        {
            WriteApiError(context.Response, 400, "invalid_json", $"Failed to parse JSON body: {ex.Message}");
            return;
        }

        var instruction = (request?.Instruction ?? string.Empty).Trim();
        var responsePayload = ExecuteOverwriteSchedule(instruction, request?.Config, out var statusCode);

        DutyDiagnosticsLogger.Info("WebPreview", "Overwrite API request handled.",
            new
            {
                success = responsePayload.Success,
                code = responsePayload.Code,
                statusCode,
                instructionLength = instruction.Length,
                durationMs = responsePayload.DurationMs
            });
        WriteJson(context.Response, responsePayload, statusCode);
    }

    private OverwriteScheduleResponse ExecuteOverwriteSchedule(
        string instruction,
        OverwriteScheduleConfig? config,
        out int statusCode)
    {
        var normalizedInstruction = (instruction ?? string.Empty).Trim();
        if (normalizedInstruction.Length == 0)
        {
            statusCode = 400;
            return new OverwriteScheduleResponse
            {
                Success = false,
                Code = "validation",
                Message = "`instruction` cannot be empty.",
                ApplyMode = "replace_all",
                PreviewUrl = PreviewUrl,
                ApiOverwriteUrl = ApiOverwriteUrl,
                McpUrl = McpUrl,
                State = _backendService.LoadState()
            };
        }

        try
        {
            if (config != null)
            {
                ApplyOverwriteConfig(config);
            }
        }
        catch (Exception ex)
        {
            statusCode = 400;
            return new OverwriteScheduleResponse
            {
                Success = false,
                Code = "invalid_config",
                Message = ex.Message,
                ApplyMode = "replace_all",
                PreviewUrl = PreviewUrl,
                ApiOverwriteUrl = ApiOverwriteUrl,
                McpUrl = McpUrl,
                State = _backendService.LoadState()
            };
        }

        var startedAt = DateTimeOffset.Now;
        var result = _backendService.RunCoreAgentWithMessage(normalizedInstruction, applyMode: "replace_all");
        var finishedAt = DateTimeOffset.Now;

        var responsePayload = new OverwriteScheduleResponse
        {
            Success = result.Success,
            Code = string.IsNullOrWhiteSpace(result.Code) ? (result.Success ? "ok" : "run_failed") : result.Code,
            Message = result.Message,
            ApplyMode = "replace_all",
            AiResponse = result.AiResponse,
            StartedAt = startedAt.ToString("O"),
            FinishedAt = finishedAt.ToString("O"),
            DurationMs = Math.Max(0L, (long)(finishedAt - startedAt).TotalMilliseconds),
            PreviewUrl = PreviewUrl,
            ApiOverwriteUrl = ApiOverwriteUrl,
            McpUrl = McpUrl,
            State = _backendService.LoadState()
        };

        statusCode = result.Success
            ? 200
            : result.Code switch
            {
                "busy" => 409,
                "validation" => 400,
                "config" => 400,
                _ => 500
            };

        return responsePayload;
    }

    private void HandleStreamableMcp(HttpListenerContext context)
    {
        if (string.Equals(context.Request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
        {
            HandleMcpSseSession(context);
            return;
        }

        if (string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
        {
            HandleMcpJsonRpcPost(context);
            return;
        }

        WriteApiError(context.Response, 405, "method_not_allowed", "Use GET or POST /mcp.");
    }

    private void HandleMcpSseSession(HttpListenerContext context)
    {
        if (!IsSseRequest(context.Request))
        {
            WriteApiError(context.Response, 406, "not_acceptable", "GET /mcp requires `Accept: text/event-stream`.");
            return;
        }

        var sessionId = ResolveMcpSessionId(context.Request, allowGenerate: true);
        var response = context.Response;
        response.StatusCode = 200;
        response.ContentType = "text/event-stream";
        response.SendChunked = true;
        response.KeepAlive = true;
        response.Headers["Cache-Control"] = "no-cache";
        response.Headers["Connection"] = "keep-alive";
        response.Headers["Mcp-Session-Id"] = sessionId;

        var session = new McpSseSession(sessionId, response);
        ReplaceMcpSession(session);
        var endpointUrl = BuildMcpEndpointUrl(context.Request, sessionId);

        DutyDiagnosticsLogger.Info("MCP", "SSE session connected.",
            new
            {
                sessionId,
                endpoint = endpointUrl
            });

        _ = SendSseEventAsync(session, "endpoint", endpointUrl, CancellationToken.None);
        session.KeepAliveTask = Task.Run(() => RunMcpKeepAliveAsync(session));
    }

    private void HandleMcpJsonRpcPost(HttpListenerContext context)
    {
        string jsonBody;
        try
        {
            jsonBody = ReadRequestBody(context.Request);
        }
        catch (Exception ex)
        {
            WriteApiError(context.Response, 400, "invalid_body", $"Failed to read request body: {ex.Message}");
            return;
        }

        JsonDocument rpcDoc;
        try
        {
            rpcDoc = JsonDocument.Parse(jsonBody);
        }
        catch (Exception ex)
        {
            WriteApiError(context.Response, 400, "invalid_json", $"Failed to parse JSON body: {ex.Message}");
            return;
        }

        using (rpcDoc)
        {
            var sessionId = ResolveMcpSessionId(context.Request, allowGenerate: false);
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                sessionId = Guid.NewGuid().ToString("N");
            }

            context.Response.Headers["Mcp-Session-Id"] = sessionId;
            var responses = ProcessJsonRpcRootForHttp(rpcDoc.RootElement);
            if (responses.Count == 0)
            {
                WriteText(context.Response, string.Empty, "text/plain; charset=utf-8", 202);
                return;
            }

            var payload = responses.Count == 1 ? responses[0] : responses;
            WriteJson(context.Response, payload, 200);
        }
    }

    private bool ContainsInitializeRequest(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            return IsInitializeMethod(root);
        }

        if (root.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object && IsInitializeMethod(item))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsInitializeMethod(JsonElement requestElement)
    {
        if (!requestElement.TryGetProperty("method", out var methodElement) || methodElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        return string.Equals(methodElement.GetString(), "initialize", StringComparison.Ordinal);
    }

    private List<object> ProcessJsonRpcRootForHttp(JsonElement root)
    {
        var responses = new List<object>();
        if (root.ValueKind == JsonValueKind.Object)
        {
            var single = ProcessSingleJsonRpcForHttp(root);
            if (single != null)
            {
                responses.Add(single);
            }

            return responses;
        }

        if (root.ValueKind != JsonValueKind.Array)
        {
            responses.Add(BuildJsonRpcErrorResponse(null, -32600, "Invalid Request."));
            return responses;
        }

        var hasAny = false;
        foreach (var item in root.EnumerateArray())
        {
            hasAny = true;
            var response = ProcessSingleJsonRpcForHttp(item);
            if (response != null)
            {
                responses.Add(response);
            }
        }

        if (!hasAny)
        {
            responses.Add(BuildJsonRpcErrorResponse(null, -32600, "Invalid Request."));
        }

        return responses;
    }

    private object? ProcessSingleJsonRpcForHttp(JsonElement requestElement)
    {
        JsonElement? rpcId = null;
        if (requestElement.TryGetProperty("id", out var idElement))
        {
            rpcId = idElement.Clone();
        }

        var hasId = rpcId != null;
        if (requestElement.ValueKind != JsonValueKind.Object)
        {
            return hasId ? BuildJsonRpcErrorResponse(rpcId, -32600, "Invalid Request.") : null;
        }

        if (!requestElement.TryGetProperty("jsonrpc", out var jsonRpcElement) ||
            jsonRpcElement.ValueKind != JsonValueKind.String ||
            !string.Equals(jsonRpcElement.GetString(), "2.0", StringComparison.Ordinal))
        {
            return hasId
                ? BuildJsonRpcErrorResponse(rpcId, -32600, "Invalid Request: `jsonrpc` must be \"2.0\".")
                : null;
        }

        if (!requestElement.TryGetProperty("method", out var methodElement) ||
            methodElement.ValueKind != JsonValueKind.String)
        {
            return hasId ? BuildJsonRpcErrorResponse(rpcId, -32600, "Invalid Request: missing method.") : null;
        }

        var method = methodElement.GetString() ?? string.Empty;
        switch (method)
        {
            case "initialize":
                return hasId ? BuildJsonRpcResultResponse(rpcId, BuildInitializeResult()) : null;
            case "notifications/initialized":
                return null;
            case "tools/list":
                return hasId ? BuildJsonRpcResultResponse(rpcId, BuildToolsListResult()) : null;
            case "tools/call":
            {
                var callHandled = TryHandleToolsCall(requestElement, out var callResult, out var callErrorCode,
                    out var callErrorMessage, out var callErrorData);
                if (!hasId)
                {
                    return null;
                }

                if (!callHandled)
                {
                    return BuildJsonRpcErrorResponse(rpcId, callErrorCode, callErrorMessage, callErrorData);
                }

                return BuildJsonRpcResultResponse(rpcId, callResult);
            }
            case "ping":
                return hasId ? BuildJsonRpcResultResponse(rpcId, new { }) : null;
            default:
                return hasId ? BuildJsonRpcErrorResponse(rpcId, -32601, $"Method not found: {method}") : null;
        }
    }

    private object BuildJsonRpcResultResponse(JsonElement? rpcId, object? result)
    {
        return new
        {
            jsonrpc = "2.0",
            id = rpcId == null ? null : ConvertJsonValue(rpcId.Value),
            result = result ?? new { }
        };
    }

    private object BuildJsonRpcErrorResponse(JsonElement? rpcId, int code, string message, object? data = null)
    {
        return new
        {
            jsonrpc = "2.0",
            id = rpcId == null ? null : ConvertJsonValue(rpcId.Value),
            error = new
            {
                code,
                message,
                data
            }
        };
    }

    private async Task ProcessMcpJsonRpcAsync(string sessionId, string jsonBody)
    {
        if (!_mcpSessions.TryGetValue(sessionId, out var session))
        {
            DutyDiagnosticsLogger.Warn("MCP", "Session not found for JSON-RPC processing.",
                new { sessionId });
            return;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(jsonBody);
        }
        catch (Exception ex)
        {
            await SendJsonRpcErrorAsync(session, null, -32700, "Parse error.", new { message = ex.Message });
            return;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    await ProcessSingleJsonRpcAsync(session, item);
                }

                return;
            }

            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                await SendJsonRpcErrorAsync(session, null, -32600, "Invalid Request.");
                return;
            }

            await ProcessSingleJsonRpcAsync(session, doc.RootElement);
        }
    }

    private async Task ProcessSingleJsonRpcAsync(McpSseSession session, JsonElement requestElement)
    {
        JsonElement? rpcId = null;
        if (requestElement.TryGetProperty("id", out var idElement))
        {
            rpcId = idElement.Clone();
        }

        if (!requestElement.TryGetProperty("jsonrpc", out var jsonRpcElement) ||
            jsonRpcElement.ValueKind != JsonValueKind.String ||
            !string.Equals(jsonRpcElement.GetString(), "2.0", StringComparison.Ordinal))
        {
            await SendJsonRpcErrorIfNeededAsync(session, rpcId, -32600, "Invalid Request: `jsonrpc` must be \"2.0\".");
            return;
        }

        if (!requestElement.TryGetProperty("method", out var methodElement) ||
            methodElement.ValueKind != JsonValueKind.String)
        {
            await SendJsonRpcErrorIfNeededAsync(session, rpcId, -32600, "Invalid Request: missing method.");
            return;
        }

        var method = methodElement.GetString() ?? string.Empty;
        switch (method)
        {
            case "initialize":
                await SendJsonRpcResultIfNeededAsync(session, rpcId, BuildInitializeResult());
                return;
            case "notifications/initialized":
                return;
            case "tools/list":
                await SendJsonRpcResultIfNeededAsync(session, rpcId, BuildToolsListResult());
                return;
            case "tools/call":
            {
                var callHandled = TryHandleToolsCall(requestElement, out var callResult, out var callErrorCode,
                    out var callErrorMessage, out var callErrorData);
                if (!callHandled)
                {
                    await SendJsonRpcErrorIfNeededAsync(session, rpcId, callErrorCode, callErrorMessage, callErrorData);
                    return;
                }

                await SendJsonRpcResultIfNeededAsync(session, rpcId, callResult);
                return;
            }
            case "ping":
                await SendJsonRpcResultIfNeededAsync(session, rpcId, new { });
                return;
            default:
                await SendJsonRpcErrorIfNeededAsync(session, rpcId, -32601, $"Method not found: {method}");
                return;
        }
    }

    private bool TryHandleToolsCall(
        JsonElement requestElement,
        out object? result,
        out int errorCode,
        out string errorMessage,
        out object? errorData)
    {
        result = null;
        errorCode = -32602;
        errorMessage = "Invalid params.";
        errorData = null;

        if (!requestElement.TryGetProperty("params", out var paramsElement) ||
            paramsElement.ValueKind != JsonValueKind.Object)
        {
            errorMessage = "Invalid params: missing `params` object.";
            return false;
        }

        if (!paramsElement.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String)
        {
            errorMessage = "Invalid params: missing tool `name`.";
            return false;
        }

        var toolName = (nameElement.GetString() ?? string.Empty).Trim();
        JsonElement argumentsElement = default;
        if (paramsElement.TryGetProperty("arguments", out var rawArguments))
        {
            if (rawArguments.ValueKind is not JsonValueKind.Object and not JsonValueKind.Null)
            {
                errorMessage = "Invalid params: `arguments` must be an object.";
                return false;
            }

            if (rawArguments.ValueKind == JsonValueKind.Object)
            {
                argumentsElement = rawArguments;
            }
        }

        return toolName switch
        {
            McpToolScheduleOverwrite => TryHandleScheduleOverwriteTool(argumentsElement, out result, out errorCode,
                out errorMessage, out errorData),
            McpToolRosterImportStudents => TryHandleRosterImportStudentsTool(argumentsElement, out result, out errorCode,
                out errorMessage, out errorData),
            McpToolConfigUpdateSettings => TryHandleConfigUpdateSettingsTool(argumentsElement, out result, out errorCode,
                out errorMessage, out errorData),
            _ => BuildToolNotFound(toolName, out errorCode, out errorMessage)
        };
    }

    private bool BuildToolNotFound(string toolName, out int errorCode, out string errorMessage)
    {
        errorCode = -32602;
        errorMessage = $"Tool not found: {toolName}";
        return false;
    }

    private bool TryHandleScheduleOverwriteTool(
        JsonElement argumentsElement,
        out object? result,
        out int errorCode,
        out string errorMessage,
        out object? errorData)
    {
        result = null;
        errorCode = -32602;
        errorMessage = "Invalid params.";
        errorData = null;

        if (!TryGetToolArgument(argumentsElement, "instruction", out var instructionElement) ||
            instructionElement.ValueKind != JsonValueKind.String)
        {
            errorMessage = "Invalid params: `instruction` is required.";
            return false;
        }

        var instruction = (instructionElement.GetString() ?? string.Empty).Trim();
        if (instruction.Length == 0)
        {
            errorMessage = "Invalid params: `instruction` is required.";
            return false;
        }

        OverwriteScheduleConfig? config = null;
        if (TryGetToolArgument(argumentsElement, "config", out var configElement) &&
            configElement.ValueKind != JsonValueKind.Null)
        {
            if (configElement.ValueKind != JsonValueKind.Object)
            {
                errorMessage = "Invalid params: `config` must be an object when provided.";
                return false;
            }

            try
            {
                config = JsonSerializer.Deserialize<OverwriteScheduleConfig>(configElement.GetRawText(), JsonOptions);
            }
            catch (Exception ex)
            {
                errorMessage = "Invalid params: `config` cannot be parsed.";
                errorData = new { message = ex.Message };
                return false;
            }
        }

        var overwriteResult = ExecuteOverwriteSchedule(instruction, config, out var statusCode);
        result = new
        {
            content = new[]
            {
                new
                {
                    type = "text",
                    text = overwriteResult.Success
                        ? $"Schedule overwrite succeeded. {overwriteResult.Message}"
                        : $"Schedule overwrite failed. {overwriteResult.Message}"
                }
            },
            isError = !overwriteResult.Success,
            structuredContent = new
            {
                success = overwriteResult.Success,
                code = overwriteResult.Code,
                message = overwriteResult.Message,
                apply_mode = overwriteResult.ApplyMode,
                ai_response = overwriteResult.AiResponse,
                status_code = statusCode,
                state = overwriteResult.State,
                preview_url = overwriteResult.PreviewUrl,
                api_overwrite_url = overwriteResult.ApiOverwriteUrl,
                mcp_url = overwriteResult.McpUrl
            }
        };
        return true;
    }

    private bool TryHandleRosterImportStudentsTool(
        JsonElement argumentsElement,
        out object? result,
        out int errorCode,
        out string errorMessage,
        out object? errorData)
    {
        result = null;
        errorCode = -32602;
        errorMessage = "Invalid params.";
        errorData = null;

        if (!TryReadStringListArgument(argumentsElement, "students", required: true, out var students, out var studentError))
        {
            errorMessage = studentError;
            return false;
        }

        if (!TryReadOptionalBooleanArgument(argumentsElement, "active", out var activeValue, out var activeError))
        {
            errorMessage = activeError ?? "Invalid params: `active` must be a boolean.";
            return false;
        }

        if (!TryReadOptionalBooleanArgument(argumentsElement, "replace_existing", out var replaceExistingValue,
                out var replaceError))
        {
            errorMessage = replaceError ?? "Invalid params: `replace_existing` must be a boolean.";
            return false;
        }

        var active = activeValue ?? true;
        var replaceExisting = replaceExistingValue ?? false;
        var roster = replaceExisting ? new List<RosterEntry>() : _backendService.LoadRosterEntries();
        var existingNames = new HashSet<string>(
            roster.Select(x => (x.Name ?? string.Empty).Trim()).Where(x => x.Length > 0),
            StringComparer.OrdinalIgnoreCase);

        var importedNames = new List<string>();
        var skippedNames = new List<string>();
        foreach (var student in students!)
        {
            if (!existingNames.Add(student))
            {
                skippedNames.Add(student);
                continue;
            }

            roster.Add(new RosterEntry
            {
                Name = student,
                Active = active
            });
            importedNames.Add(student);
        }

        _backendService.SaveRosterEntries(roster);
        var updatedRoster = _backendService.LoadRosterEntries();
        result = new
        {
            content = new[]
            {
                new
                {
                    type = "text",
                    text = $"Imported {importedNames.Count} students; skipped {skippedNames.Count} duplicates."
                }
            },
            isError = false,
            structuredContent = new
            {
                imported_count = importedNames.Count,
                skipped_count = skippedNames.Count,
                imported_names = importedNames,
                skipped_names = skippedNames,
                replace_existing = replaceExisting,
                total_count = updatedRoster.Count,
                roster = updatedRoster.Select(x => new
                {
                    id = x.Id,
                    name = x.Name,
                    active = x.Active
                }).ToList()
            }
        };
        return true;
    }

    private bool TryHandleConfigUpdateSettingsTool(
        JsonElement argumentsElement,
        out object? result,
        out int errorCode,
        out string errorMessage,
        out object? errorData)
    {
        result = null;
        errorCode = -32602;
        errorMessage = "Invalid params.";
        errorData = null;

        var supportedFields = new[]
        {
            "api_key",
            "base_url",
            "model",
            "python_path",
            "enable_auto_run",
            "auto_run_day",
            "auto_run_time",
            "auto_run_coverage_days",
            "per_day",
            "skip_weekends",
            "duty_rule",
            "start_from_today",
            "component_refresh_time",
            "area_names",
            "area_per_day_counts",
            "notification_templates",
            "duty_reminder_enabled",
            "duty_reminder_times",
            "duty_reminder_templates"
        };
        if (!HasAnyToolArgument(argumentsElement, supportedFields))
        {
            errorMessage = "Invalid params: no supported settings provided.";
            return false;
        }

        if (!TryReadOptionalStringArgument(argumentsElement, "api_key", out var apiKey, out var parseError))
        {
            errorMessage = parseError ?? "Invalid params.";
            return false;
        }
        if (!TryReadOptionalStringArgument(argumentsElement, "base_url", out var baseUrl, out parseError))
        {
            errorMessage = parseError ?? "Invalid params.";
            return false;
        }
        if (!TryReadOptionalStringArgument(argumentsElement, "model", out var model, out parseError))
        {
            errorMessage = parseError ?? "Invalid params.";
            return false;
        }
        if (!TryReadOptionalStringArgument(argumentsElement, "python_path", out var pythonPath, out parseError))
        {
            errorMessage = parseError ?? "Invalid params.";
            return false;
        }
        if (!TryReadOptionalBooleanArgument(argumentsElement, "enable_auto_run", out var enableAutoRun, out parseError))
        {
            errorMessage = parseError ?? "Invalid params.";
            return false;
        }
        if (!TryReadOptionalStringArgument(argumentsElement, "auto_run_day", out var autoRunDay, out parseError))
        {
            errorMessage = parseError ?? "Invalid params.";
            return false;
        }
        if (!TryReadOptionalStringArgument(argumentsElement, "auto_run_time", out var autoRunTime, out parseError))
        {
            errorMessage = parseError ?? "Invalid params.";
            return false;
        }
        if (!TryReadOptionalIntArgument(argumentsElement, "auto_run_coverage_days", out var autoRunCoverageDays,
                out parseError))
        {
            errorMessage = parseError ?? "Invalid params.";
            return false;
        }
        if (!TryReadOptionalIntArgument(argumentsElement, "per_day", out var perDay, out parseError))
        {
            errorMessage = parseError ?? "Invalid params.";
            return false;
        }
        if (!TryReadOptionalBooleanArgument(argumentsElement, "skip_weekends", out var skipWeekends, out parseError))
        {
            errorMessage = parseError ?? "Invalid params.";
            return false;
        }
        if (!TryReadOptionalStringArgument(argumentsElement, "duty_rule", out var dutyRule, out parseError))
        {
            errorMessage = parseError ?? "Invalid params.";
            return false;
        }
        if (!TryReadOptionalBooleanArgument(argumentsElement, "start_from_today", out var startFromToday, out parseError))
        {
            errorMessage = parseError ?? "Invalid params.";
            return false;
        }
        if (!TryReadOptionalStringArgument(argumentsElement, "component_refresh_time", out var componentRefreshTime,
                out parseError))
        {
            errorMessage = parseError ?? "Invalid params.";
            return false;
        }
        if (!TryReadStringListArgument(argumentsElement, "area_names", required: false, out var areaNames,
                out var listParseError))
        {
            errorMessage = listParseError;
            return false;
        }
        if (!TryReadStringIntMapArgument(argumentsElement, "area_per_day_counts", out var areaPerDayCounts,
                out listParseError))
        {
            errorMessage = listParseError;
            return false;
        }
        if (!TryReadStringListArgument(argumentsElement, "notification_templates", required: false,
                out var notificationTemplates, out listParseError))
        {
            errorMessage = listParseError;
            return false;
        }
        if (!TryReadOptionalBooleanArgument(argumentsElement, "duty_reminder_enabled", out var dutyReminderEnabled,
                out parseError))
        {
            errorMessage = parseError ?? "Invalid params.";
            return false;
        }
        if (!TryReadStringListArgument(argumentsElement, "duty_reminder_times", required: false, out var dutyReminderTimes,
                out listParseError))
        {
            errorMessage = listParseError;
            return false;
        }
        if (!TryReadStringListArgument(argumentsElement, "duty_reminder_templates", required: false,
                out var dutyReminderTemplates, out listParseError))
        {
            errorMessage = listParseError;
            return false;
        }

        _backendService.LoadConfig();
        var current = _backendService.Config;
        try
        {
            _backendService.SaveUserConfig(
                apiKey: DutyBackendService.ResolveApiKeyInput(apiKey, current.DecryptedApiKey),
                baseUrl: baseUrl ?? current.BaseUrl,
                model: model ?? current.Model,
                enableAutoRun: enableAutoRun ?? current.EnableAutoRun,
                autoRunDay: autoRunDay ?? current.AutoRunDay,
                autoRunTime: autoRunTime ?? current.AutoRunTime,
                perDay: perDay ?? current.PerDay,
                skipWeekends: skipWeekends ?? current.SkipWeekends,
                dutyRule: dutyRule ?? current.DutyRule,
                startFromToday: startFromToday ?? current.StartFromToday,
                autoRunCoverageDays: autoRunCoverageDays ?? current.AutoRunCoverageDays,
                componentRefreshTime: componentRefreshTime ?? current.ComponentRefreshTime,
                pythonPath: pythonPath ?? current.PythonPath,
                areaNames: areaNames ?? current.AreaNames,
                areaPerDayCounts: areaPerDayCounts ?? current.AreaPerDayCounts,
                notificationTemplates: notificationTemplates ?? current.NotificationTemplates,
                dutyReminderEnabled: dutyReminderEnabled ?? current.DutyReminderEnabled,
                dutyReminderTimes: dutyReminderTimes ?? current.DutyReminderTimes,
                dutyReminderTemplates: dutyReminderTemplates ?? current.DutyReminderTemplates);
        }
        catch (Exception ex)
        {
            errorMessage = $"Invalid params: {ex.Message}";
            return false;
        }

        _backendService.LoadConfig();
        var saved = _backendService.Config;
        result = new
        {
            content = new[]
            {
                new
                {
                    type = "text",
                    text = "Duty settings updated."
                }
            },
            isError = false,
            structuredContent = new
            {
                enable_auto_run = saved.EnableAutoRun,
                auto_run_day = saved.AutoRunDay,
                auto_run_time = saved.AutoRunTime,
                auto_run_coverage_days = saved.AutoRunCoverageDays,
                per_day = saved.PerDay,
                skip_weekends = saved.SkipWeekends,
                duty_rule = saved.DutyRule,
                start_from_today = saved.StartFromToday,
                component_refresh_time = saved.ComponentRefreshTime,
                area_names = _backendService.GetAreaNames(),
                area_per_day_counts = _backendService.GetAreaPerDayCounts(),
                notification_templates = _backendService.GetNotificationTemplates(),
                duty_reminder_enabled = saved.DutyReminderEnabled,
                duty_reminder_times = _backendService.GetDutyReminderTimes(),
                duty_reminder_templates = _backendService.GetDutyReminderTemplates()
            }
        };
        return true;
    }

    private static bool TryReadOptionalStringArgument(
        JsonElement argumentsElement,
        string propertyName,
        out string? value,
        out string? error)
    {
        value = null;
        error = null;
        if (!TryGetToolArgument(argumentsElement, propertyName, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            error = $"Invalid params: `{propertyName}` must be a string.";
            return false;
        }

        value = (element.GetString() ?? string.Empty).Trim();
        return true;
    }

    private static bool TryReadOptionalBooleanArgument(
        JsonElement argumentsElement,
        string propertyName,
        out bool? value,
        out string? error)
    {
        value = null;
        error = null;
        if (!TryGetToolArgument(argumentsElement, propertyName, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (!TryParseBooleanLike(element, out var parsed))
        {
            error = $"Invalid params: `{propertyName}` must be a boolean.";
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryReadOptionalIntArgument(
        JsonElement argumentsElement,
        string propertyName,
        out int? value,
        out string? error)
    {
        value = null;
        error = null;
        if (!TryGetToolArgument(argumentsElement, propertyName, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (!TryParseIntLike(element, out var parsed))
        {
            error = $"Invalid params: `{propertyName}` must be an integer.";
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryReadStringListArgument(
        JsonElement argumentsElement,
        string propertyName,
        bool required,
        out List<string>? values,
        out string error)
    {
        values = null;
        error = string.Empty;
        if (!TryGetToolArgument(argumentsElement, propertyName, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            if (required)
            {
                error = $"Invalid params: `{propertyName}` is required.";
                return false;
            }

            return true;
        }

        var parsed = new List<string>();
        if (element.ValueKind == JsonValueKind.String)
        {
            parsed.AddRange(SplitAndDeduplicateText(element.GetString() ?? string.Empty));
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    error = $"Invalid params: `{propertyName}` array must only contain strings.";
                    return false;
                }

                parsed.AddRange(SplitAndDeduplicateText(item.GetString() ?? string.Empty));
            }
        }
        else
        {
            error = $"Invalid params: `{propertyName}` must be a string or string array.";
            return false;
        }

        values = DeduplicateTextItems(parsed);
        if (required && values.Count == 0)
        {
            error = $"Invalid params: `{propertyName}` cannot be empty.";
            return false;
        }

        return true;
    }

    private static bool TryReadStringIntMapArgument(
        JsonElement argumentsElement,
        string propertyName,
        out Dictionary<string, int>? values,
        out string error)
    {
        values = null;
        error = string.Empty;
        if (!TryGetToolArgument(argumentsElement, propertyName, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            error = $"Invalid params: `{propertyName}` must be an object.";
            return false;
        }

        var parsed = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            var areaName = (property.Name ?? string.Empty).Trim();
            if (areaName.Length == 0)
            {
                continue;
            }

            if (!TryParseIntLike(property.Value, out var count))
            {
                error = $"Invalid params: `{propertyName}.{areaName}` must be an integer.";
                return false;
            }

            parsed[areaName] = Math.Clamp(count, 1, 30);
        }

        values = parsed;
        return true;
    }

    private static bool TryGetToolArgument(JsonElement argumentsElement, string propertyName, out JsonElement value)
    {
        value = default;
        return argumentsElement.ValueKind == JsonValueKind.Object &&
               argumentsElement.TryGetProperty(propertyName, out value);
    }

    private static bool HasAnyToolArgument(JsonElement argumentsElement, IEnumerable<string> names)
    {
        if (argumentsElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var name in names)
        {
            if (argumentsElement.TryGetProperty(name, out _))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParseBooleanLike(JsonElement element, out bool value)
    {
        value = false;
        switch (element.ValueKind)
        {
            case JsonValueKind.True:
                value = true;
                return true;
            case JsonValueKind.False:
                value = false;
                return true;
            case JsonValueKind.Number:
                if (element.TryGetInt64(out var intValue))
                {
                    value = intValue != 0;
                    return true;
                }

                return false;
            case JsonValueKind.String:
            {
                var text = (element.GetString() ?? string.Empty).Trim();
                if (bool.TryParse(text, out var boolValue))
                {
                    value = boolValue;
                    return true;
                }

                if (long.TryParse(text, out var numeric))
                {
                    value = numeric != 0;
                    return true;
                }

                if (text.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                    text.Equals("on", StringComparison.OrdinalIgnoreCase))
                {
                    value = true;
                    return true;
                }

                if (text.Equals("no", StringComparison.OrdinalIgnoreCase) ||
                    text.Equals("off", StringComparison.OrdinalIgnoreCase))
                {
                    value = false;
                    return true;
                }

                return false;
            }
            default:
                return false;
        }
    }

    private static bool TryParseIntLike(JsonElement element, out int value)
    {
        value = 0;
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                return element.TryGetInt32(out value);
            case JsonValueKind.String:
                return int.TryParse((element.GetString() ?? string.Empty).Trim(), out value);
            default:
                return false;
        }
    }

    private static List<string> SplitAndDeduplicateText(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var tokens = raw
            .Split([',', ';', '，', '；', '、', '\r', '\n', '\t', '|'], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .ToList();
        return DeduplicateTextItems(tokens);
    }

    private static List<string> DeduplicateTextItems(IEnumerable<string> values)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            var text = (value ?? string.Empty).Trim();
            if (text.Length == 0 || !seen.Add(text))
            {
                continue;
            }

            result.Add(text);
        }

        return result;
    }

    private object BuildInitializeResult()
    {
        return new
        {
            protocolVersion = McpProtocolVersion,
            capabilities = new
            {
                tools = new { }
            },
            serverInfo = new
            {
                name = "Duty-Agent",
                version = "1.0.0"
            }
        };
    }

    private object BuildToolsListResult()
    {
        return new
        {
            tools = new object[]
            {
                new
                {
                    name = McpToolScheduleOverwrite,
                    description = "Overwrite the current duty schedule from a natural-language instruction.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            instruction = new
                            {
                                type = "string",
                                description = "Scheduling instruction, e.g. generate 7 days from today with 2 students per area per day."
                            },
                            config = new
                            {
                                type = "object",
                                description = "Optional config overrides. Fields are the same as the REST overwrite API config object."
                            }
                        },
                        required = new[] { "instruction" }
                    }
                },
                new
                {
                    name = McpToolRosterImportStudents,
                    description = "Bulk import students from names and auto-assign normalized IDs.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            students = new
                            {
                                oneOf = new object[]
                                {
                                    new { type = "string" },
                                    new
                                    {
                                        type = "array",
                                        items = new { type = "string" }
                                    }
                                },
                                description = "Student names. Supports comma/newline/semicolon separated string, or a string array."
                            },
                            active = new
                            {
                                type = "boolean",
                                description = "Whether imported students are active. Defaults to true."
                            },
                            replace_existing = new
                            {
                                type = "boolean",
                                description = "If true, replace existing roster before importing."
                            }
                        },
                        required = new[] { "students" }
                    }
                },
                new
                {
                    name = McpToolConfigUpdateSettings,
                    description = "Update duty settings including auto run, refresh time, area names/counts, and reminder rules.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            enable_auto_run = new { type = "boolean" },
                            auto_run_day = new { type = "string" },
                            auto_run_time = new { type = "string" },
                            auto_run_coverage_days = new { type = "integer" },
                            per_day = new { type = "integer" },
                            component_refresh_time = new { type = "string" },
                            area_names = new
                            {
                                oneOf = new object[]
                                {
                                    new { type = "string" },
                                    new
                                    {
                                        type = "array",
                                        items = new { type = "string" }
                                    }
                                }
                            },
                            area_per_day_counts = new
                            {
                                type = "object",
                                additionalProperties = new { type = "integer" }
                            },
                            duty_reminder_enabled = new { type = "boolean" },
                            duty_reminder_times = new
                            {
                                oneOf = new object[]
                                {
                                    new { type = "string" },
                                    new
                                    {
                                        type = "array",
                                        items = new { type = "string" }
                                    }
                                }
                            },
                            duty_reminder_templates = new
                            {
                                oneOf = new object[]
                                {
                                    new { type = "string" },
                                    new
                                    {
                                        type = "array",
                                        items = new { type = "string" }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };
    }


    private async Task RunMcpKeepAliveAsync(McpSseSession session)
    {
        while (!session.CancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(McpKeepAliveInterval, session.CancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var ok = await SendSseCommentAsync(session, "keepalive", session.CancellationToken);
            if (!ok)
            {
                break;
            }
        }
    }

    private async Task<bool> SendSseCommentAsync(McpSseSession session, string comment, CancellationToken cancellationToken)
    {
        var frame = $": {(comment ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ')}\n\n";
        return await SendSseFrameAsync(session, frame, cancellationToken);
    }

    private async Task<bool> SendSseEventAsync(
        McpSseSession session,
        string eventName,
        string data,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(eventName))
        {
            builder.Append("event: ").Append(eventName.Trim()).Append('\n');
        }

        var normalizedData = (data ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        foreach (var line in normalizedData.Split('\n'))
        {
            builder.Append("data: ").Append(line).Append('\n');
        }

        builder.Append('\n');
        return await SendSseFrameAsync(session, builder.ToString(), cancellationToken);
    }

    private async Task<bool> SendSseFrameAsync(McpSseSession session, string frame, CancellationToken cancellationToken)
    {
        try
        {
            await session.WriteLock.WaitAsync(cancellationToken);
            try
            {
                var bytes = Encoding.UTF8.GetBytes(frame);
                await session.Response.OutputStream.WriteAsync(bytes, cancellationToken);
                await session.Response.OutputStream.FlushAsync(cancellationToken);
                return true;
            }
            finally
            {
                session.WriteLock.Release();
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or HttpListenerException or IOException or OperationCanceledException)
        {
            RemoveMcpSession(session.SessionId, "send_failed", ex.Message);
            return false;
        }
    }

    private async Task SendJsonRpcResultIfNeededAsync(McpSseSession session, JsonElement? rpcId, object? result)
    {
        if (rpcId == null)
        {
            return;
        }

        var payload = new
        {
            jsonrpc = "2.0",
            id = ConvertJsonValue(rpcId.Value),
            result = result ?? new { }
        };
        await SendSseEventAsync(session, "message", JsonSerializer.Serialize(payload, JsonOptions), CancellationToken.None);
    }

    private async Task SendJsonRpcErrorIfNeededAsync(
        McpSseSession session,
        JsonElement? rpcId,
        int code,
        string message,
        object? data = null)
    {
        if (rpcId == null)
        {
            return;
        }

        await SendJsonRpcErrorAsync(session, rpcId, code, message, data);
    }

    private async Task SendJsonRpcErrorAsync(
        McpSseSession session,
        JsonElement? rpcId,
        int code,
        string message,
        object? data = null)
    {
        var payload = new
        {
            jsonrpc = "2.0",
            id = rpcId == null ? null : ConvertJsonValue(rpcId.Value),
            error = new
            {
                code,
                message,
                data
            }
        };
        await SendSseEventAsync(session, "message", JsonSerializer.Serialize(payload, JsonOptions), CancellationToken.None);
    }

    private static object? ConvertJsonValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var int64) => int64,
            JsonValueKind.Number when element.TryGetDecimal(out var decimalValue) => decimalValue,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.GetRawText()
        };
    }

    private static bool IsSseRequest(HttpListenerRequest request)
    {
        var accept = request.Headers["Accept"] ?? string.Empty;
        if (accept.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (request.AcceptTypes == null)
        {
            return false;
        }

        return request.AcceptTypes.Any(x => x?.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) == true);
    }

    private static string ResolveMcpSessionId(HttpListenerRequest request, bool allowGenerate)
    {
        var sessionId = request.Headers["Mcp-Session-Id"] ?? request.Headers["X-Session-Id"];
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            return sessionId.Trim();
        }

        sessionId = GetQueryValueIgnoreCase(request, "sessionId", "session_id", "mcpSessionId", "mcp_session_id");
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            return sessionId.Trim();
        }

        return allowGenerate ? Guid.NewGuid().ToString("N") : string.Empty;
    }

    private string BuildMcpEndpointUrl(HttpListenerRequest request, string? sessionId = null)
    {
        string endpoint;
        if (!string.IsNullOrWhiteSpace(McpUrl))
        {
            endpoint = McpUrl;
        }
        else
        {
            var authority = request.Url?.GetLeftPart(UriPartial.Authority) ?? "http://127.0.0.1";
            endpoint = $"{authority.TrimEnd('/')}/mcp";
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return endpoint;
        }

        var separator = endpoint.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{endpoint}{separator}sessionId={Uri.EscapeDataString(sessionId)}";
    }

    private static string? GetQueryValueIgnoreCase(HttpListenerRequest request, params string[] keys)
    {
        var query = request.QueryString;
        if (query == null || query.Count == 0 || keys.Length == 0)
        {
            return null;
        }

        foreach (var key in keys)
        {
            var direct = query[key];
            if (!string.IsNullOrWhiteSpace(direct))
            {
                return direct;
            }
        }

        foreach (var existingKey in query.AllKeys)
        {
            if (string.IsNullOrWhiteSpace(existingKey))
            {
                continue;
            }

            if (!keys.Any(k => string.Equals(k, existingKey, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var value = query[existingKey];
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private void ReplaceMcpSession(McpSseSession session)
    {
        if (_mcpSessions.TryGetValue(session.SessionId, out var old))
        {
            RemoveMcpSession(old.SessionId, "replaced");
        }

        _mcpSessions[session.SessionId] = session;
    }

    private void CloseAllMcpSessions()
    {
        foreach (var pair in _mcpSessions)
        {
            RemoveMcpSession(pair.Key, "shutdown");
        }
    }

    private void RemoveMcpSession(string sessionId, string reason, string? details = null)
    {
        if (!_mcpSessions.TryRemove(sessionId, out var session))
        {
            return;
        }

        DutyDiagnosticsLogger.Info("MCP", "SSE session closed.",
            new
            {
                sessionId,
                reason,
                details = details ?? string.Empty
            });
        session.Dispose();
    }

    private void ApplyOverwriteConfig(OverwriteScheduleConfig config)
    {
        _backendService.LoadConfig();
        var current = _backendService.Config;

        var apiKey = DutyBackendService.ResolveApiKeyInput(config.ApiKey, current.DecryptedApiKey);
        var baseUrl = config.BaseUrl ?? current.BaseUrl;
        var model = config.Model ?? current.Model;
        var perDay = config.PerDay ?? current.PerDay;
        var skipWeekends = config.SkipWeekends ?? current.SkipWeekends;
        var dutyRule = config.DutyRule ?? current.DutyRule;
        var startFromToday = config.StartFromToday ?? current.StartFromToday;
        var autoRunCoverageDays = config.AutoRunCoverageDays ?? current.AutoRunCoverageDays;
        var pythonPath = config.PythonPath ?? current.PythonPath;
        var areaNames = config.AreaNames ?? current.AreaNames;
        var areaPerDayCounts = config.AreaPerDayCounts ?? current.AreaPerDayCounts;

        _backendService.SaveUserConfig(
            apiKey: apiKey,
            baseUrl: baseUrl,
            model: model,
            enableAutoRun: current.EnableAutoRun,
            autoRunDay: current.AutoRunDay,
            autoRunTime: current.AutoRunTime,
            perDay: perDay,
            skipWeekends: skipWeekends,
            dutyRule: dutyRule,
            startFromToday: startFromToday,
            autoRunCoverageDays: autoRunCoverageDays,
            componentRefreshTime: current.ComponentRefreshTime,
            pythonPath: pythonPath,
            areaNames: areaNames,
            areaPerDayCounts: areaPerDayCounts,
            notificationTemplates: current.NotificationTemplates,
            dutyReminderEnabled: current.DutyReminderEnabled,
            dutyReminderTimes: current.DutyReminderTimes,
            dutyReminderTemplates: current.DutyReminderTemplates);
    }

    private static string ReadRequestBody(HttpListenerRequest request)
    {
        var encoding = request.ContentEncoding ?? Encoding.UTF8;
        using var reader = new StreamReader(request.InputStream, encoding, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static void WriteApiError(HttpListenerResponse response, int statusCode, string code, string message)
    {
        WriteJson(response, new
        {
            success = false,
            code,
            message
        }, statusCode);
    }

    private static void SetCorsHeaders(HttpListenerResponse response)
    {
        response.AddHeader("Access-Control-Allow-Origin", "*");
        response.AddHeader("Access-Control-Allow-Headers", "Content-Type, X-Duty-Token, Mcp-Session-Id, X-Session-Id, Accept");
        response.AddHeader("Access-Control-Allow-Methods", "POST, GET, OPTIONS");
        response.AddHeader("Access-Control-Expose-Headers", "Mcp-Session-Id");
    }

    private static void WriteFile(HttpListenerResponse response, string filePath, string contentType)
    {
        if (!File.Exists(filePath))
        {
            WriteText(response, "Not Found", "text/plain; charset=utf-8", 404);
            return;
        }

        var bytes = File.ReadAllBytes(filePath);
        response.StatusCode = 200;
        response.ContentType = contentType;
        response.ContentLength64 = bytes.LongLength;
        response.OutputStream.Write(bytes, 0, bytes.Length);
        TryCloseResponse(response);
    }

    private static void WriteText(HttpListenerResponse response, string text, string contentType, int statusCode)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        response.StatusCode = statusCode;
        response.ContentType = contentType;
        response.ContentLength64 = bytes.LongLength;
        response.OutputStream.Write(bytes, 0, bytes.Length);
        TryCloseResponse(response);
    }

    private static void WriteJson(HttpListenerResponse response, object payload, int statusCode)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        WriteText(response, json, "application/json; charset=utf-8", statusCode);
    }

    private static void TryCloseResponse(HttpListenerResponse response)
    {
        try
        {
            response.OutputStream.Close();
            response.Close();
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        try
        {
            StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch
        {
        }
    }

    private sealed class OverwriteScheduleRequest
    {
        [JsonPropertyName("instruction")]
        public string? Instruction { get; set; }

        [JsonPropertyName("config")]
        public OverwriteScheduleConfig? Config { get; set; }
    }

    private sealed class OverwriteScheduleConfig
    {
        [JsonPropertyName("api_key")]
        public string? ApiKey { get; set; }

        [JsonPropertyName("base_url")]
        public string? BaseUrl { get; set; }

        [JsonPropertyName("model")]
        public string? Model { get; set; }

        [JsonPropertyName("auto_run_coverage_days")]
        public int? AutoRunCoverageDays { get; set; }

        [JsonPropertyName("per_day")]
        public int? PerDay { get; set; }

        [JsonPropertyName("skip_weekends")]
        public bool? SkipWeekends { get; set; }

        [JsonPropertyName("duty_rule")]
        public string? DutyRule { get; set; }

        [JsonPropertyName("start_from_today")]
        public bool? StartFromToday { get; set; }

        [JsonPropertyName("python_path")]
        public string? PythonPath { get; set; }

        [JsonPropertyName("area_names")]
        public List<string>? AreaNames { get; set; }

        [JsonPropertyName("area_per_day_counts")]
        public Dictionary<string, int>? AreaPerDayCounts { get; set; }
    }

    private sealed class OverwriteScheduleResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("code")]
        public string Code { get; set; } = string.Empty;

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("apply_mode")]
        public string ApplyMode { get; set; } = "replace_all";

        [JsonPropertyName("ai_response")]
        public string AiResponse { get; set; } = string.Empty;

        [JsonPropertyName("started_at")]
        public string StartedAt { get; set; } = string.Empty;

        [JsonPropertyName("finished_at")]
        public string FinishedAt { get; set; } = string.Empty;

        [JsonPropertyName("duration_ms")]
        public long DurationMs { get; set; }

        [JsonPropertyName("preview_url")]
        public string PreviewUrl { get; set; } = string.Empty;

        [JsonPropertyName("api_overwrite_url")]
        public string ApiOverwriteUrl { get; set; } = string.Empty;

        [JsonPropertyName("mcp_url")]
        public string McpUrl { get; set; } = string.Empty;

        [JsonPropertyName("state")]
        public DutyState State { get; set; } = new();
    }

    private sealed class McpSseSession : IDisposable
    {
        public string SessionId { get; }
        public HttpListenerResponse Response { get; }
        public SemaphoreSlim WriteLock { get; } = new(1, 1);
        public CancellationTokenSource CancellationTokenSource { get; } = new();
        public CancellationToken CancellationToken => CancellationTokenSource.Token;
        public Task? KeepAliveTask { get; set; }

        public McpSseSession(string sessionId, HttpListenerResponse response)
        {
            SessionId = sessionId;
            Response = response;
        }

        public void Dispose()
        {
            try
            {
                if (!CancellationTokenSource.IsCancellationRequested)
                {
                    CancellationTokenSource.Cancel();
                }
            }
            catch
            {
            }

            try
            {
                Response.OutputStream.Close();
                Response.Close();
            }
            catch
            {
            }

            WriteLock.Dispose();
            CancellationTokenSource.Dispose();
        }
    }
}
