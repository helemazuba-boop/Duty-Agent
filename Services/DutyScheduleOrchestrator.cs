using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using DutyAgent.Models;
using Timer = System.Timers.Timer;
using System.Text;

namespace DutyAgent.Services;

public class DutyScheduleOrchestrator : IDisposable
{
    private const string AutoRunInstruction = "Please generate duty schedule automatically based on roster.csv.";
    private const string ApiKeyMask = "********";

    private readonly IConfigManager _configManager;
    private readonly IStateAndRosterManager _stateManager;
    private readonly IPythonIpcService _ipcService;
    private readonly DutyNotificationService _notificationService;
    private readonly DutyAutomationBridgeService _automationBridge;
    
    private readonly Timer _debounceTimer;
    private readonly Timer _autoRunTimer;
    private readonly Timer _currentDutyBoundaryTimer;
    private readonly SemaphoreSlim _runCoreGate = new(1, 1);
    private readonly object _dutyReminderLock = new();
    private readonly object _currentDutyBoundaryLock = new();
    private readonly Dictionary<string, string> _publishedDutyReminderSignatures = new(StringComparer.Ordinal);
    private readonly DutyPluginPaths _pluginPaths;
    private bool _runtimeStarted;
    private bool _pendingAutomationStateChange;

    public event EventHandler? ScheduleUpdated;
    public DutyConfig Config => _configManager.Config;

    private const string DefaultAreaClassroom = "\u6559\u5BA4";
    private const string DefaultAreaCleaning = "\u6E05\u6D01\u533A";
    private const string DefaultDutyReminderTime = "07:40";
    
