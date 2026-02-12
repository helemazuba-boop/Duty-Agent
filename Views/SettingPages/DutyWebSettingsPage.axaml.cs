using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
    private readonly DutyLocalPreviewHostedService _localPreviewHostedService;
    private readonly DutyWebViewHost _webViewHost;
    private Size _lastLoggedContainerSize;

    public DutyWebSettingsPage()
        : this(
            IAppHost.GetService<DutyBackendService>(),
            IAppHost.GetService<DutyNotificationService>(),
            IAppHost.GetService<DutyLocalPreviewHostedService>())
    {
    }

    public DutyWebSettingsPage(
        DutyBackendService backendService,
        DutyNotificationService notificationService,
        DutyLocalPreviewHostedService localPreviewHostedService)
    {
        _backendService = backendService;
        _notificationService = notificationService;
        _localPreviewHostedService = localPreviewHostedService;

        InitializeComponent();

        var entryPath = ResolveWebEntryPath();
        _webViewHost = new DutyWebViewHost(entryPath);
        _webViewHost.WebMessageReceived += OnWebMessageReceived;
        _webViewHost.ContentReady += OnWebContentReady;
        WebViewHostContainer.Child = _webViewHost;
        WebViewHostContainer.SizeChanged += OnWebViewHostContainerSizeChanged;

        Loaded += OnLoaded;
        DutyDiagnosticsLogger.Info("SettingsPage", "Duty web settings page initialized.",
            new
            {
                entryPath,
                logPath = DutyDiagnosticsLogger.CurrentLogPath
            });
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        await SendSnapshotAsync();
        _webViewHost.RequestResizeSync();
        DutyDiagnosticsLogger.Info("SettingsPage", "Settings page loaded.",
            new
            {
                containerWidth = Math.Round(WebViewHostContainer.Bounds.Width, 2),
                containerHeight = Math.Round(WebViewHostContainer.Bounds.Height, 2)
            });
    }

    private async void OnWebContentReady(object? sender, EventArgs e)
    {
        _webViewHost.RequestResizeSync();
        await SendThemeAsync();
        WebViewHostContainer.Opacity = 1;
        DutyDiagnosticsLogger.Info("SettingsPage", "Web content reported ready.");
    }

    private void OnWebViewHostContainerSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        _webViewHost.RequestResizeSync();
        var changed = Math.Abs(e.NewSize.Width - _lastLoggedContainerSize.Width) >= 0.5
            || Math.Abs(e.NewSize.Height - _lastLoggedContainerSize.Height) >= 0.5;
        if (changed)
        {
            _lastLoggedContainerSize = e.NewSize;
            DutyDiagnosticsLogger.Info("SettingsPage", "Container size changed.",
                new
                {
                    width = Math.Round(e.NewSize.Width, 2),
                    height = Math.Round(e.NewSize.Height, 2)
                });
        }
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
            DutyDiagnosticsLogger.Error("Bridge", "Failed to parse web message JSON.", ex,
                new { preview = TruncateForLog(messageJson, 220) });
            await SendErrorAsync("invalid_json", ex.Message);
            return;
        }

        var action = (envelope?.Action ?? string.Empty).Trim().ToLowerInvariant();
        var payload = envelope?.Payload ?? default;
        DutyDiagnosticsLogger.Info("Bridge", "Received web message.",
            new
            {
                action,
                payloadKind = payload.ValueKind.ToString()
            });
        if (action.Length == 0)
        {
            DutyDiagnosticsLogger.Warn("Bridge", "Web message action is empty.");
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
                case "trigger_run_completion_notification":
                    await HandleTriggerRunCompletionNotificationAsync(payload);
                    break;
                case "trigger_duty_reminder_notification":
                    await HandleTriggerDutyReminderNotificationAsync(payload);
                    break;
                case "open_test_in_browser":
                    await HandleOpenTestInBrowserAsync();
                    break;
                default:
                    await SendErrorAsync("unknown_action", $"Unsupported action: {action}", action);
                    break;
            }
        }
        catch (Exception ex)
        {
            DutyDiagnosticsLogger.Error("Bridge", "Web message handler failed.", ex, new { action });
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
        DutyDiagnosticsLogger.Info("RunCore", "Received run_core request.",
            new
            {
                hasConfig = request.Config != null,
                instructionLength = (request.Instruction ?? string.Empty).Trim().Length,
                requestedApplyMode = request.ApplyMode ?? string.Empty
            });
        if (request.Config != null)
        {
            ApplyConfig(request.Config);
            DutyDiagnosticsLogger.Info("RunCore", "Applied config from run_core payload.");
        }

        var instruction = (request.Instruction ?? string.Empty).Trim();
        if (instruction.Length == 0)
        {
            const string emptyInstructionMessage = "Instruction cannot be empty.";
            DutyDiagnosticsLogger.Warn("RunCore", emptyInstructionMessage);
            await SendRunStatusAsync("failed", emptyInstructionMessage);
            await SendRunResultAsync(false, emptyInstructionMessage, string.Empty);
            return;
        }

        var applyMode = NormalizeApplyMode(request.ApplyMode);
        await SendRunStatusAsync("started", "Schedule generation started.");
        DutyDiagnosticsLogger.Info("RunCore", "Run started.",
            new
            {
                applyMode,
                instructionPreview = TruncateForLog(instruction, 180)
            });
        try
        {
            var result = await Task.Run(() => _backendService.RunCoreAgentWithMessage(
                instruction,
                applyMode,
                progress: progress =>
                {
                    var phase = (progress.Phase ?? string.Empty).Trim().ToLowerInvariant();
                    if (phase.Length == 0)
                    {
                        return;
                    }

                    DutyDiagnosticsLogger.Info("RunCore", "Progress update.",
                        new
                        {
                            phase,
                            message = TruncateForLog(progress.Message ?? string.Empty, 220)
                        });
                    var progressMessage = progress.Message ?? string.Empty;
                    Dispatcher.UIThread.Post(() => _ = SendRunStatusAsync(phase, progressMessage));
                }));
            var resultMessage = result.Message ?? string.Empty;
            var aiResponse = result.AiResponse ?? string.Empty;
            DutyDiagnosticsLogger.Info("RunCore", "Run finished.",
                new
                {
                    success = result.Success,
                    message = TruncateForLog(resultMessage, 220),
                    aiResponseLength = aiResponse.Length
                });
            if (!string.IsNullOrWhiteSpace(aiResponse))
            {
                DutyDiagnosticsLogger.Info("RunCore", "AI response received.",
                    new
                    {
                        aiResponsePreview = TruncateForLog(aiResponse, 500)
                    });
            }

            await SendRunResultAsync(result.Success, resultMessage, aiResponse);
            if (result.Success)
            {
                TryPublishRunCompletionNotification(instruction, applyMode, resultMessage);
            }

            await SendRunStatusAsync(result.Success ? "completed" : "failed", resultMessage);
            await SendSnapshotAsync();
        }
        catch (Exception ex)
        {
            DutyDiagnosticsLogger.Error("RunCore", "Run failed with exception.", ex,
                new { applyMode });
            await SendRunStatusAsync("failed", ex.Message);
            await SendRunResultAsync(false, ex.Message, string.Empty);
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
        DutyDiagnosticsLogger.Info("Notification", "Published host notification.",
            new
            {
                duration = request?.DurationSeconds ?? 6,
                text = TruncateForLog(text, 200)
            });
        await Task.CompletedTask;
    }

    private async Task HandleTriggerRunCompletionNotificationAsync(JsonElement payload)
    {
        var request = payload.Deserialize<TriggerRunCompletionNotificationRequest>(JsonOptions)
                      ?? new TriggerRunCompletionNotificationRequest();
        var instruction = (request.Instruction ?? string.Empty).Trim();
        if (instruction.Length == 0)
        {
            instruction = "Manual trigger from test page.";
        }

        var applyMode = NormalizeApplyMode(request.ApplyMode);
        var message = (request.Message ?? string.Empty).Trim();
        if (message.Length == 0)
        {
            message = "Notification triggered from test page.";
        }

        TryPublishRunCompletionNotification(instruction, applyMode, message);
        DutyDiagnosticsLogger.Info("Notification", "Triggered run completion notification from web page.",
            new
            {
                applyMode,
                instructionPreview = TruncateForLog(instruction, 120),
                messagePreview = TruncateForLog(message, 120)
            });
        await Task.CompletedTask;
    }

    private async Task HandleTriggerDutyReminderNotificationAsync(JsonElement payload)
    {
        var request = payload.Deserialize<TriggerDutyReminderNotificationRequest>(JsonOptions)
                      ?? new TriggerDutyReminderNotificationRequest();
        var dateText = (request.Date ?? string.Empty).Trim();
        var timeText = (request.Time ?? string.Empty).Trim();

        _backendService.PublishDutyReminderNotificationNow(dateText, timeText);
        DutyDiagnosticsLogger.Info("Notification", "Triggered duty reminder notification from web page.",
            new
            {
                date = dateText,
                time = timeText
            });
        await Task.CompletedTask;
    }

    private async Task HandleOpenTestInBrowserAsync()
    {
        var target = _localPreviewHostedService.PreviewUrl;
        if (string.IsNullOrWhiteSpace(target))
        {
            target = new Uri(ResolveWebEntryPath()).AbsoluteUri;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = target,
            UseShellExecute = true
        });
        DutyDiagnosticsLogger.Info("WebPreview", "Opened test page in local browser.",
            new { target });
        await Task.CompletedTask;
    }

    private void ApplyConfig(WebConfigDto config)
    {
        _backendService.LoadConfig();
        var current = _backendService.Config;

        var apiKey = DutyBackendService.ResolveApiKeyInput(config.ApiKey, current.DecryptedApiKey);
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
        var areaPerDayCounts = config.AreaPerDayCounts ?? current.AreaPerDayCounts;
        var notificationTemplates = config.NotificationTemplates ?? current.NotificationTemplates;
        var dutyReminderEnabled = config.DutyReminderEnabled ?? current.DutyReminderEnabled;
        var dutyReminderTimes = config.DutyReminderTimes ?? current.DutyReminderTimes;
        var dutyReminderTemplates = config.DutyReminderTemplates ?? current.DutyReminderTemplates;

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
            areaPerDayCounts: areaPerDayCounts,
            notificationTemplates: notificationTemplates,
            dutyReminderEnabled: dutyReminderEnabled,
            dutyReminderTimes: dutyReminderTimes,
            dutyReminderTemplates: dutyReminderTemplates);
    }

    private async Task SendSnapshotAsync()
    {
        _backendService.LoadConfig();
        var config = _backendService.Config;

        var snapshot = new BridgeSnapshot
        {
            LocalPreviewUrl = _localPreviewHostedService.PreviewUrl,
            ApiOverwriteUrl = _localPreviewHostedService.ApiOverwriteUrl,
            McpUrl = _localPreviewHostedService.McpUrl,
            Config = new WebConfigDto
            {
                PythonPath = config.PythonPath,
                ApiKey = _backendService.GetApiKeyMaskForUi(),
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
                AreaPerDayCounts = _backendService.GetAreaPerDayCounts(),
                NotificationTemplates = _backendService.GetNotificationTemplates(),
                DutyReminderEnabled = config.DutyReminderEnabled,
                DutyReminderTimes = _backendService.GetDutyReminderTimes(),
                DutyReminderTemplates = _backendService.GetDutyReminderTemplates()
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

        DutyDiagnosticsLogger.Info("Bridge", "Sending snapshot to web.",
            new
            {
                rosterCount = snapshot.Roster.Count,
                scheduleCount = snapshot.State.SchedulePool.Count
            });
        await _webViewHost.PostJsonAsync(snapshot);
        await SendThemeAsync();
    }

    private Task SendThemeAsync()
    {
        var payload = BuildThemePayload();
        return _webViewHost.PostJsonAsync(new ThemeMessage
        {
            Type = "host_theme",
            Payload = payload
        });
    }

    private void TryPublishRunCompletionNotification(string instruction, string applyMode, string resultMessage)
    {
        try
        {
            _backendService.LoadConfig();
            var config = _backendService.Config;
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            var areaNames = _backendService.GetAreaNames();
            var state = _backendService.LoadState();
            var item = state.SchedulePool.LastOrDefault(x => string.Equals(x.Date, today, StringComparison.Ordinal));
            var assignments = item is null
                ? new Dictionary<string, List<string>>(StringComparer.Ordinal)
                : _backendService.GetAreaAssignments(item);

            var segments = areaNames
                .Select(area =>
                {
                    var students = assignments.TryGetValue(area, out var names) ? names : [];
                    var peopleText = students.Count > 0 ? string.Join("\u3001", students) : "None";
                    return $"{area}: {peopleText}";
                })
                .ToList();

            var placeholders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["scene"] = "Duty Schedule",
                ["status"] = "Completed",
                ["date"] = today,
                ["areas"] = string.Join("\u3001", areaNames),
                ["days"] = config.AutoRunCoverageDays.ToString(),
                ["per_day"] = config.PerDay.ToString(),
                ["mode"] = applyMode,
                ["instruction"] = instruction,
                ["message"] = resultMessage,
                ["time"] = DateTime.Now.ToString("HH:mm"),
                ["assignments"] = segments.Count > 0 ? string.Join("\uFF1B", segments) : "No assignments"
            };

            var fallback = $"Duty schedule completed ({applyMode}). {resultMessage}";
            _notificationService.PublishFromTemplates(
                _backendService.GetNotificationTemplates(),
                placeholders,
                fallback,
                durationSeconds: 8);
        }
        catch (Exception ex)
        {
            DutyDiagnosticsLogger.Error("Notification", "Run completion notification failed.", ex);
        }
    }

    private static ThemePayload BuildThemePayload()
    {
        var isDark = Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
        if (isDark)
        {
            return new ThemePayload
            {
                Mode = "dark",
                Bg1 = "rgba(15, 23, 42, 0.42)",
                Bg2 = "rgba(15, 23, 42, 0.26)",
                Card = "rgba(15, 23, 42, 0.62)",
                Line = "rgba(148, 163, 184, 0.35)",
                Text = "#E2E8F0",
                Muted = "#94A3B8",
                Shadow = "0 12px 28px rgba(2, 6, 23, 0.45)",
                InputBg = "rgba(15, 23, 42, 0.52)",
                TableHead = "rgba(30, 41, 59, 0.72)"
            };
        }

        return new ThemePayload
        {
            Mode = "light",
            Bg1 = "rgba(248, 251, 255, 0.55)",
            Bg2 = "rgba(239, 246, 255, 0.45)",
            Card = "rgba(255, 255, 255, 0.82)",
            Line = "rgba(148, 163, 184, 0.35)",
            Text = "#1F2937",
            Muted = "#64748B",
            Shadow = "0 12px 28px rgba(15, 23, 42, 0.08)",
            InputBg = "rgba(255, 255, 255, 0.72)",
            TableHead = "rgba(248, 250, 252, 0.80)"
        };
    }

    private Task SendRunResultAsync(bool success, string message, string aiResponse)
    {
        return _webViewHost.PostJsonAsync(new RunResultMessage
        {
            Type = "run_result",
            Success = success,
            Message = message,
            AiResponse = aiResponse
        });
    }

    private Task SendRunStatusAsync(string phase, string message)
    {
        return _webViewHost.PostJsonAsync(new RunStatusMessage
        {
            Type = "run_status",
            Phase = phase,
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
        var testPath = Path.Combine(baseDir, "Assets_Duty", "web", "test.html");
        if (File.Exists(testPath))
        {
            return testPath;
        }

        return Path.Combine(baseDir, "Assets_Duty", "web", "index.html");
    }

    private static string TruncateForLog(string value, int maxLength)
    {
        var normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
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

    private sealed class TriggerRunCompletionNotificationRequest
    {
        [JsonPropertyName("instruction")]
        public string? Instruction { get; set; }

        [JsonPropertyName("apply_mode")]
        public string? ApplyMode { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }

    private sealed class TriggerDutyReminderNotificationRequest
    {
        [JsonPropertyName("date")]
        public string? Date { get; set; }

        [JsonPropertyName("time")]
        public string? Time { get; set; }
    }

    private sealed class BridgeSnapshot
    {
        [JsonPropertyName("local_preview_url")]
        public string LocalPreviewUrl { get; set; } = string.Empty;

        [JsonPropertyName("api_overwrite_url")]
        public string ApiOverwriteUrl { get; set; } = string.Empty;

        [JsonPropertyName("mcp_url")]
        public string McpUrl { get; set; } = string.Empty;

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

        [JsonPropertyName("ai_response")]
        public string AiResponse { get; set; } = string.Empty;
    }

    private sealed class RunStatusMessage
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "run_status";

        [JsonPropertyName("phase")]
        public string Phase { get; set; } = "started";

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

    private sealed class ThemeMessage
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "host_theme";

        [JsonPropertyName("payload")]
        public ThemePayload Payload { get; set; } = new();
    }

    private sealed class ThemePayload
    {
        [JsonPropertyName("mode")]
        public string Mode { get; set; } = "light";

        [JsonPropertyName("bg1")]
        public string Bg1 { get; set; } = "rgba(248, 251, 255, 0.55)";

        [JsonPropertyName("bg2")]
        public string Bg2 { get; set; } = "rgba(239, 246, 255, 0.45)";

        [JsonPropertyName("card")]
        public string Card { get; set; } = "rgba(255, 255, 255, 0.82)";

        [JsonPropertyName("line")]
        public string Line { get; set; } = "rgba(148, 163, 184, 0.35)";

        [JsonPropertyName("text")]
        public string Text { get; set; } = "#1F2937";

        [JsonPropertyName("muted")]
        public string Muted { get; set; } = "#64748B";

        [JsonPropertyName("shadow")]
        public string Shadow { get; set; } = "0 12px 28px rgba(15, 23, 42, 0.08)";

        [JsonPropertyName("input_bg")]
        public string InputBg { get; set; } = "rgba(255, 255, 255, 0.72)";

        [JsonPropertyName("table_head")]
        public string TableHead { get; set; } = "rgba(248, 250, 252, 0.80)";
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

        [JsonPropertyName("area_per_day_counts")]
        public Dictionary<string, int>? AreaPerDayCounts { get; set; }

        [JsonPropertyName("notification_templates")]
        public List<string>? NotificationTemplates { get; set; }

        [JsonPropertyName("duty_reminder_enabled")]
        public bool? DutyReminderEnabled { get; set; }

        [JsonPropertyName("duty_reminder_times")]
        public List<string>? DutyReminderTimes { get; set; }

        [JsonPropertyName("duty_reminder_templates")]
        public List<string>? DutyReminderTemplates { get; set; }
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
    private const int WsClipChildren = 0x02000000;
    private const int WsClipSiblings = 0x04000000;
    private const int SwShow = 5;
    private const int PostNavigationResizeDelayMs = 500;
    private const int ResizePulseIntervalMs = 200;
    private const int ResizePulseRestoreDelayMs = 20;
    private const int ResizePulseCount = 3;
    private const int ResizePulseDeltaPixels = 10;

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
    private static extern bool GetClientRect(IntPtr handle, out Win32Rect rect);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr handle, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool MoveWindow(IntPtr handle, int x, int y, int width, int height, bool repaint);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    private readonly string _entryPath;
    private IntPtr _parentHandle = IntPtr.Zero;
    private IntPtr _childHandle = IntPtr.Zero;
    private CoreWebView2Environment? _environment;
    private CoreWebView2Controller? _controller;
    private TopLevel? _topLevel;
    private bool _layoutHooksAttached;
    private bool _resizeQueued;
    private bool _isDisposed;
    private bool _parentIsTopLevelHwnd;
    private int _navigationPulseSessionId;
    private int _resizeOperationCount;
    private int _lastLoggedResizeWidth = -1;
    private int _lastLoggedResizeHeight = -1;
    private int _lastLoggedResizeX = int.MinValue;
    private int _lastLoggedResizeY = int.MinValue;

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private readonly struct PhysicalSizeInfo
    {
        public PhysicalSizeInfo(int width, int height, string source, double scale)
        {
            Width = width;
            Height = height;
            Source = source;
            Scale = scale;
        }

        public int Width { get; }
        public int Height { get; }
        public string Source { get; }
        public double Scale { get; }
    }

    private readonly struct HostPositionInfo
    {
        public HostPositionInfo(int x, int y, string source)
        {
            X = x;
            Y = y;
            Source = source;
        }

        public int X { get; }
        public int Y { get; }
        public string Source { get; }
    }

    private readonly struct HostBoundsInfo
    {
        public HostBoundsInfo(int x, int y, int width, int height, string sizeSource, string positionSource)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
            SizeSource = sizeSource;
            PositionSource = positionSource;
        }

        public int X { get; }
        public int Y { get; }
        public int Width { get; }
        public int Height { get; }
        public string SizeSource { get; }
        public string PositionSource { get; }
    }

    public event EventHandler<string>? WebMessageReceived;
    public event EventHandler? ContentReady;

    public DutyWebViewHost(string entryPath)
    {
        _entryPath = entryPath;
        DutyDiagnosticsLogger.Info("WebViewHost", "Host created.",
            new
            {
                entryPath,
                logPath = DutyDiagnosticsLogger.CurrentLogPath
            });
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        try
        {
            _parentHandle = parent.Handle;
            RefreshParentKind();

            var size = ResolvePhysicalHostSize(Bounds.Size);
            var pixelWidth = Math.Max(1, size.Width);
            var pixelHeight = Math.Max(1, size.Height);
            _childHandle = CreateWindowExW(
                0,
                "Static",
                string.Empty,
                WsChild | WsClipChildren | WsClipSiblings,
                0,
                0,
                pixelWidth,
                pixelHeight,
                parent.Handle,
                IntPtr.Zero,
                GetModuleHandle(null),
                IntPtr.Zero);

            DutyDiagnosticsLogger.Info("WebViewHost", "Native host window created.",
                new
                {
                    parentHandle = FormatHandle(_parentHandle),
                    childHandle = FormatHandle(_childHandle),
                    logicalWidth = Math.Round(Bounds.Width, 2),
                    logicalHeight = Math.Round(Bounds.Height, 2),
                    pixelWidth,
                    pixelHeight,
                    source = size.Source,
                    scale = Math.Round(size.Scale, 4),
                    parentIsTopLevel = _parentIsTopLevelHwnd
                });
            _ = InitializeWebView2Async();
            return new PlatformHandle(_childHandle, "HWND");
        }
        catch (Exception ex)
        {
            DutyDiagnosticsLogger.Error("WebViewHost", "CreateNativeControlCore failed.", ex,
                new
                {
                    parentHandle = FormatHandle(parent.Handle),
                    logicalWidth = Math.Round(Bounds.Width, 2),
                    logicalHeight = Math.Round(Bounds.Height, 2)
                });
            return base.CreateNativeControlCore(parent);
        }
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        Dispose();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        AttachLayoutHooks();
        QueueResizeSync(3);
        DutyDiagnosticsLogger.Info("WebViewHost", "Attached to visual tree.");
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        DetachLayoutHooks();
        DutyDiagnosticsLogger.Info("WebViewHost", "Detached from visual tree.");
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        ResizeNativeHost(e.NewSize);
        QueueResizeSync(1);
        DutyDiagnosticsLogger.Info("WebViewHost", "OnSizeChanged received.",
            new
            {
                width = Math.Round(e.NewSize.Width, 2),
                height = Math.Round(e.NewSize.Height, 2)
            });
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

    public void RequestResizeSync()
    {
        QueueResizeSync(2);
    }

    private async Task InitializeWebView2Async()
    {
        if (!File.Exists(_entryPath))
        {
            DutyDiagnosticsLogger.Error("WebViewHost", "Entry page not found.", null,
                new { entryPath = _entryPath });
            return;
        }

        try
        {
            var userDataPath = Path.Combine(Path.GetTempPath(), "DutyAgent.WebView2");
            Directory.CreateDirectory(userDataPath);
            DutyDiagnosticsLogger.Info("WebViewHost", "Initializing WebView2.",
                new
                {
                    entryPath = _entryPath,
                    userDataPath,
                    childHandle = FormatHandle(_childHandle)
                });

            _environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataPath);
            _controller = await _environment.CreateCoreWebView2ControllerAsync(_childHandle);
            if (_controller == null)
            {
                DutyDiagnosticsLogger.Warn("WebViewHost", "CreateCoreWebView2ControllerAsync returned null.");
                return;
            }

            _controller.DefaultBackgroundColor = System.Drawing.Color.FromArgb(246, 249, 255);
            _controller.CoreWebView2.Settings.IsWebMessageEnabled = true;
            _controller.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _controller.CoreWebView2.WebMessageReceived += OnCoreWebMessageReceived;
            _controller.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            _controller.IsVisible = false;
            DutyDiagnosticsLogger.Info("WebViewHost", "WebView2 controller initialized.");

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ResizeNativeHost(Bounds.Size);
                var uri = new Uri(_entryPath).AbsoluteUri;
                DutyDiagnosticsLogger.Info("WebViewHost", "Navigating webview.",
                    new { uri });
                _controller.CoreWebView2.Navigate(uri);
                QueueResizeSync(2);
            });
        }
        catch (Exception ex)
        {
            DutyDiagnosticsLogger.Error("WebViewHost", "InitializeWebView2Async failed.", ex);
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        DutyDiagnosticsLogger.Info("WebViewHost", "Navigation completed.",
            new
            {
                success = e.IsSuccess,
                webError = e.WebErrorStatus.ToString()
            });
        Dispatcher.UIThread.Post(() =>
        {
            if (_isDisposed || _controller == null)
            {
                return;
            }

            ResizeNativeHost(Bounds.Size);
            if (_childHandle != IntPtr.Zero)
            {
                ShowWindow(_childHandle, SwShow);
            }

            _controller.IsVisible = true;
            ContentReady?.Invoke(this, EventArgs.Empty);
            QueueResizeSync(2);
            var pulseSessionId = Interlocked.Increment(ref _navigationPulseSessionId);
            _ = RunDelayedResizePulseLoopAsync(pulseSessionId);
        }, DispatcherPriority.Render);
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
            DutyDiagnosticsLogger.Error("WebViewHost", "CoreWebMessageReceived parse failed.", ex);
        }
    }

    private void ResizeNativeHost(Size newSize)
    {
        if (_childHandle == IntPtr.Zero || _isDisposed)
        {
            return;
        }

        _resizeOperationCount++;
        var size = ResolvePhysicalHostSize(newSize);
        var width = size.Width;
        var height = size.Height;
        var position = ResolveHostPosition();
        var x = position.X;
        var y = position.Y;

        if (width <= 0 || height <= 0)
        {
            DutyDiagnosticsLogger.Warn("WebViewHost", "Skipped resize because resolved size is invalid.",
                new
                {
                    logicalWidth = Math.Round(newSize.Width, 2),
                    logicalHeight = Math.Round(newSize.Height, 2),
                    width,
                    height,
                    source = size.Source,
                    scale = Math.Round(size.Scale, 4),
                    x,
                    y,
                    positionSource = position.Source
                });
            return;
        }

        var oldBounds = _controller?.Bounds;
        var moveOk = MoveWindow(_childHandle, x, y, width, height, true);
        if (!moveOk)
        {
            DutyDiagnosticsLogger.Warn("WebViewHost", "MoveWindow returned false.",
                new
                {
                    win32 = Marshal.GetLastWin32Error(),
                    x,
                    y,
                    width,
                    height,
                    source = size.Source,
                    positionSource = position.Source
                });
        }

        if (_controller != null)
        {
            var bounds = _controller.Bounds;
            if (bounds.X != x || bounds.Y != y || bounds.Width != width || bounds.Height != height)
            {
                _controller.Bounds = new System.Drawing.Rectangle(x, y, width, height);
            }
        }

        var shouldLog = width != _lastLoggedResizeWidth
            || height != _lastLoggedResizeHeight
            || x != _lastLoggedResizeX
            || y != _lastLoggedResizeY
            || _resizeOperationCount % 50 == 0;
        if (shouldLog)
        {
            _lastLoggedResizeWidth = width;
            _lastLoggedResizeHeight = height;
            _lastLoggedResizeX = x;
            _lastLoggedResizeY = y;
            DutyDiagnosticsLogger.Info("WebViewHost", "Resize applied.",
                new
                {
                    call = _resizeOperationCount,
                    logicalWidth = Math.Round(newSize.Width, 2),
                    logicalHeight = Math.Round(newSize.Height, 2),
                    x,
                    y,
                    width,
                    height,
                    source = size.Source,
                    scale = Math.Round(size.Scale, 4),
                    positionSource = position.Source,
                    parentIsTopLevel = _parentIsTopLevelHwnd,
                    oldControllerBounds = oldBounds is null ? null : new
                    {
                        oldBounds.Value.X,
                        oldBounds.Value.Y,
                        oldBounds.Value.Width,
                        oldBounds.Value.Height
                    }
                });
        }
    }

    private void DispatchWebResizeEvent()
    {
        if (_isDisposed || _controller?.CoreWebView2 == null)
        {
            return;
        }

        const string script = "window.dispatchEvent(new Event('resize'));";
        var task = _controller.CoreWebView2.ExecuteScriptAsync(script);
        _ = task.ContinueWith(t =>
        {
            if (_isDisposed)
            {
                return;
            }

            if (t.IsFaulted)
            {
                var ex = t.Exception?.GetBaseException();
                DutyDiagnosticsLogger.Error("WebViewHost", "Dispatch resize script failed.", ex);
                return;
            }

            var result = t.Result ?? string.Empty;
            DutyDiagnosticsLogger.Info("WebViewHost", "Dispatched resize script.",
                new
                {
                    result = TruncateLogValue(result, 120)
                });
        }, TaskScheduler.Default);
    }

    private async Task RunDelayedResizePulseLoopAsync(int pulseSessionId)
    {
        DutyDiagnosticsLogger.Info("WebViewHost", "Starting delayed resize pulse loop.",
            new
            {
                pulseSessionId,
                delayMs = PostNavigationResizeDelayMs,
                intervalMs = ResizePulseIntervalMs,
                pulseCount = ResizePulseCount,
                deltaPixels = ResizePulseDeltaPixels
            });
        try
        {
            await Task.Delay(PostNavigationResizeDelayMs);
            for (var iteration = 1; iteration <= ResizePulseCount; iteration++)
            {
                if (ShouldStopPulseLoop(pulseSessionId))
                {
                    DutyDiagnosticsLogger.Info("WebViewHost", "Stopped delayed resize pulse loop.",
                        new { pulseSessionId, iteration });
                    return;
                }

                var pulseBounds = await Dispatcher.UIThread.InvokeAsync(
                    () => CaptureAndApplyPhysicalPulse(iteration),
                    DispatcherPriority.Background);
                if (pulseBounds == null)
                {
                    DutyDiagnosticsLogger.Warn("WebViewHost", "Skipped delayed pulse because host bounds are unavailable.",
                        new { pulseSessionId, iteration });
                    return;
                }

                await Task.Delay(ResizePulseRestoreDelayMs);
                if (ShouldStopPulseLoop(pulseSessionId))
                {
                    return;
                }

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    RestorePhysicalPulse(pulseBounds.Value, iteration);
                    QueueResizeSync(1);
                    DispatchWebResizeEvent();
                }, DispatcherPriority.Background);

                if (iteration < ResizePulseCount)
                {
                    await Task.Delay(ResizePulseIntervalMs);
                }
            }

            DutyDiagnosticsLogger.Info("WebViewHost", "Completed delayed resize pulse loop.",
                new
                {
                    pulseSessionId,
                    pulseCount = ResizePulseCount
                });
        }
        catch (Exception ex)
        {
            DutyDiagnosticsLogger.Error("WebViewHost", "Delayed resize pulse loop failed.", ex,
                new { pulseSessionId });
        }
    }

    private bool ShouldStopPulseLoop(int pulseSessionId)
    {
        return _isDisposed || _controller == null || pulseSessionId != _navigationPulseSessionId;
    }

    private HostBoundsInfo? CaptureAndApplyPhysicalPulse(int iteration)
    {
        if (!TryGetHostBounds(out var bounds))
        {
            return null;
        }

        var delta = Math.Max(1, Math.Abs(ResizePulseDeltaPixels));
        var pulseWidth = bounds.Width + delta;
        var pulseHeight = bounds.Height + delta;

        var moveOk = MoveWindow(_childHandle, bounds.X, bounds.Y, pulseWidth, pulseHeight, true);
        if (!moveOk)
        {
            DutyDiagnosticsLogger.Warn("WebViewHost", "MoveWindow failed during pulse expand.",
                new
                {
                    iteration,
                    win32 = Marshal.GetLastWin32Error(),
                    x = bounds.X,
                    y = bounds.Y,
                    pulseWidth,
                    pulseHeight
                });
        }

        if (_controller != null)
        {
            _controller.Bounds = new System.Drawing.Rectangle(bounds.X, bounds.Y, pulseWidth, pulseHeight);
        }

        DutyDiagnosticsLogger.Info("WebViewHost", "Applied physical resize pulse.",
            new
            {
                iteration,
                x = bounds.X,
                y = bounds.Y,
                width = bounds.Width,
                height = bounds.Height,
                pulseWidth,
                pulseHeight,
                deltaPixels = delta,
                sizeSource = bounds.SizeSource,
                positionSource = bounds.PositionSource
            });
        return bounds;
    }

    private void RestorePhysicalPulse(HostBoundsInfo bounds, int iteration)
    {
        if (_isDisposed || _controller == null || _childHandle == IntPtr.Zero)
        {
            return;
        }

        var moveOk = MoveWindow(_childHandle, bounds.X, bounds.Y, bounds.Width, bounds.Height, true);
        if (!moveOk)
        {
            DutyDiagnosticsLogger.Warn("WebViewHost", "MoveWindow failed during pulse restore.",
                new
                {
                    iteration,
                    win32 = Marshal.GetLastWin32Error(),
                    x = bounds.X,
                    y = bounds.Y,
                    width = bounds.Width,
                    height = bounds.Height
                });
        }

        _controller.Bounds = new System.Drawing.Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        DutyDiagnosticsLogger.Info("WebViewHost", "Restored physical resize pulse.",
            new
            {
                iteration,
                x = bounds.X,
                y = bounds.Y,
                width = bounds.Width,
                height = bounds.Height
            });
    }

    private bool TryGetHostBounds(out HostBoundsInfo bounds)
    {
        bounds = default;
        if (_isDisposed || _controller == null || _childHandle == IntPtr.Zero)
        {
            return false;
        }

        var size = ResolvePhysicalHostSize(Bounds.Size);
        if (size.Width <= 0 || size.Height <= 0)
        {
            return false;
        }

        var position = ResolveHostPosition();
        bounds = new HostBoundsInfo(position.X, position.Y, size.Width, size.Height, size.Source, position.Source);
        return true;
    }

    private void QueueResizeSync(int followUpPasses)
    {
        if (_isDisposed)
        {
            return;
        }

        if (!_resizeQueued)
        {
            _resizeQueued = true;
            Dispatcher.UIThread.Post(() =>
            {
                _resizeQueued = false;
                ResizeNativeHost(Bounds.Size);
            }, DispatcherPriority.Render);
        }

        if (followUpPasses > 0)
        {
            QueueInitialResizePasses(followUpPasses);
        }
    }

    private void AttachLayoutHooks()
    {
        if (_layoutHooksAttached || _isDisposed)
        {
            return;
        }

        _layoutHooksAttached = true;
        _topLevel = TopLevel.GetTopLevel(this);
        RefreshParentKind();
        if (_topLevel != null)
        {
            _topLevel.PropertyChanged += OnTopLevelPropertyChanged;
        }
    }

    private void DetachLayoutHooks()
    {
        if (!_layoutHooksAttached)
        {
            return;
        }

        _layoutHooksAttached = false;

        if (_topLevel != null)
        {
            _topLevel.PropertyChanged -= OnTopLevelPropertyChanged;
            _topLevel = null;
        }
    }

    private void OnTopLevelPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        var propertyName = e.Property?.Name ?? "unknown";
        if (!IsLayoutRelatedTopLevelProperty(propertyName))
        {
            return;
        }

        DutyDiagnosticsLogger.Info("WebViewHost", "TopLevel layout property changed.",
            new
            {
                property = propertyName
            });
        RefreshParentKind();
        QueueResizeSync(2);
    }

    private void QueueInitialResizePasses(int remaining)
    {
        if (remaining <= 0 || _isDisposed)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_isDisposed)
            {
                return;
            }

            ResizeNativeHost(Bounds.Size);
            QueueInitialResizePasses(remaining - 1);
        }, DispatcherPriority.Background);
    }

    private void RefreshParentKind()
    {
        var topLevelHandle = TopLevel.GetTopLevel(this)?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        _parentIsTopLevelHwnd = _parentHandle != IntPtr.Zero
            && topLevelHandle != IntPtr.Zero
            && _parentHandle == topLevelHandle;
    }

    private static bool IsLayoutRelatedTopLevelProperty(string propertyName)
    {
        return propertyName.Equals("ClientSize", StringComparison.Ordinal)
            || propertyName.Equals("WindowState", StringComparison.Ordinal)
            || propertyName.Equals("Bounds", StringComparison.Ordinal)
            || propertyName.Equals("RenderScaling", StringComparison.Ordinal)
            || propertyName.Equals("Position", StringComparison.Ordinal);
    }

    private PhysicalSizeInfo ResolvePhysicalHostSize(Size logicalSize)
    {
        var scale = GetPhysicalScaleFactor();
        var width = (int)Math.Round(Math.Max(0, logicalSize.Width) * scale);
        var height = (int)Math.Round(Math.Max(0, logicalSize.Height) * scale);
        if (width > 0 && height > 0)
        {
            return new PhysicalSizeInfo(width, height, "logical_dpi", scale);
        }

        if (_parentHandle != IntPtr.Zero && GetClientRect(_parentHandle, out var parentRect))
        {
            var parentWidth = parentRect.Right - parentRect.Left;
            var parentHeight = parentRect.Bottom - parentRect.Top;
            if (parentWidth > 0 && parentHeight > 0)
            {
                return new PhysicalSizeInfo(parentWidth, parentHeight, "parent_client_rect_fallback", 0);
            }
        }

        return new PhysicalSizeInfo(width, height, "dpi_scale", scale);
    }

    private HostPositionInfo ResolveHostPosition()
    {
        if (!_parentIsTopLevelHwnd)
        {
            return new HostPositionInfo(0, 0, "local_parent");
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
        {
            return new HostPositionInfo(0, 0, "top_level_unavailable");
        }

        var logicalPoint = this.TranslatePoint(new Point(0, 0), topLevel);
        if (logicalPoint == null)
        {
            return new HostPositionInfo(0, 0, "translate_point_unavailable");
        }

        var scale = GetPhysicalScaleFactor();
        var x = (int)Math.Round(logicalPoint.Value.X * scale);
        var y = (int)Math.Round(logicalPoint.Value.Y * scale);
        return new HostPositionInfo(x, y, "translated_from_top_level");
    }

    private double GetPhysicalScaleFactor()
    {
        var parentScale = TryGetWindowScale(_parentHandle);
        if (parentScale > 0)
        {
            return parentScale;
        }

        var childScale = TryGetWindowScale(_childHandle);
        if (childScale > 0)
        {
            return childScale;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.RenderScaling is > 0)
        {
            return topLevel.RenderScaling;
        }

        return 1.0;
    }

    private static double TryGetWindowScale(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return 0;
        }

        try
        {
            var dpi = GetDpiForWindow(handle);
            return dpi > 0 ? dpi / 96.0 : 0;
        }
        catch (EntryPointNotFoundException)
        {
            return 0;
        }
    }

    private static string TruncateLogValue(string value, int maxLength)
    {
        var normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    private static string FormatHandle(IntPtr handle)
    {
        return handle == IntPtr.Zero ? "0x0" : $"0x{unchecked((ulong)handle.ToInt64()):X}";
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        Interlocked.Increment(ref _navigationPulseSessionId);
        DetachLayoutHooks();
        if (_controller != null)
        {
            _controller.CoreWebView2.WebMessageReceived -= OnCoreWebMessageReceived;
            _controller.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
            _controller.Close();
            _controller = null;
        }

        _environment = null;

        if (_childHandle != IntPtr.Zero)
        {
            DestroyWindow(_childHandle);
            _childHandle = IntPtr.Zero;
        }

        _parentHandle = IntPtr.Zero;

        GC.SuppressFinalize(this);
    }
}

