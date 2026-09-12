using System.Net.Http.Headers;
using System.Text.Json;

namespace DutyAgent.Client;

internal sealed class NotificationStreamClient : IDisposable
{
    private readonly BackendProcessManager _backend;
    private readonly DesktopNotificationService _notificationService;
    private readonly HttpClient _httpClient = new()
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };
    private CancellationTokenSource? _cts;
    private Task? _workerTask;

    // 后端每 15s 发一次 SSE ping；静默超过 45s（≈3 个 ping 周期未到）
    // 即判定连接已失效，主动断开重连。
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(45);

    public NotificationStreamClient(BackendProcessManager backend, DesktopNotificationService notificationService)
    {
        _backend = backend;
        _notificationService = notificationService;
    }

    public void Start()
    {
        if (_workerTask is { IsCompleted: false })
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _workerTask = Task.Run(() => RunAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        try
        {
            if (_workerTask is not null)
            {
                await _workerTask.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
        finally
        {
            cts.Dispose();
            _cts = null;
            _workerTask = null;
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var reconnectDelay = TimeSpan.FromSeconds(2);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ListenOnceAsync(cancellationToken).ConfigureAwait(false);
                reconnectDelay = TimeSpan.FromSeconds(2);
                ClientLog.Info("通知流连接已结束（服务端关闭）。");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                ClientLog.Warn($"通知流连接中断，{(int)reconnectDelay.TotalSeconds}s 后重连：{ex.Message}");
                try
                {
                    await Task.Delay(reconnectDelay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                reconnectDelay = TimeSpan.FromSeconds(Math.Min(30, reconnectDelay.TotalSeconds * 1.5));
            }
        }
    }

    private async Task ListenOnceAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_backend.BaseUrl}/api/v1/notifications/stream");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.TryAddWithoutValidation("X-Duty-Request-Source", "client");
        if (!string.IsNullOrWhiteSpace(_backend.Token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _backend.Token);
        }

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        ClientLog.Info("通知流已连接。");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        string? eventName = null;
        var dataLines = new List<string>();

        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            using (var idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                idleCts.CancelAfter(IdleTimeout);
                try
                {
                    line = await reader.ReadLineAsync(idleCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"通知流超过 {IdleTimeout.TotalSeconds:0}s 未收到任何数据（后端 ping 间隔 15s），判定连接已静默失效。");
                }
            }

            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                DispatchEvent(eventName, dataLines);
                eventName = null;
                dataLines.Clear();
                continue;
            }

            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                eventName = line["event:".Length..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                dataLines.Add(line["data:".Length..].TrimStart());
            }
        }
    }

    private void DispatchEvent(string? eventName, List<string> dataLines)
    {
        if (!string.Equals(eventName, "notification", StringComparison.OrdinalIgnoreCase) || dataLines.Count == 0)
        {
            return;
        }

        try
        {
            var json = string.Join("\n", dataLines);
            var notification = JsonSerializer.Deserialize<NotificationEvent>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (notification is not null)
            {
                if (!notification.Targets.Any(target => string.Equals(target, "system", StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }

                _notificationService.Show(notification);
            }
        }
        catch (Exception ex)
        {
            ClientLog.Warn($"通知事件解析失败：{ex.Message}");
        }
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
        _httpClient.Dispose();
    }
}
