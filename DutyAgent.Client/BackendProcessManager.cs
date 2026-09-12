using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DutyAgent.Client;

internal sealed class BackendProcessManager : IDisposable
{
    private static readonly Regex PortRegex = new(@"^__DUTY_SERVER_PORT__\s*[:=]\s*(\d+)\s*$", RegexOptions.Compiled);
    private static readonly Regex TokenRegex = new(@"^__DUTY_SERVER_TOKEN__\s*[:=]\s*(.+)\s*$", RegexOptions.Compiled);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);
    private const string BridgeMetaFileName = ".duty-agent-meta.json";

    // ======== watchdog（启动成功后的意外退出自动重启） ========
    // 参考 DutyPythonIpcService.ScheduleEngineAutoRestart 的有界重试语义：
    // 最多连续 3 次重启尝试，退避 2/8/30s；进程稳定运行满 30min 后崩溃
    // 视为环境偶发问题，重置计数，给满新一轮预算。
    private const int MaxRestartAttempts = 3;
    private static readonly TimeSpan[] RestartBackoffSchedule =
    {
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(30),
    };
    private static readonly TimeSpan StableUptimeThreshold = TimeSpan.FromMinutes(30);

    private Process? _process;
    private string? _assetsDirectory;
    private string? _bridgeMetaPath;
    private ClassIslandConfigDiscovery? _classIslandConfig;
    private TaskCompletionSource<bool>? _bootstrapTcs;
    private Task? _stdoutPump;
    private Task? _stderrPump;
    private readonly StringBuilder _recentStderr = new();
    private readonly object _stderrLock = new();
    private DateTime _lastReadyUtc = DateTime.MinValue;
    private int _restartFailures;
    private int _watchdogInFlight;
    private volatile bool _intentionalStop;

    /// <summary>后端经 watchdog 自动重启成功后触发（后台线程回调，订阅方自行调度到 UI 线程）。</summary>
    public event EventHandler? BackendRestarted;

    public string AssetsDirectory => _assetsDirectory ??= ResolveAssetsDirectory();
    public string DataDirectory => ClientLog.DataDirectory;
    public string BridgeMetaPath => _bridgeMetaPath ??= ResolveBridgeMetaPath();
    public ClassIslandConfigDiscovery ClassIslandConfig => _classIslandConfig ??= ClassIslandConfigLocator.Discover();

    public int Port { get; private set; }
    public string? Token { get; private set; }
    public string BaseUrl => Port > 0 ? $"http://127.0.0.1:{Port}" : string.Empty;
    public string WebAppUrl => BuildWebAppUrl("/dashboard");

    public string BuildWebAppUrl(string route)
    {
        var normalizedRoute = string.IsNullOrWhiteSpace(route) ? "/dashboard" : route.Trim();
        if (normalizedRoute.StartsWith("#/", StringComparison.Ordinal))
        {
            normalizedRoute = normalizedRoute[1..];
        }

        if (!normalizedRoute.StartsWith("/", StringComparison.Ordinal))
        {
            normalizedRoute = $"/{normalizedRoute}";
        }

        return string.IsNullOrWhiteSpace(Token)
            ? $"{BaseUrl}/app/#{normalizedRoute}"
            : $"{BaseUrl}/app/#{normalizedRoute}?access_token={Uri.EscapeDataString(Token)}";
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false })
        {
            return;
        }

        EnsureRequiredFiles();
        EnsureDefaultHostConfig();

        var pythonExe = Path.Combine(AssetsDirectory, "python-embed", "python.exe");
        var corePy = Path.Combine(AssetsDirectory, "core.py");

        var startInfo = new ProcessStartInfo
        {
            FileName = pythonExe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = AssetsDirectory,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // 环境清洗：宿主调试开关不得泄漏进后端子进程——
        // SKIP_AUTH_BYPASS 会放开后端鉴权，DUTY_DEBUG_* 会改变后端调试行为。
        startInfo.EnvironmentVariables.Remove("SKIP_AUTH_BYPASS");
        foreach (var key in startInfo.EnvironmentVariables.Keys.Cast<string>()
            .Where(key => key.StartsWith("DUTY_DEBUG_", StringComparison.OrdinalIgnoreCase))
            .ToList())
        {
            startInfo.EnvironmentVariables.Remove(key);
        }

        startInfo.ArgumentList.Add(corePy);
        startInfo.ArgumentList.Add("--server");
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add("0");
        startInfo.ArgumentList.Add("--data-dir");
        startInfo.ArgumentList.Add(DataDirectory);
        startInfo.ArgumentList.Add("--disable-mcp-runtime");

        _process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动后端进程。");
        var process = _process;
        var bootstrapTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _bootstrapTcs = bootstrapTcs;
        process.EnableRaisingEvents = true;
        // 闭包捕获本次引导的 TCS：旧进程迟到的 Exited 不会污染新一轮引导。
        process.Exited += (_, _) => OnBackendProcessExited(process, bootstrapTcs);
        StartPipePumps(process);

        try
        {
            await WaitForBootstrapAsync(process, cancellationToken).ConfigureAwait(false);
            await WaitForHttpReadyAsync(cancellationToken).ConfigureAwait(false);
            WriteBridgeMetaFile();
        }
        catch
        {
            await StopCoreAsync().ConfigureAwait(false);
            throw;
        }

        _lastReadyUtc = DateTime.UtcNow;
        _intentionalStop = false;
    }

    /// <summary>外部主动停止（窗口关闭 / 程序退出）：停掉 watchdog，不再自动重启。</summary>
    public async Task StopAsync()
    {
        _intentionalStop = true;
        await StopCoreAsync().ConfigureAwait(false);
    }

    /// <summary>内部清理：不触碰 watchdog 状态（引导失败与重启失败路径复用）。</summary>
    private async Task StopCoreAsync()
    {
        DeleteBridgeMetaFile();

        var process = _process;
        _process = null;
        if (process is null)
        {
            return;
        }

        if (process.HasExited)
        {
            process.Dispose();
            return;
        }

        await TryRequestShutdownAsync().ConfigureAwait(false);

        try
        {
            using var cts = new CancellationTokenSource(ShutdownTimeout);
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryTerminate(process);
        }
        finally
        {
            process.Dispose();
        }
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
    }

    private void OnBackendProcessExited(Process process, TaskCompletionSource<bool> bootstrapTcs)
    {
        if (!bootstrapTcs.Task.IsCompleted)
        {
            // 引导期退出（端口/token 尚未输出即崩溃）：维持既有语义——让等待方收到启动失败。
            bootstrapTcs.TrySetException(
                new InvalidOperationException("后端进程在输出端口与 token 之前退出。"));
            return;
        }

        // 启动成功后的意外退出：交给 watchdog 有界重启（主动停止时由 _intentionalStop 拦下）。
        if (_intentionalStop)
        {
            return;
        }

        var exitCode = -1;
        try
        {
            exitCode = process.ExitCode;
        }
        catch
        {
        }

        // 长时间稳定运行证明启动配置有效；只有快速崩溃循环才应耗尽重启预算。
        var uptime = DateTime.UtcNow - _lastReadyUtc;
        if (uptime >= StableUptimeThreshold)
        {
            _restartFailures = 0;
        }

        ClientLog.Warn($"后端进程意外退出（exit code {exitCode}，已运行 {(int)uptime.TotalSeconds}s），准备自动重启。");
        ScheduleWatchdogRestart();
    }

    private void ScheduleWatchdogRestart()
    {
        // 单飞闸门：Exited 事件与进行中的重启循环可能竞争，同一时刻只允许一个循环。
        if (Interlocked.CompareExchange(ref _watchdogInFlight, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                while (!_intentionalStop)
                {
                    if (_restartFailures >= MaxRestartAttempts)
                    {
                        ClientLog.Error(
                            $"后端自动重启已连续失败 {_restartFailures} 次，放弃本轮自动重启；进程再次意外退出（稳定运行满 30min）时将重新计数。");
                        return;
                    }

                    var delay = RestartBackoffSchedule[Math.Min(_restartFailures, RestartBackoffSchedule.Length - 1)];
                    await Task.Delay(delay).ConfigureAwait(false);
                    if (_intentionalStop)
                    {
                        return;
                    }

                    _restartFailures++;
                    ClientLog.Warn(
                        $"尝试自动重启后端（第 {_restartFailures}/{MaxRestartAttempts} 次，退避 {(int)delay.TotalSeconds}s）。");
                    try
                    {
                        await StartAsync(CancellationToken.None).ConfigureAwait(false);
                        if (_intentionalStop)
                        {
                            // 关停与重启竞态：回收刚拉起的进程。
                            await StopCoreAsync().ConfigureAwait(false);
                            return;
                        }

                        _restartFailures = 0;
                        ClientLog.Info("后端自动重启成功。");
                        BackendRestarted?.Invoke(this, EventArgs.Empty);
                        return;
                    }
                    catch (Exception ex)
                    {
                        // 循环继续，按下一档退避重试。
                        ClientLog.Error("后端自动重启失败。", ex);
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _watchdogInFlight, 0);
            }
        });
    }

    private static string ResolveAssetsDirectory()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var candidates = new List<string>
        {
            Path.Combine(baseDirectory, "Assets_Duty"),
        };

        var current = new DirectoryInfo(baseDirectory);
        while (current is not null)
        {
            candidates.Add(Path.Combine(current.FullName, "Assets_Duty"));
            current = current.Parent;
        }

        var match = candidates.FirstOrDefault(path =>
            Directory.Exists(path) && File.Exists(Path.Combine(path, "core.py")));

        if (match is null)
        {
            throw new DirectoryNotFoundException(
                "未找到 Assets_Duty。请确认 Assets_Duty 位于客户端 exe 旁或仓库根目录。");
        }

        return Path.GetFullPath(match);
    }

    private string ResolveBridgeMetaPath()
    {
        return Path.Combine(ClassIslandConfig.ConfigFolder, "DutyAgentBridge", BridgeMetaFileName);
    }

    private void EnsureRequiredFiles()
    {
        var pythonExe = Path.Combine(AssetsDirectory, "python-embed", "python.exe");
        var corePy = Path.Combine(AssetsDirectory, "core.py");
        var webIndex = Path.Combine(AssetsDirectory, "web", "index.html");

        if (!File.Exists(pythonExe))
        {
            throw new FileNotFoundException("未找到内置 Python。", pythonExe);
        }

        if (!File.Exists(corePy))
        {
            throw new FileNotFoundException("未找到后端入口 core.py。", corePy);
        }

        if (!File.Exists(webIndex))
        {
            throw new FileNotFoundException(
                "未找到前端页面 Assets_Duty/web/index.html。请先构建前端并复制 dist 到 Assets_Duty/web。",
                webIndex);
        }
    }

    private void EnsureDefaultHostConfig()
    {
        Directory.CreateDirectory(DataDirectory);
        var hostConfigPath = Path.Combine(DataDirectory, "host-config.json");
        var hostConfig = new Dictionary<string, object?>();
        if (File.Exists(hostConfigPath))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(hostConfigPath, Encoding.UTF8));
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    hostConfig[property.Name] = JsonSerializer.Deserialize<object?>(property.Value.GetRawText());
                }
            }
            catch (Exception ex)
            {
                // 损坏防护：先把原文件留档，再写默认配置，避免用户数据无声丢失。
                var backupPath = $"{hostConfigPath}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
                try
                {
                    File.Copy(hostConfigPath, backupPath, overwrite: false);
                    ClientLog.Warn(
                        $"host-config.json 解析失败（{ex.Message}），已备份为 {Path.GetFileName(backupPath)} 并重建默认配置。");
                }
                catch (Exception copyEx)
                {
                    ClientLog.Error($"host-config.json 解析失败且备份失败（{copyEx.Message}）。原始错误：{ex.Message}");
                }

                hostConfig.Clear();
            }
        }

        // 只补缺失键，不再无条件重置：access_token_mode / static_access_token_verifier
        // 是用户可配置项，已有文件里的值必须保留。
        hostConfig.TryAdd("access_token_mode", "dynamic");
        hostConfig.TryAdd("static_access_token_verifier", "");
        hostConfig.TryAdd("enable_mcp", false);
        hostConfig.TryAdd("enable_webview_debug_layer", false);
        // web 自绘标题栏(窗口三键/拖拽在 web,缩放贴靠仍归系统);异常时置 false 回退原生标题栏
        hostConfig.TryAdd("client_custom_chrome", true);

        var json = JsonSerializer.Serialize(
            hostConfig,
            new JsonSerializerOptions { WriteIndented = true });

        File.WriteAllText(hostConfigPath, json, new UTF8Encoding(false));
    }

    private void WriteBridgeMetaFile()
    {
        if (_process is not { HasExited: false } || Port <= 0)
        {
            return;
        }

        var metaPath = BridgeMetaPath;
        var directory = Path.GetDirectoryName(metaPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var payload = new Dictionary<string, object?>
        {
            ["version"] = "0.50.0",
            ["pid"] = Environment.ProcessId,
            ["port"] = Port,
            ["token_mode"] = string.IsNullOrWhiteSpace(Token) ? "none" : "dynamic",
            ["token"] = Token ?? "",
            ["started_at"] = DateTimeOffset.UtcNow.ToString("O"),
            ["data_dir"] = DataDirectory,
            ["webapp_url"] = $"{BaseUrl}/app/#/dashboard",
            ["classisland_config_dir"] = ClassIslandConfig.ConfigFolder,
            ["classisland_config_source"] = ClassIslandConfig.Source,
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        var tempPath = metaPath + ".tmp";
        File.WriteAllText(tempPath, json, new UTF8Encoding(false));
        File.Move(tempPath, metaPath, overwrite: true);
    }

    private void DeleteBridgeMetaFile()
    {
        var metaPath = BridgeMetaPath;
        if (!File.Exists(metaPath))
        {
            return;
        }

        try
        {
            if (!MetaFileBelongsToCurrentProcess(metaPath))
            {
                return;
            }

            File.Delete(metaPath);
        }
        catch
        {
        }
    }

    private static bool MetaFileBelongsToCurrentProcess(string metaPath)
    {
        try
        {
            var json = File.ReadAllText(metaPath, Encoding.UTF8);
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("pid", out var pidElement))
            {
                return false;
            }

            return pidElement.TryGetInt32(out var pid) && pid == Environment.ProcessId;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 全生命周期排空 stdout/stderr。管道缓冲区有限（约 4 KiB），一旦写满，
    /// 任何再写管道的线程都会永久阻塞——此前运行期一条 stderr traceback
    /// 就冻死了后端事件循环（进程"活着"，但所有请求超时）。
    /// </summary>
    private void StartPipePumps(Process process)
    {
        _stdoutPump = Task.Run(async () =>
        {
            try
            {
                while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    if (HandleBootstrapLine(line))
                    {
                        _bootstrapTcs?.TrySetResult(true);
                        continue;
                    }

                    Debug.WriteLine($"[Python] {line}");
                }
            }
            catch
            {
                // 进程退出或 Dispose 后管道关闭，泵自然结束。
            }
        });

        _stderrPump = Task.Run(async () =>
        {
            try
            {
                while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    Debug.WriteLine($"[Python ERR] {line}");
                    lock (_stderrLock)
                    {
                        _recentStderr.AppendLine(line);
                        if (_recentStderr.Length > 8000)
                        {
                            _recentStderr.Remove(0, _recentStderr.Length - 4000);
                        }
                    }
                }
            }
            catch
            {
            }
        });
    }

    private bool HandleBootstrapLine(string line)
    {
        var trimmed = line.Trim();
        var portMatch = PortRegex.Match(trimmed);
        if (portMatch.Success)
        {
            Port = int.Parse(portMatch.Groups[1].Value);
        }
        else
        {
            var tokenMatch = TokenRegex.Match(trimmed);
            if (!tokenMatch.Success)
            {
                return false;
            }

            Token = tokenMatch.Groups[1].Value.Trim();
        }

        return Port > 0 && !string.IsNullOrWhiteSpace(Token);
    }

    private async Task WaitForBootstrapAsync(Process process, CancellationToken cancellationToken)
    {
        var bootstrapTcs = _bootstrapTcs
            ?? throw new InvalidOperationException("后端引导尚未初始化。");
        using var timeoutCts = new CancellationTokenSource(StartupTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            await bootstrapTcs.Task.WaitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            string recentStderr;
            lock (_stderrLock)
            {
                recentStderr = _recentStderr.ToString();
            }

            throw new TimeoutException(
                $"后端启动超时，未在 {StartupTimeout.TotalSeconds:0} 秒内输出端口和 token。\n{recentStderr}");
        }
    }

    private async Task TryRequestShutdownAsync()
    {
        if (Port <= 0 || string.IsNullOrWhiteSpace(Token))
        {
            return;
        }

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
            await client.PostAsync($"{BaseUrl}/shutdown", content: null).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private async Task WaitForHttpReadyAsync(CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(StartupTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };

        try
        {
            while (true)
            {
                linkedCts.Token.ThrowIfCancellationRequested();
                try
                {
                    using var response = await client.GetAsync($"{BaseUrl}/app/", linkedCts.Token).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        return;
                    }
                }
                catch when (!timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                }

                await Task.Delay(250, linkedCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Backend HTTP server did not become ready within {StartupTimeout.TotalSeconds:0} seconds.");
        }
    }

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch
        {
        }
    }
}
