using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Shared;
using DutyIsland.Models;
using DutyIsland.Services;
using Microsoft.Web.WebView2.Core;

namespace DutyIsland.Views.SettingPages;

[FullWidthPage]
[HidePageTitle]
[SettingsPageInfo("duty.settings", "Duty-Agent", "\uE31E", "\uE31E")]
public partial class DutyWebSettingsPage : SettingsPageBase
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly DutyBackendService _backendService;
    private readonly DutyNotificationService _notificationService;
    private readonly DutyWebViewHost _webViewHost;

    public DutyWebSettingsPage()
        : this(
            IAppHost.GetService<DutyBackendService>(),
            IAppHost.GetService<DutyNotificationService>())
    {
    }

    public DutyWebSettingsPage(
        DutyBackendService backendService,
        DutyNotificationService notificationService)
    {
        _backendService = backendService;
        _notificationService = notificationService;

        InitializeComponent();

        _webViewHost = new DutyWebViewHost(ResolveWebEntryPath());
        _webViewHost.WebMessageReceived += OnWebMessageReceived;
        WebViewHostContainer.Child = _webViewHost;

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        await SendSnapshotAsync();
    }

    private void OnWebMessageReceived(object? sender, string messageJson)
    {
        _ = HandleWebMessageAsync(messageJson);
    }

    private async Task HandleWebMessageAsync(string messageJson)
    {
        BridgeEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<BridgeEnvelope>(messageJson, JsonOptions);
        }
        catch (Exception ex)
        {
            await SendErrorAsync("invalid_json", ex.Message);
            return;
        }

        var action = (envelope?.Action ?? string.Empty).Trim().ToLowerInvariant();
        var payload = envelope?.Payload ?? default;
        if (action.Length == 0)
        {
            await SendErrorAsync("missing_action", "Bridge message action is empty.");
            return;
        }

        try
        {
            switch (action)
            {
                case "ready":
                case "load_all":
                    await SendSnapshotAsync();
                    break;
                case "save_config":
                    await HandleSaveConfigAsync(payload);
                    break;
                case "save_roster":
                    await HandleSaveRosterAsync(payload);
                    break;
                case "run_core":
                    await HandleRunCoreAsync(payload);
                    break;
                case "publish_notification":
                    await HandlePublishNotificationAsync(payload);
                    break;
                default:
                    await SendErrorAsync("unknown_action", $"Unsupported action: {action}", action);
                    break;
            }
        }
        catch (Exception ex)
        {
            await SendErrorAsync("handler_exception", ex.Message, action);
        }
    }

    private async Task HandleSaveConfigAsync(JsonElement payload)
    {
        var request = payload.Deserialize<SaveConfigRequest>(JsonOptions);
        if (request?.Config == null)
        {
            await SendErrorAsync("invalid_payload", "save_config requires payload.config.", "save_config");
            return;
        }

        ApplyConfig(request.Config);
        await SendSnapshotAsync();
    }

    private async Task HandleSaveRosterAsync(JsonElement payload)
    {
        var request = payload.Deserialize<SaveRosterRequest>(JsonOptions);
        var normalized = (request?.Roster ?? [])
            .Select(x => new RosterEntry
            {
                Id = x.Id,
                Name = (x.Name ?? string.Empty).Trim(),
                Active = x.Active
            })
            .ToList();

        _backendService.SaveRosterEntries(normalized);
        await SendSnapshotAsync();
    }

    private async Task HandleRunCoreAsync(JsonElement payload)
    {
        var request = payload.Deserialize<RunCoreRequest>(JsonOptions) ?? new RunCoreRequest();
        if (request.Config != null)
        {
            ApplyConfig(request.Config);
        }

        var instruction = (request.Instruction ?? string.Empty).Trim();
        if (instruction.Length == 0)
        {
            await SendRunResultAsync(false, "排班指令不能为空。");
            return;
        }

        var applyMode = NormalizeApplyMode(request.ApplyMode);
        try
        {
            var result = await Task.Run(() => _backendService.RunCoreAgentWithMessage(instruction, applyMode));
            await SendRunResultAsync(result.Success, result.Message);
            await SendSnapshotAsync();
        }
        catch (Exception ex)
        {
            await SendRunResultAsync(false, ex.Message);
        }
    }

    private async Task HandlePublishNotificationAsync(JsonElement payload)
    {
        var request = payload.Deserialize<PublishNotificationRequest>(JsonOptions);
        var text = (request?.Text ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return;
        }

        _notificationService.Publish(text, request?.DurationSeconds ?? 6);
        await Task.CompletedTask;
    }

    private void ApplyConfig(WebConfigDto config)
    {
        _backendService.LoadConfig();
        var current = _backendService.Config;

        var apiKey = config.ApiKey ?? current.DecryptedApiKey;
        var baseUrl = config.BaseUrl ?? current.BaseUrl;
        var model = config.Model ?? current.Model;
        var enableAutoRun = config.EnableAutoRun ?? current.EnableAutoRun;
        var autoRunDay = config.AutoRunDay ?? current.AutoRunDay;
        var autoRunTime = config.AutoRunTime ?? current.AutoRunTime;
        var perDay = config.PerDay ?? current.PerDay;
        var skipWeekends = config.SkipWeekends ?? current.SkipWeekends;
        var dutyRule = config.DutyRule ?? current.DutyRule;
        var startFromToday = config.StartFromToday ?? current.StartFromToday;
        var coverageDays = config.AutoRunCoverageDays ?? current.AutoRunCoverageDays;
        var componentRefreshTime = config.ComponentRefreshTime ?? current.ComponentRefreshTime;
        var pythonPath = config.PythonPath ?? current.PythonPath;
        var areaNames = config.AreaNames ?? current.AreaNames;
        var notificationTemplates = config.NotificationTemplates ?? current.NotificationTemplates;

        _backendService.SaveUserConfig(
            apiKey: apiKey,
            baseUrl: baseUrl,
            model: model,
            enableAutoRun: enableAutoRun,
            autoRunDay: autoRunDay,
            autoRunTime: autoRunTime,
            perDay: perDay,
            skipWeekends: skipWeekends,
            dutyRule: dutyRule,
            startFromToday: startFromToday,
            autoRunCoverageDays: coverageDays,
            componentRefreshTime: componentRefreshTime,
            pythonPath: pythonPath,
            areaNames: areaNames,
            notificationTemplates: notificationTemplates);
    }

    private async Task SendSnapshotAsync()
    {
        _backendService.LoadConfig();
        var config = _backendService.Config;

        var snapshot = new BridgeSnapshot
        {
            Config = new WebConfigDto
            {
                PythonPath = config.PythonPath,
                ApiKey = config.DecryptedApiKey,
                BaseUrl = config.BaseUrl,
                Model = config.Model,
                EnableAutoRun = config.EnableAutoRun,
                AutoRunDay = config.AutoRunDay,
                AutoRunTime = config.AutoRunTime,
                AutoRunCoverageDays = config.AutoRunCoverageDays,
                PerDay = config.PerDay,
                SkipWeekends = config.SkipWeekends,
                DutyRule = config.DutyRule,
                StartFromToday = config.StartFromToday,
                ComponentRefreshTime = config.ComponentRefreshTime,
                AreaNames = _backendService.GetAreaNames(),
                NotificationTemplates = _backendService.GetNotificationTemplates()
            },
            Roster = _backendService.LoadRosterEntries()
                .Select(x => new WebRosterEntryDto
                {
                    Id = x.Id,
                    Name = x.Name,
                    Active = x.Active
                })
                .ToList(),
            State = _backendService.LoadState()
        };

        await _webViewHost.PostJsonAsync(snapshot);
    }

    private Task SendRunResultAsync(bool success, string message)
    {
        return _webViewHost.PostJsonAsync(new RunResultMessage
        {
            Type = "run_result",
            Success = success,
            Message = message
        });
    }

    private Task SendErrorAsync(string code, string message, string? action = null)
    {
        return _webViewHost.PostJsonAsync(new ErrorMessage
        {
            Type = "error",
            Code = code,
            Message = message,
            Action = action
        });
    }

    private static string NormalizeApplyMode(string? applyMode)
    {
        return (applyMode ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "append" => "append",
            "replace_future" => "replace_future",
            "replace_overlap" => "replace_overlap",
            "replace_all" => "replace_all",
            _ => "append"
        };
    }

    private static string ResolveWebEntryPath()
    {
        var baseDir = Path.GetDirectoryName(typeof(DutyWebSettingsPage).Assembly.Location) ?? AppContext.BaseDirectory;
        var indexPath = Path.Combine(baseDir, "Assets_Duty", "web", "index.html");
        if (File.Exists(indexPath))
        {
            return indexPath;
        }

        return Path.Combine(baseDir, "Assets_Duty", "web", "test.html");
    }

    private sealed class BridgeEnvelope
    {
        [JsonPropertyName("action")]
        public string? Action { get; set; }

        [JsonPropertyName("payload")]
        public JsonElement Payload { get; set; }
    }

    private sealed class SaveConfigRequest
    {
        [JsonPropertyName("config")]
        public WebConfigDto? Config { get; set; }
    }

    private sealed class SaveRosterRequest
    {
        [JsonPropertyName("roster")]
        public List<WebRosterEntryDto>? Roster { get; set; }
    }

    private sealed class RunCoreRequest
    {
        [JsonPropertyName("instruction")]
        public string? Instruction { get; set; }

        [JsonPropertyName("apply_mode")]
        public string? ApplyMode { get; set; }

        [JsonPropertyName("config")]
        public WebConfigDto? Config { get; set; }
    }

    private sealed class PublishNotificationRequest
    {
        [JsonPropertyName("text")]
        public string? Text { get; set; }

        [JsonPropertyName("duration_seconds")]
        public double DurationSeconds { get; set; } = 6;
    }

    private sealed class BridgeSnapshot
    {
        [JsonPropertyName("config")]
        public WebConfigDto Config { get; set; } = new();

        [JsonPropertyName("roster")]
        public List<WebRosterEntryDto> Roster { get; set; } = [];

        [JsonPropertyName("state")]
        public DutyState State { get; set; } = new();
    }

    private sealed class RunResultMessage
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "run_result";

        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;
    }

    private sealed class ErrorMessage
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "error";

        [JsonPropertyName("code")]
        public string Code { get; set; } = string.Empty;

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("action")]
        public string? Action { get; set; }
    }

    private sealed class WebConfigDto
    {
        [JsonPropertyName("python_path")]
        public string? PythonPath { get; set; }

        [JsonPropertyName("api_key")]
        public string? ApiKey { get; set; }

        [JsonPropertyName("base_url")]
        public string? BaseUrl { get; set; }

        [JsonPropertyName("model")]
        public string? Model { get; set; }

        [JsonPropertyName("enable_auto_run")]
        public bool? EnableAutoRun { get; set; }

        [JsonPropertyName("auto_run_day")]
        public string? AutoRunDay { get; set; }

        [JsonPropertyName("auto_run_time")]
        public string? AutoRunTime { get; set; }

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

        [JsonPropertyName("component_refresh_time")]
        public string? ComponentRefreshTime { get; set; }

        [JsonPropertyName("area_names")]
        public List<string>? AreaNames { get; set; }

        [JsonPropertyName("notification_templates")]
        public List<string>? NotificationTemplates { get; set; }
    }

    private sealed class WebRosterEntryDto
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("active")]
        public bool Active { get; set; } = true;
    }
}

