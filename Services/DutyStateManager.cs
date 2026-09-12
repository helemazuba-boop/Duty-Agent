using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using DutyAgent.Models;

namespace DutyAgent.Services;

public interface IStateAndRosterManager
{
    DutyState LoadState();
    event EventHandler<DutyState> StateChanged;
}

/// <summary>
/// 插件侧状态源（D1：插件不再有任何 state.json 文件读写）。
/// 数据唯一来源是 Python 后端 GET /api/v1/state，经 <see cref="IPythonIpcService"/> 拉取；
/// 变更感知 = 5s 轮询 + mtime_ns 比较（mtime_ns 缺失时退化为状态内容比较），
/// StateChanged 仍保留 150ms 去抖与 UI 线程投递语义。
/// 后端未就绪/请求失败时保留上一次成功快照，仅记录 Debug 日志，不向外抛异常。
/// </summary>
public class DutyStateManager : IStateAndRosterManager, IDisposable
{
    private const int StatePollIntervalMilliseconds = 5000;
    private const int StateChangeDebounceMilliseconds = 150;

    private readonly IPythonIpcService _ipcService;
    private readonly object _stateGate = new();
    private DutyState _cachedState = new();
    private long? _cachedMtimeNs;
    private string _cachedStateJson = string.Empty;
    private bool _lastPollSucceeded;

    // LoadState() 的即时刷新提示：计数最多 1，多余提示被吞掉（防抖动）。
    private readonly SemaphoreSlim _pollWake = new(0, 1);
    private CancellationTokenSource? _pollCts;
    private readonly object _stateChangeGate = new();
    private CancellationTokenSource? _pendingStateChangeCts;
    private bool _disposed;

    public event EventHandler<DutyState>? StateChanged;

    public DutyStateManager(IPythonIpcService ipcService)
    {
        _ipcService = ipcService;
        _pollCts = new CancellationTokenSource();
        _ = Task.Run(() => RunPollLoopAsync(_pollCts.Token));
    }

    public DutyState LoadState()
    {
        DutyState snapshot;
        lock (_stateGate)
        {
            snapshot = CloneState(_cachedState);
        }

        // 状态由轮询循环维护；显式读取只提示循环立即拉取一次（非阻塞），
        // 让刚发生的变更不必等满 5s 心跳才出现在 UI 上。
        RequestPollWake();
        return snapshot;
    }

    private async Task RunPollLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await PollBackendStateOnceAsync(cancellationToken).ConfigureAwait(false);
                // 5s 心跳；LoadState() 的提示可提前唤醒（计数上限 1）。
                await _pollWake.WaitAsync(StatePollIntervalMilliseconds, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Dispose 取消轮询：预期路径。
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"DutyStateManager poll loop crashed: {ex.Message}");
        }
    }

    private async Task PollBackendStateOnceAsync(CancellationToken cancellationToken)
    {
        DutyBackendStateEnvelope? envelope;
        try
        {
            envelope = await _ipcService.GetBackendStateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 后端未就绪/请求失败：保留上一次成功缓存。只在"成功↔失败"翻转时
            // 记一条日志，避免 5s 一条的调试噪音（引擎长期 Faulted 时尤其明显）。
            if (_lastPollSucceeded)
            {
                _lastPollSucceeded = false;
                Debug.WriteLine($"DutyStateManager state poll failed (keeping last snapshot): {ex.Message}");
            }
            return;
        }

        if (!_lastPollSucceeded)
        {
            _lastPollSucceeded = true;
            Debug.WriteLine("DutyStateManager state poll recovered.");
        }

        ApplyStateSnapshot(envelope);
    }

    private void ApplyStateSnapshot(DutyBackendStateEnvelope? envelope)
    {
        if (envelope?.State == null)
        {
            return;
        }

        string stateJson;
        try
        {
            stateJson = JsonSerializer.Serialize(envelope.State);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"DutyStateManager state serialize error: {ex.Message}");
            return;
        }

        bool changed;
        lock (_stateGate)
        {
            // mtime_ns 变化优先（契约 C1）；后端未提供 mtime_ns（null→null）时
            // 退化为状态内容比较，避免重复事件，也避免漏报。
            changed = !Nullable.Equals(envelope.MtimeNs, _cachedMtimeNs) ||
                      !string.Equals(stateJson, _cachedStateJson, StringComparison.Ordinal);
            if (changed)
            {
                _cachedMtimeNs = envelope.MtimeNs;
                _cachedStateJson = stateJson;
                _cachedState = envelope.State;
            }
        }

        if (changed)
        {
            QueueStateChangedNotification();
        }
    }

    private void QueueStateChangedNotification()
    {
        CancellationTokenSource nextCts;
        lock (_stateChangeGate)
        {
            _pendingStateChangeCts?.Cancel();
            _pendingStateChangeCts?.Dispose();
            _pendingStateChangeCts = new CancellationTokenSource();
            nextCts = _pendingStateChangeCts;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(StateChangeDebounceMilliseconds, nextCts.Token).ConfigureAwait(false);
                DutyState snapshot;
                lock (_stateGate)
                {
                    snapshot = CloneState(_cachedState);
                }

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!_disposed)
                    {
                        StateChanged?.Invoke(this, snapshot);
                    }
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"State change dispatch error: {ex.Message}");
            }
        });
    }

    private void RequestPollWake()
    {
        try
        {
            if (_pollWake.CurrentCount == 0)
            {
                _pollWake.Release();
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private static DutyState CloneState(DutyState state)
    {
        try
        {
            return JsonSerializer.Deserialize<DutyState>(JsonSerializer.Serialize(state)) ?? new DutyState();
        }
        catch (Exception)
        {
            return new DutyState();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = null;

        lock (_stateChangeGate)
        {
            _pendingStateChangeCts?.Cancel();
            _pendingStateChangeCts?.Dispose();
            _pendingStateChangeCts = null;
        }

        _pollWake.Dispose();
    }
}
