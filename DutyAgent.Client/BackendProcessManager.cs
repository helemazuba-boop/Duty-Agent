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

    private Process? _process;
    private string? _assetsDirectory;
    private string? _bridgeMetaPath;
    private ClassIslandConfigDiscovery? _classIslandConfig;

    public string AssetsDirectory => _assetsDirectory ??= ResolveAssetsDirectory();
    public string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DutyAgent",
        "data");
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
            RedirectStandardInput = true,
            CreateNoWindow = true,
            WorkingDirectory = AssetsDirectory,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        startInfo.ArgumentList.Add(corePy);
        startInfo.ArgumentList.Add("--server");
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add("0");
        startInfo.ArgumentList.Add("--data-dir");
        startInfo.ArgumentList.Add(DataDirectory);
        startInfo.ArgumentList.Add("--disable-mcp-runtime");

        _process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动后端进程。");

        try
        {
            await WaitForBootstrapAsync(_process, cancellationToken).ConfigureAwait(false);
            await WaitForHttpReadyAsync(cancellationToken).ConfigureAwait(false);
            WriteBridgeMetaFile();
        }
        catch
        {
            await StopAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task StopAsync()
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
            catch
            {
                hostConfig.Clear();
            }
        }

        hostConfig["access_token_mode"] = "dynamic";
        hostConfig["static_access_token_verifier"] = "";
        hostConfig.TryAdd("enable_mcp", false);
        hostConfig.TryAdd("enable_webview_debug_layer", false);

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

    private async Task WaitForBootstrapAsync(Process process, CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(StartupTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var stderr = new StringBuilder();

        _ = Task.Run(async () =>
        {
            try
            {
                while (!process.HasExited)
                {
                    var line = await process.StandardError.ReadLineAsync(linkedCts.Token).ConfigureAwait(false);
                    if (line is null)
                    {
                        break;
                    }

                    if (line.Length > 0)
                    {
                        stderr.AppendLine(line);
                    }
                }
            }
            catch
            {
            }
        }, CancellationToken.None);

        try
        {
            while (!linkedCts.IsCancellationRequested)
            {
                if (process.HasExited)
                {
                    throw new InvalidOperationException(
                        $"后端进程过早退出，退出码 {process.ExitCode}。\n{stderr}");
                }

                var line = await process.StandardOutput.ReadLineAsync(linkedCts.Token).ConfigureAwait(false);
                if (line is null)
                {
                    await Task.Delay(50, linkedCts.Token).ConfigureAwait(false);
                    continue;
                }

                var trimmed = line.Trim();
                var portMatch = PortRegex.Match(trimmed);
                if (portMatch.Success)
                {
                    Port = int.Parse(portMatch.Groups[1].Value);
                    continue;
                }

                var tokenMatch = TokenRegex.Match(trimmed);
                if (tokenMatch.Success)
                {
                    Token = tokenMatch.Groups[1].Value.Trim();
                }

                if (Port > 0 && !string.IsNullOrWhiteSpace(Token))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException($"后端启动超时，未在 {StartupTimeout.TotalSeconds:0} 秒内输出端口和 token。\n{stderr}");
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