internal sealed class DutyWebViewHost : NativeControlHost, IDisposable
{
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipChildren = 0x02000000;
    private const int WsClipSiblings = 0x04000000;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(
        int exStyle,
        string className,
        string windowName,
        int style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool MoveWindow(IntPtr handle, int x, int y, int width, int height, bool repaint);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    private readonly string _entryPath;
    private IntPtr _childHandle = IntPtr.Zero;
    private CoreWebView2Environment? _environment;
    private CoreWebView2Controller? _controller;
    private bool _isDisposed;

    public event EventHandler<string>? WebMessageReceived;

    public DutyWebViewHost(string entryPath)
    {
        _entryPath = entryPath;
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        try
        {
            var scale = GetScaleFactor();
            var pixelWidth = (int)(Bounds.Width * scale);
            var pixelHeight = (int)(Bounds.Height * scale);
            _childHandle = CreateWindowExW(
                0,
                "Static",
                "DutyWebViewHost",
                WsChild | WsVisible | WsClipChildren | WsClipSiblings,
                0,
                0,
                pixelWidth,
                pixelHeight,
                parent.Handle,
                IntPtr.Zero,
                GetModuleHandle(null),
                IntPtr.Zero);

            _ = InitializeWebView2Async();
            return new PlatformHandle(_childHandle, "HWND");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"DutyWebViewHost CreateNativeControlCore failed: {ex}");
            return base.CreateNativeControlCore(parent);
        }
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        Dispose();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        ResizeNativeHost(e.NewSize);
    }