    private static readonly Dictionary<string, DayOfWeek> AutoRunDayAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        { "\u5468\u4E00", DayOfWeek.Monday },
        { "\u5468\u4E8C", DayOfWeek.Tuesday },
        { "\u5468\u4E09", DayOfWeek.Wednesday },
        { "\u5468\u56DB", DayOfWeek.Thursday },
        { "\u5468\u4E94", DayOfWeek.Friday },
        { "\u5468\u516D", DayOfWeek.Saturday },
        { "\u5468\u65E5", DayOfWeek.Sunday },
        { "Monday", DayOfWeek.Monday },
        { "Tuesday", DayOfWeek.Tuesday },
        { "Wednesday", DayOfWeek.Wednesday },
        { "Thursday", DayOfWeek.Thursday },
        { "Friday", DayOfWeek.Friday },
        { "Saturday", DayOfWeek.Saturday },
        { "Sunday", DayOfWeek.Sunday }
    };

    public DutyScheduleOrchestrator(
        IConfigManager configManager,
        IStateAndRosterManager stateManager,
        IPythonIpcService ipcService,
        DutyNotificationService notificationService,
        DutyAutomationBridgeService automationBridge,
        DutyPluginPaths pluginPaths)
    {
        _configManager = configManager;
        _stateManager = stateManager;
        _ipcService = ipcService;
        _notificationService = notificationService;
        _automationBridge = automationBridge;
        _pluginPaths = pluginPaths;

        _stateManager.StateChanged += (_, _) =>
        {
            DebounceUpdateNotification(notifyAutomationBridge: true);
            TryPublishDutyReminderNotifications(DateTime.Now, allowRepublishIfChanged: true);
        };
        _configManager.ConfigChanged += (_, _) =>
        {
            DebounceUpdateNotification();
            ScheduleCurrentDutyBoundaryRefresh();
        };

        _debounceTimer = new Timer(500) { AutoReset = false };
        _debounceTimer.Elapsed += (_, _) =>
        {
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_pendingAutomationStateChange)
                {
                    _pendingAutomationStateChange = false;
                    _automationBridge.PublishScheduleStateChanged("schedule-state-updated");
                }

                ScheduleUpdated?.Invoke(this, EventArgs.Empty);
            });
        };

        _autoRunTimer = new Timer(60_000) { AutoReset = true };
        _autoRunTimer.Elapsed += (_, _) => TryRunAutoSchedule();

        _currentDutyBoundaryTimer = new Timer { AutoReset = false };
        _currentDutyBoundaryTimer.Elapsed += (_, _) =>
        {
            DebounceUpdateNotification();
            ScheduleCurrentDutyBoundaryRefresh();
        };
    }

    private void DebounceUpdateNotification(bool notifyAutomationBridge = false)
    {
        if (notifyAutomationBridge)
        {
            _pendingAutomationStateChange = true;
        }

        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    // Proxy load/save for backward compatibility with existing view models
    public void LoadConfig() => _ = _configManager.Config;
    public void SaveConfig() => _configManager.SaveConfig();
    public DutyConfig UpdateHostConfig(Action<DutyConfig> update) => _configManager.UpdateConfig(update);
    public async Task<string> GetWebAppUrlAsync(CancellationToken cancellationToken = default)
    {
        await _ipcService.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(_ipcService.WebAppUrl))
        {
            throw new InvalidOperationException("Backend web app URL is unavailable.");
        }

        return _ipcService.WebAppUrl;
    }

    public async Task<DutyBackendSnapshot> LoadBackendSnapshotAsync(
        string requestSource = "host_settings",
        string? traceId = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveTraceId = string.IsNullOrWhiteSpace(traceId)
            ? DutyDiagnosticsLogger.CreateTraceId("snapshot")
            : traceId.Trim();
        var stopwatch = Stopwatch.StartNew();
        DutyDiagnosticsLogger.Info("SettingsBackend", "Loading backend snapshot.",
            new { traceId = effectiveTraceId, requestSource });
        try
        {
            var snapshot = await _ipcService.GetBackendSnapshotAsync(requestSource, effectiveTraceId, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            DutyDiagnosticsLogger.Info("SettingsBackend", "Backend snapshot loaded.",
                new
                {
                    traceId = effectiveTraceId,
                    requestSource,
                    durationMs = stopwatch.ElapsedMilliseconds,
                    rosterCount = snapshot.Roster.Count,
                    scheduleCount = snapshot.State.SchedulePool.Count
                });
            return snapshot;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            DutyDiagnosticsLogger.Error("SettingsBackend", "Failed to load backend snapshot.", ex,
                new { traceId = effectiveTraceId, requestSource, durationMs = stopwatch.ElapsedMilliseconds });
            throw;
        }
    }

    public DutyState LoadState() => _stateManager.LoadState();

    public bool RunCoreAgent(string instruction)
    {
        var result = RunCoreAgentWithMessage(instruction);
        return result.Success;
    }

    public CoreRunResult RunCoreAgentWithMessage(
        string instruction,
        Action<CoreRunProgress>? progress = null,
        bool isAutoRun = false)
    {
        var t = RunCoreAgentAsync(instruction, progress, isAutoRun);
        t.Wait();
        return t.Result;
    }

    public async Task<CoreRunResult> RunCoreAgentAsync(
        string instruction,
        Action<CoreRunProgress>? progress = null,
        bool isAutoRun = false)
    {
        var effectiveInstruction = string.IsNullOrWhiteSpace(instruction) ? AutoRunInstruction : instruction.Trim();

        if (!_runCoreGate.Wait(0))
        {
            return CoreRunResult.Fail("Another schedule run is already in progress.", code: "busy");
        }

        try
        {
            var inputData = new
            {
                instruction = effectiveInstruction,
                request_source = isAutoRun ? "automation" : "host"
            };

            var result = await _ipcService.RunScheduleAsync(inputData, progress, CancellationToken.None).ConfigureAwait(false);
            PublishAutomationRunResult(effectiveInstruction, result, isAutoRun);
            return result;
        }
        catch (Exception ex)
        {
            var result = CoreRunResult.Fail($"Execution error: {ex.Message}");
            PublishAutomationRunResult(effectiveInstruction, result, isAutoRun);
            return result;
        }
        finally
        {
            _runCoreGate.Release();
        }
    }

    public static string ResolveApiKeyInput(string? incomingApiKey, string existingApiKey)
    {
        if (incomingApiKey is null) return existingApiKey;
        var trimmed = incomingApiKey.Trim();
        if (trimmed.Length == 0) return string.Empty;
        if (string.Equals(trimmed, ApiKeyMask, StringComparison.Ordinal)) return existingApiKey;
        return trimmed;
    }

    private static string TruncateForLog(string? value, int maxLength)
    {
        var normalized = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    public EngineState EngineStatus => _ipcService.State;
    public string? EngineLastError => _ipcService.LastErrorMessage;

    public async Task RestartAIEngineAsync()
    {
        await _ipcService.RestartEngineAsync().ConfigureAwait(false);
    }

    public void StartRuntime()
    {
        if (_runtimeStarted)
        {
            return;
        }

        _runtimeStarted = true;
        _autoRunTimer.Start();
        ScheduleCurrentDutyBoundaryRefresh();
    }

    public void StopRuntime()
    {
        if (!_runtimeStarted)
        {
            return;
        }

        _runtimeStarted = false;
        _autoRunTimer.Stop();
        _currentDutyBoundaryTimer.Stop();
        _debounceTimer.Stop();
    }

    public static string ValidatePythonPath(string configuredPath, string pluginBasePath, string assetsBasePath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var absolutePath = Path.IsPathRooted(configuredPath) ? configuredPath : Path.Combine(pluginBasePath, configuredPath);
            if (File.Exists(absolutePath)) return absolutePath;
        }

        var expectedPath = Path.Combine(assetsBasePath, "python-embed", "python.exe");
        if (File.Exists(expectedPath)) return expectedPath;

        return "python"; // Fallback to system python
    }

    public static string NormalizeTimeOrThrow(string? time)
    {
        if (TimeSpan.TryParse(time, out var ts)) return ts.ToString(@"hh\:mm");
        throw new ArgumentException("Invalid time format.");
    }

    public static string NormalizeAutoRunMode(string mode)
    {
        var trimmed = (mode ?? "Off").Trim();
        return trimmed.ToLowerInvariant() switch
        {
            "weekly" => "Weekly",
            "monthly" => "Monthly",
            "custom" => "Custom",
            _ => "Off"
        };
    }

    public static string NormalizeModelProfile(string? modelProfile)
    {
        var trimmed = (modelProfile ?? "auto").Trim();
        return trimmed.ToLowerInvariant() switch
        {
            "cloud" or "cloud_general" => "cloud",
            "campus" or "campus_small" or "school_small" => "campus_small",
            "edge" or "edge_tuned" or "edge_finetuned" => "edge",
            "custom" => "custom",
            _ => "auto"
        };
    }

    public static string NormalizeOrchestrationMode(string? orchestrationMode)
    {
        var trimmed = (orchestrationMode ?? "auto").Trim();
        return trimmed.ToLowerInvariant() switch
        {
            "single" or "single_pass" or "unified" => "single_pass",
            "multi_agent" or "multi-agent" or "staged" => "multi_agent",
            _ => "auto"
        };
    }

    public static string NormalizeMultiAgentExecutionMode(string? executionMode)
    {
        var trimmed = (executionMode ?? "auto").Trim();
        return trimmed.ToLowerInvariant() switch
        {
            "parallel" => "parallel",
            "serial" or "sequential" => "serial",
            _ => "auto"
        };
    }

    private static List<DutyPlanPreset> ClonePlanPresets(IEnumerable<DutyPlanPreset>? planPresets)
    {
        return (planPresets ?? [])
            .Select(plan => new DutyPlanPreset
            {
                Id = plan.Id,
                Name = plan.Name,
                ModeId = plan.ModeId,
                ApiKey = plan.ApiKey,
                BaseUrl = plan.BaseUrl,
                Model = plan.Model,
                ModelProfile = plan.ModelProfile,
                ProviderHint = plan.ProviderHint,
                MultiAgentExecutionMode = plan.MultiAgentExecutionMode
            })
            .ToList();
    }

    private static string NormalizeTimeOrDefault(string? value, string fallback)
    {
        if (TimeSpan.TryParse(value, out var parsed))
        {
            return $"{parsed.Hours:D2}:{parsed.Minutes:D2}";
        }

        return fallback;
    }

    private void TryRunAutoSchedule()
    {
        try
        {
            LoadConfig();
            var now = DateTime.Now;

            var mode = (Config.AutoRunMode ?? "Off").Trim();
            if (!string.Equals(mode, "Off", StringComparison.OrdinalIgnoreCase))
            {
                var today = now.ToString("yyyy-MM-dd");
                if (!string.Equals(Config.LastAutoRunDate, today, StringComparison.Ordinal) &&
                    IsAutoRunTriggered(mode, Config.AutoRunParameter, Config.LastAutoRunDate, now) &&
                    TimeSpan.TryParse(Config.AutoRunTime, out var targetTime) &&
                    now.TimeOfDay >= targetTime &&
                    _runCoreGate.CurrentCount != 0)
                {
                    PublishAutoRunTriggeredNotification(now);
                    var result = RunCoreAgentWithMessage(AutoRunInstruction, isAutoRun: true);
                    if (!string.Equals(result.Code, "busy", StringComparison.Ordinal))
                    {
                        PublishRunCompletionNotification(
                            instruction: AutoRunInstruction,
                            resultMessage: result.Message,
                            success: result.Success,
                            isAutoRun: true);

                        UpdateHostConfig(config =>
                        {
                            config.LastAutoRunDate = today;
                            config.AiConsecutiveFailures = 0;
                        });
                    }
                }
            }

            TryPublishDutyReminderNotifications(now);
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        StopRuntime();
        _debounceTimer.Dispose();
        _autoRunTimer.Dispose();
        _currentDutyBoundaryTimer.Dispose();
        _runCoreGate.Dispose();
    }

    public string GetRosterPath() => _pluginPaths.RosterPath;

    public DateTime GetCurrentScheduleDate(DateTime? now = null)
    {
        LoadConfig();
        var current = now ?? DateTime.Now;
        var targetDate = current.Date;

        if (TimeSpan.TryParse(Config.ComponentRefreshTime, out var refreshTime) &&
            current.TimeOfDay >= refreshTime)
        {
            targetDate = targetDate.AddDays(1);
        }

        return targetDate;
    }

    public SchedulePoolItem? GetScheduleItem(string? dateText)
    {
        var normalizedDate = (dateText ?? string.Empty).Trim();
        if (normalizedDate.Length == 0)
        {
            return null;
        }

        return LoadState().SchedulePool.LastOrDefault(x =>
            string.Equals(x.Date, normalizedDate, StringComparison.Ordinal));
    }

    public SchedulePoolItem? GetCurrentScheduleItem(DateTime? now = null)
    {
        return GetScheduleItem(GetCurrentScheduleDate(now).ToString("yyyy-MM-dd"));
    }

    public List<string> GetAreaNames()
    {
        var state = LoadState();
        return InferAreaNamesFromState(state);
    }

    public List<string> GetDutyReminderTimes()
    {
        lock (_dutyReminderLock)
        {
            return NormalizeDutyReminderTimes(Config.DutyReminderTimes);
        }
    }

    public Dictionary<string, List<string>> GetAreaAssignments(SchedulePoolItem item)
    {
        return BuildAreaAssignments(item);
    }

    public List<RosterEntry> LoadRosterEntries()
    {
        var path = GetRosterPath();
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return RosterWorkbookHelper.LoadCsv(path);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"LoadRosterEntries Error: {ex.Message}");
            return [];
        }
    }

    public void SaveRosterEntries(IEnumerable<RosterEntry>? rosterEntries)
    {
        var path = GetRosterPath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var normalized = new List<RosterEntry>();
        var nameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var usedIds = new HashSet<int>();
        var nextGeneratedId = 1;
        foreach (var entry in rosterEntries ?? [])
        {
            var baseName = (entry.Name ?? string.Empty).Trim();
            if (baseName.Length == 0)
            {
                continue;
            }

            var id = entry.Id;
            if (id <= 0 || !usedIds.Add(id))
            {
                while (usedIds.Contains(nextGeneratedId))
                {
                    nextGeneratedId++;
                }

                id = nextGeneratedId;
                usedIds.Add(id);
            }

            if (id >= nextGeneratedId)
            {
                nextGeneratedId = id + 1;
            }

            var uniqueName = ToUniqueRosterName(baseName, nameCounts);
            normalized.Add(new RosterEntry
            {
                Id = id,
                Name = uniqueName,
                Active = entry.Active
            });
        }

        normalized = normalized
            .OrderBy(x => x.Id)
            .ToList();

        var builder = new StringBuilder();
        builder.AppendLine("id,name,active");
        foreach (var item in normalized)
        {
            builder.Append(item.Id);
            builder.Append(',');
            builder.Append(EscapeCsv(item.Name));
            builder.Append(',');
            builder.AppendLine(item.Active ? "1" : "0");
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }



    public async Task<DutyScheduleEntrySaveResponse> SaveScheduleEntryAsync(
        string? sourceDate,
        string? targetDate,
        string? day,
        IDictionary<string, List<string>>? areaAssignments,
        string? note,
        bool confirmOverwrite,
        bool recordDebtCreditChanges,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeScheduleDate(targetDate, out var normalizedTargetDate, out var parsedDate))
        {
            throw new ArgumentException("Date format is invalid.", nameof(targetDate));
        }

        var normalizedDay = NormalizeScheduleDay(day, parsedDate.DayOfWeek);
        var request = new DutyScheduleEntrySaveRequest
        {
            SourceDate = string.IsNullOrWhiteSpace(sourceDate) ? null : sourceDate.Trim(),
            TargetDate = normalizedTargetDate,
            Day = normalizedDay,
            AreaAssignments = NormalizeAreaAssignmentsForState(areaAssignments),
            Note = (note ?? string.Empty).Trim(),
            ConfirmOverwrite = confirmOverwrite,
            LedgerMode = recordDebtCreditChanges ? "record" : "skip"
        };

        return await _ipcService.SaveScheduleEntryAsync(
            request,
            requestSource: "host_schedule_editor",
            traceId: DutyDiagnosticsLogger.CreateTraceId("schedule-edit"),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }







    private static Dictionary<string, List<string>> BuildAreaAssignments(SchedulePoolItem item)
    {
        var assignments = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var (area, students) in item.AreaAssignments)
        {
            var areaName = (area ?? string.Empty).Trim();
            if (areaName.Length == 0)
            {
                continue;
            }

            var normalizedStudents = NormalizeStudents(students);
            if (normalizedStudents.Count > 0)
            {
                assignments[areaName] = normalizedStudents;
            }
        }

        return assignments;
    }

    private static List<string> InferAreaNamesFromState(DutyState state)
    {
        var areas = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in state.SchedulePool)
        {
            if (item.AreaAssignments == null)
            {
                continue;
            }

            foreach (var area in item.AreaAssignments.Keys)
            {
                var name = (area ?? string.Empty).Trim();
                if (name.Length == 0 || !seen.Add(name))
                {
                    continue;
                }

                areas.Add(name);
            }
        }

        if (areas.Count == 0)
        {
            areas.Add(DefaultAreaClassroom);
            areas.Add(DefaultAreaCleaning);
        }

        return areas;
    }

    private static List<string> NormalizeStudents(IEnumerable<string>? rawStudents)
    {
        var students = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (rawStudents == null)
        {
            return students;
        }

        foreach (var raw in rawStudents)
        {
            var name = (raw ?? string.Empty).Trim();
            if (name.Length == 0 || !seen.Add(name))
            {
                continue;
            }

            students.Add(name);
        }

        return students;
    }

    private static List<string> NormalizeAreaNames(IEnumerable<string>? rawAreas)
    {
        var areas = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (rawAreas != null)
        {
            foreach (var raw in rawAreas)
            {
                var area = (raw ?? string.Empty).Trim();
                if (area.Length == 0 || !seen.Add(area))
                {
                    continue;
                }

                areas.Add(area);
            }
        }

        if (areas.Count == 0)
        {
            areas.Add(DefaultAreaClassroom);
            areas.Add(DefaultAreaCleaning);
        }

        return areas;
    }



    private static Dictionary<string, List<string>> NormalizeAreaAssignmentsForState(
        IEnumerable<KeyValuePair<string, List<string>>>? rawAssignments)
    {
        var assignments = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (rawAssignments == null)
        {
            return assignments;
        }

        foreach (var (area, students) in rawAssignments)
        {
            var areaName = (area ?? string.Empty).Trim();
            if (areaName.Length == 0)
            {
                continue;
            }

            var normalizedStudents = NormalizeStudents(students);
            assignments[areaName] = normalizedStudents;
        }

        return assignments;
    }

    private static bool TryNormalizeScheduleDate(string? rawDate, out string normalizedDate, out DateTime parsedDate)
    {
        normalizedDate = string.Empty;
        parsedDate = default;
        var text = (rawDate ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return false;
        }

        if (DateTime.TryParseExact(
                text,
                "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out parsedDate) ||
            DateTime.TryParse(text, out parsedDate))
        {
            normalizedDate = parsedDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }

        return false;
    }

    private static string NormalizeScheduleDay(string? day, DayOfWeek dayOfWeek)
    {
        var normalized = (day ?? string.Empty).Trim();
        if (normalized.Length > 0)
        {
            return normalized;
        }

        return dayOfWeek switch
        {
            DayOfWeek.Monday => "\u5468\u4E00",
            DayOfWeek.Tuesday => "\u5468\u4E8C",
            DayOfWeek.Wednesday => "\u5468\u4E09",
            DayOfWeek.Thursday => "\u5468\u56DB",
            DayOfWeek.Friday => "\u5468\u4E94",
            DayOfWeek.Saturday => "\u5468\u516D",
            DayOfWeek.Sunday => "\u5468\u65E5",
            _ => string.Empty
        };
    }

    private void PublishAutomationRunResult(
        string instruction,
        CoreRunResult result,
        bool isAutoRun)
    {
        if (string.Equals(result.Code, "busy", StringComparison.Ordinal))
        {
            return;
        }

        _automationBridge.PublishRunCompleted(new DutyScheduleRunEvent(
            DateTimeOffset.Now,
            result.Success,
            instruction,
            result.Message,
            result.Code,
            isAutoRun));
    }

    public void PublishRunCompletionNotification(
        string? instruction,
        string? resultMessage,
        bool success = true,
        bool isAutoRun = false)
    {
        try
        {
            LoadConfig();
            var duration = Math.Clamp(Config.NotificationDurationSeconds, 3, 15);
            var now = DateTime.Now;
            var targetDate = GetCurrentScheduleDate(now).ToString("yyyy-MM-dd");
            var item = GetScheduleItem(targetDate);
            var assignments = item is null
                ? new Dictionary<string, List<string>>(StringComparer.Ordinal)
                : GetAreaAssignments(item);

            var segments = FormatAreaAssignments(assignments, emptyStudentLabel: "\u65E0");

            var scene = isAutoRun ? "\u81EA\u52A8\u6392\u73ED" : "\u6392\u73ED\u4EFB\u52A1";
            var status = success ? "\u5DF2\u5B8C\u6210" : "\u6267\u884C\u5931\u8D25";
            var primaryText = $"{scene}{status}";
            var scrollingText = segments.Count > 0
                ? string.Join("\uFF1B", segments)
                : "\u6682\u65E0\u5B89\u6392";

            _notificationService.Publish(primaryText, scrollingText, duration);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"PublishRunCompletionNotification Error: {ex.Message}");
        }
    }

    private void PublishAutoRunTriggeredNotification(DateTime now)
    {
        try
        {
            if (!Config.AutoRunTriggerNotificationEnabled)
            {
                return;
            }

            var duration = Math.Clamp(Config.NotificationDurationSeconds, 3, 15);
            var primaryText = "\u81EA\u52A8\u6392\u73ED\u5F00\u59CB\u6267\u884C";
            var scrollingText = $"{now:yyyy-MM-dd HH:mm} \u4EFB\u52A1\u5DF2\u52A0\u5165\u961F\u5217";

            _notificationService.Publish(primaryText, scrollingText, duration);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"PublishAutoRunTriggeredNotification Error: {ex.Message}");
        }
    }

    public void PublishDutyReminderNotificationNow(string? dateText = null, string? timeText = null)
    {
        LoadConfig();

        var dateValue = (dateText ?? string.Empty).Trim();
        if (dateValue.Length == 0)
        {
            dateValue = GetCurrentScheduleDate().ToString("yyyy-MM-dd");
        }

        var timeValue = (timeText ?? string.Empty).Trim();
        if (timeValue.Length == 0)
        {
            timeValue = DateTime.Now.ToString("HH:mm");
        }

        PublishDutyReminderNotification(dateValue, timeValue);
    }

    private void TryPublishDutyReminderNotifications(DateTime now, bool allowRepublishIfChanged = false)
    {
        if (!Config.DutyReminderEnabled)
        {
            return;
        }

        var reminderTimes = NormalizeDutyReminderTimes(Config.DutyReminderTimes);
        if (reminderTimes.Count == 0)
        {
            return;
        }

        var targetDate = GetCurrentScheduleDate(now).ToString("yyyy-MM-dd");
        List<string> dueTimes;

        lock (_dutyReminderLock)
        {
            CleanupPublishedDutyReminderSlots(targetDate);
            dueTimes = GetDueDutyReminderTimes(now, reminderTimes);
        }

        foreach (var reminderTime in dueTimes)
        {
            PublishDutyReminderNotificationIfNeeded(targetDate, reminderTime, allowRepublishIfChanged);
        }
    }

    private void PublishDutyReminderNotification(string dateText, string timeText)
    {
        var duration = Math.Clamp(Config.NotificationDurationSeconds, 3, 15);
        var item = GetScheduleItem(dateText);
        var assignments = item is null
            ? new Dictionary<string, List<string>>(StringComparer.Ordinal)
            : GetAreaAssignments(item);

        var assignmentSegments = FormatAreaAssignments(assignments, emptyStudentLabel: "\u65E0");

        var primaryText = $"\u5F53\u524D\u503C\u65E5\u63D0\u9192 {timeText}";
        var scrollingText = item is null
            ? $"{dateText} \u6682\u65E0\u503C\u65E5\u5B89\u6392"
            : assignmentSegments.Count > 0 ? string.Join("\uFF1B", assignmentSegments) : $"{dateText} \u6682\u65E0\u503C\u65E5\u5B89\u6392";

        _notificationService.Publish(primaryText, scrollingText, duration);
    }

    private void PublishDutyReminderNotificationIfNeeded(string dateText, string timeText, bool allowRepublishIfChanged)
    {
        var slotKey = $"{dateText}|{timeText}";
        var signature = BuildDutyReminderSignature(dateText);

        lock (_dutyReminderLock)
        {
            if (_publishedDutyReminderSignatures.TryGetValue(slotKey, out var existingSignature))
            {
                if (!allowRepublishIfChanged || string.Equals(existingSignature, signature, StringComparison.Ordinal))
                {
                    return;
                }
            }

            _publishedDutyReminderSignatures[slotKey] = signature;
        }

        PublishDutyReminderNotification(dateText, timeText);
    }

    private List<string> GetDueDutyReminderTimes(DateTime now, IEnumerable<string> reminderTimes)
    {
        var dueTimes = new List<string>();
        foreach (var reminderTime in reminderTimes)
        {
            if (!TimeSpan.TryParse(reminderTime, out var triggerTime))
            {
                continue;
            }

            var triggerAt = now.Date.Add(triggerTime);
            if (now >= triggerAt && now < triggerAt.AddMinutes(1))
            {
                dueTimes.Add(reminderTime);
            }
        }

        return dueTimes;
    }

    private void CleanupPublishedDutyReminderSlots(string targetDate)
    {
        var staleKeys = _publishedDutyReminderSignatures.Keys
            .Where(key => !key.StartsWith($"{targetDate}|", StringComparison.Ordinal))
            .ToList();
        foreach (var staleKey in staleKeys)
        {
            _publishedDutyReminderSignatures.Remove(staleKey);
        }
    }

    private string BuildDutyReminderSignature(string dateText)
    {
        var item = GetScheduleItem(dateText);
        if (item is null)
        {
            return $"{dateText}|empty";
        }

        var assignments = GetAreaAssignments(item);
        var builder = new StringBuilder(dateText);
        foreach (var area in assignments.Keys.OrderBy(name => name, StringComparer.Ordinal))
        {
            builder.Append('|').Append(area).Append('=');
            builder.Append(string.Join(",", assignments[area]));
        }

        var note = (item.Note ?? string.Empty).Trim();
        if (note.Length > 0)
        {
            builder.Append("|note=").Append(note);
        }

        return builder.ToString();
    }

    private static List<string> FormatAreaAssignments(
        IReadOnlyDictionary<string, List<string>> assignments,
        string emptyStudentLabel)
    {
        return assignments
            .Where(x => !string.IsNullOrWhiteSpace(x.Key))
            .Select(x =>
            {
                var students = x.Value?.Where(name => !string.IsNullOrWhiteSpace(name)).ToList() ?? [];
                var peopleText = students.Count > 0 ? string.Join("\u3001", students) : emptyStudentLabel;
                return $"{x.Key}\uFF1A{peopleText}";
            })
            .ToList();
    }

    private void ScheduleCurrentDutyBoundaryRefresh()
    {
        if (!_runtimeStarted)
        {
            return;
        }

        LoadConfig();
        var now = DateTime.Now;
        var refreshTime = TimeSpan.TryParse(Config.ComponentRefreshTime, out var configuredTime)
            ? configuredTime
            : new TimeSpan(8, 0, 0);
        var nextBoundary = now.Date.Add(refreshTime);
        if (now >= nextBoundary)
        {
            nextBoundary = nextBoundary.AddDays(1);
        }

        var nextIntervalMs = Math.Max(1000d, (nextBoundary - now).TotalMilliseconds);
        lock (_currentDutyBoundaryLock)
        {
            _currentDutyBoundaryTimer.Stop();
            _currentDutyBoundaryTimer.Interval = nextIntervalMs;
            _currentDutyBoundaryTimer.Start();
        }
    }



    private static List<string> NormalizeDutyReminderTimes(IEnumerable<string>? rawTimes)
    {
        var times = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (rawTimes != null)
        {
            foreach (var raw in rawTimes)
            {
                var text = raw ?? string.Empty;
                foreach (var token in text.Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!TryNormalizeDutyReminderTime(token, out var normalized) || !seen.Add(normalized))
                    {
                        continue;
                    }

                    times.Add(normalized);
                }
            }
        }

        if (times.Count == 0)
        {
            times.Add(DefaultDutyReminderTime);
        }

        times.Sort(StringComparer.Ordinal);
        return times;
    }

    private static bool TryNormalizeDutyReminderTime(string raw, out string normalized)
    {
        normalized = string.Empty;
        var text = (raw ?? string.Empty).Trim();
        if (!TimeSpan.TryParse(text, out var time))
        {
            return false;
        }

        if (time < TimeSpan.Zero || time >= TimeSpan.FromDays(1))
        {
            return false;
        }

        normalized = time.ToString(@"hh\:mm");
        return true;
    }

    private static bool IsAutoRunTriggered(string mode, string parameter, string lastAutoRunDate, DateTime now)
    {
        var param = (parameter ?? string.Empty).Trim();
        switch (mode.ToLowerInvariant())
        {
            case "weekly":
                if (AutoRunDayAliases.TryGetValue(param, out var dow) || Enum.TryParse(param, true, out dow))
                {
                    return now.DayOfWeek == dow;
                }
                return false;

            case "monthly":
            {
                var daysInMonth = DateTime.DaysInMonth(now.Year, now.Month);
                int targetDay;
                if (string.Equals(param, "L", StringComparison.OrdinalIgnoreCase))
                {
                    targetDay = daysInMonth;
                }
                else if (int.TryParse(param, out var parsed))
                {
                    targetDay = Math.Clamp(parsed, 1, daysInMonth);
                }
                else
                {
                    return false;
                }

                return now.Day == targetDay;
            }

            case "custom":
                if (!int.TryParse(param, out var intervalDays) || intervalDays <= 0)
                {
                    return false;
                }

                if (string.IsNullOrWhiteSpace(lastAutoRunDate))
                {
                    return true;
                }

                if (!DateTime.TryParse(lastAutoRunDate, out var lastDate))
                {
                    return true;
                }

                return (now.Date - lastDate.Date).TotalDays >= intervalDays;

            default:
                return false;
        }
    }

    private static string ToUniqueRosterName(string baseName, IDictionary<string, int> nameCounts)
    {
        if (!nameCounts.TryGetValue(baseName, out var count))
        {
            nameCounts[baseName] = 1;
            return baseName;
        }

        count++;
        nameCounts[baseName] = count;
        return $"{baseName}{count}";
    }

    private static string EscapeCsv(string text)
    {
        if (text.IndexOfAny([',', '"', '\r', '\n']) < 0)
        {
            return text;
        }

        return $"\"{text.Replace("\"", "\"\"")}\"";
    }



}