    public async Task PostJsonAsync(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_isDisposed)
            {
                return;
            }

            _controller?.CoreWebView2?.PostWebMessageAsJson(json);
        });
    }

    private async Task InitializeWebView2Async()
    {
        if (!File.Exists(_entryPath))
        {
            Debug.WriteLine($"DutyWebViewHost entry page not found: {_entryPath}");
            return;
        }

        try
        {
            var userDataPath = Path.Combine(Path.GetTempPath(), "DutyAgent.WebView2");
            Directory.CreateDirectory(userDataPath);

            _environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataPath);
            _controller = await _environment.CreateCoreWebView2ControllerAsync(_childHandle);
            if (_controller == null)
            {
                return;
            }

            _controller.DefaultBackgroundColor = System.Drawing.Color.Transparent;
            _controller.CoreWebView2.Settings.IsWebMessageEnabled = true;
            _controller.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _controller.CoreWebView2.WebMessageReceived += OnCoreWebMessageReceived;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ResizeNativeHost(Bounds.Size);
                _controller.IsVisible = true;
                var uri = new Uri(_entryPath).AbsoluteUri;
                _controller.CoreWebView2.Navigate(uri);
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"DutyWebViewHost InitializeWebView2Async failed: {ex}");
        }
    }

    private void OnCoreWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var messageJson = e.WebMessageAsJson;
            Dispatcher.UIThread.Post(() => WebMessageReceived?.Invoke(this, messageJson));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"DutyWebViewHost message parse failed: {ex}");
        }
    }

    private void ResizeNativeHost(Size newSize)
    {
        if (_childHandle == IntPtr.Zero)
        {
            return;
        }

        var scale = GetScaleFactor();
        var width = (int)(newSize.Width * scale);
        var height = (int)(newSize.Height * scale);
        if (width <= 0 || height <= 0)
        {
            return;
        }

        MoveWindow(_childHandle, 0, 0, width, height, true);
        if (_controller != null)
        {
            _controller.Bounds = new System.Drawing.Rectangle(0, 0, width, height);
        }
    }

    private double GetScaleFactor()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        return topLevel?.RenderScaling ?? 1.0;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        if (_controller != null)
        {
            _controller.CoreWebView2.WebMessageReceived -= OnCoreWebMessageReceived;
            _controller.Close();
            _controller = null;
        }

        _environment = null;

        if (_childHandle != IntPtr.Zero)
        {
            DestroyWindow(_childHandle);
            _childHandle = IntPtr.Zero;
        }

        GC.SuppressFinalize(this);
    }
}
