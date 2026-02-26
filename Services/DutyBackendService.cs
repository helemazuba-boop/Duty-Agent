using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using DutyAgent.Models;
using Timer = System.Timers.Timer;

namespace DutyAgent.Services;

public class DutyBackendService : IDisposable
{
    private const string AutoRunInstruction = "Please generate duty schedule automatically based on roster.csv.";
    private const int AutoRunRetryCooldownMinutes = 30;
    private const int CoreProcessTimeoutMs = 300_000;
    private const string ApiKeyMask = "********";
    private const string CoreProgressPrefix = "__DUTY_PROGRESS__:";
    private const string DefaultAreaClassroom = "\u6559\u5BA4";
    private const string DefaultAreaCleaning = "\u6E05\u6D01\u533A";
    private const string DefaultNotificationTemplate =
        "{scene}{status}\uFF0C\u65E5\u671F\uFF1A{date}\uFF0C\u533A\u57DF\uFF1A{areas}";
    private const string DefaultDutyReminderTemplate =
        "\u503C\u65E5\u63D0\u9192\uFF1A{date} {time}\uFF0C{assignments}";
    private const string DefaultDutyReminderTime = "07:40";
    private static readonly string PluginBaseDirectory =
        Path.GetDirectoryName(typeof(DutyBackendService).Assembly.Location) ?? AppContext.BaseDirectory;
    private static readonly Dictionary<string, DayOfWeek> AutoRunDayAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mon"] = DayOfWeek.Monday,
        ["monday"] = DayOfWeek.Monday,
        ["1"] = DayOfWeek.Monday,
        ["\u5468\u4E00"] = DayOfWeek.Monday,
        ["\u661F\u671F\u4E00"] = DayOfWeek.Monday,
        ["tue"] = DayOfWeek.Tuesday,
        ["tuesday"] = DayOfWeek.Tuesday,
        ["2"] = DayOfWeek.Tuesday,
        ["\u5468\u4E8C"] = DayOfWeek.Tuesday,
        ["\u661F\u671F\u4E8C"] = DayOfWeek.Tuesday,
        ["wed"] = DayOfWeek.Wednesday,
        ["wednesday"] = DayOfWeek.Wednesday,
        ["3"] = DayOfWeek.Wednesday,
        ["\u5468\u4E09"] = DayOfWeek.Wednesday,
        ["\u661F\u671F\u4E09"] = DayOfWeek.Wednesday,
        ["thu"] = DayOfWeek.Thursday,
        ["thursday"] = DayOfWeek.Thursday,
        ["4"] = DayOfWeek.Thursday,
        ["\u5468\u56DB"] = DayOfWeek.Thursday,
        ["\u661F\u671F\u56DB"] = DayOfWeek.Thursday,
        ["fri"] = DayOfWeek.Friday,
        ["friday"] = DayOfWeek.Friday,
        ["5"] = DayOfWeek.Friday,
        ["\u5468\u4E94"] = DayOfWeek.Friday,
        ["\u661F\u671F\u4E94"] = DayOfWeek.Friday,
        ["sat"] = DayOfWeek.Saturday,
        ["saturday"] = DayOfWeek.Saturday,
        ["6"] = DayOfWeek.Saturday,
        ["\u5468\u516D"] = DayOfWeek.Saturday,
        ["\u661F\u671F\u516D"] = DayOfWeek.Saturday,
        ["sun"] = DayOfWeek.Sunday,
        ["sunday"] = DayOfWeek.Sunday,
        ["7"] = DayOfWeek.Sunday,
        ["0"] = DayOfWeek.Sunday,
        ["\u5468\u65E5"] = DayOfWeek.Sunday,
        ["\u5468\u5929"] = DayOfWeek.Sunday,
        ["\u661F\u671F\u65E5"] = DayOfWeek.Sunday,
        ["\u661F\u671F\u5929"] = DayOfWeek.Sunday
    };

    private readonly string _basePath = Path.Combine(PluginBaseDirectory, "Assets_Duty");
    private readonly string _dataDir;
    private readonly string _configPath;
    private readonly DutyNotificationService _notificationService;
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _debounceTimer;
    private readonly Timer _autoRunTimer;
    private readonly object _configLock = new();
    private readonly object _dutyReminderLock = new();
    private readonly SemaphoreSlim _runCoreGate = new(1, 1);
    private readonly HashSet<string> _sentDutyReminderSlots = new(StringComparer.Ordinal);
    private DateTime _lastAutoRunAttempt = DateTime.MinValue;
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public event EventHandler? ScheduleUpdated;

    public DutyConfig Config { get; private set; } = new();

    public DutyBackendService(DutyNotificationService notificationService)
    {
        _notificationService = notificationService;
        _dataDir = Path.Combine(_basePath, "data");
        _configPath = Path.Combine(_dataDir, "config.json");
        Directory.CreateDirectory(_dataDir);

        LoadConfig();

        _watcher = new FileSystemWatcher(_dataDir, "state.json")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            EnableRaisingEvents = true
        };
        _watcher.Changed += OnStateFileChanged;
        _watcher.Created += OnStateFileChanged;
        _watcher.Renamed += OnStateFileChanged;

        _debounceTimer = new Timer(500) { AutoReset = false };
        _debounceTimer.Elapsed += (_, _) =>
        {
            Dispatcher.UIThread.InvokeAsync(() => ScheduleUpdated?.Invoke(this, EventArgs.Empty));
        };

        _autoRunTimer = new Timer(60_000) { AutoReset = true, Enabled = true };
        _autoRunTimer.Elapsed += (_, _) => TryRunAutoSchedule();
    }

    public void LoadConfig()
    {
        lock (_configLock)
        {
            if (!File.Exists(_configPath))
            {
                Config = new DutyConfig();
                Config.NotificationTemplates = NormalizeNotificationTemplates(Config.NotificationTemplates);
                Config.DutyReminderTimes = NormalizeDutyReminderTimes(Config.DutyReminderTimes);
                Config.DutyReminderTemplates = NormalizeDutyReminderTemplates(Config.DutyReminderTemplates);
                SaveConfig();
                return;
            }

            try
            {
                var json = File.ReadAllText(_configPath, Encoding.UTF8);
                var config = JsonSerializer.Deserialize<DutyConfig>(json) ?? new DutyConfig();
                config.NotificationTemplates = NormalizeNotificationTemplates(config.NotificationTemplates);
                config.DutyReminderTimes = NormalizeDutyReminderTimes(config.DutyReminderTimes);
                config.DutyReminderTemplates = NormalizeDutyReminderTemplates(config.DutyReminderTemplates);

                if (!string.IsNullOrWhiteSpace(config.EncryptedApiKey))
                {
                    if (!SecurityHelper.IsCurrentEncryptionFormat(config.EncryptedApiKey))
                    {
                        // Migrate legacy plaintext API keys to encrypted storage.
                        config.DecryptedApiKey = config.EncryptedApiKey;
                        Config = config;
                        SaveConfig();
                        return;
                    }

                    _ = config.DecryptedApiKey;
                }

                Config = config;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"LoadConfig Error: {ex.Message}");
                Config = new DutyConfig();
                Config.NotificationTemplates = NormalizeNotificationTemplates(Config.NotificationTemplates);
                Config.DutyReminderTimes = NormalizeDutyReminderTimes(Config.DutyReminderTimes);
                Config.DutyReminderTemplates = NormalizeDutyReminderTemplates(Config.DutyReminderTemplates);
                SaveConfig();
            }
        }
    }

    public void SaveConfig()
    {
        lock (_configLock)
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true
            };
            var json = JsonSerializer.Serialize(Config, options);
            File.WriteAllText(_configPath, json, Utf8NoBom);
        }
    }

    public void SaveUserConfig(
        string apiKey,
        string baseUrl,
        string model,
        string autoRunMode,
        string autoRunParameter,
        string autoRunTime,
        int perDay,
        string dutyRule,
        string componentRefreshTime,
        string pythonPath,
        IEnumerable<string>? notificationTemplates = null,
        bool? dutyReminderEnabled = null,
        IEnumerable<string>? dutyReminderTimes = null,
        IEnumerable<string>? dutyReminderTemplates = null,
        bool? enableMcp = null,
        bool? enableWebViewDebugLayer = null,
        bool? autoRunTriggerNotificationEnabled = null)
    {
        lock (_configLock)
        {
            Config.DecryptedApiKey = ResolveApiKeyInput(apiKey, Config.DecryptedApiKey);
            Config.BaseUrl = baseUrl;
            Config.Model = model;
            Config.AutoRunMode = NormalizeAutoRunMode(autoRunMode);
            Config.AutoRunParameter = (autoRunParameter ?? Config.AutoRunParameter).Trim();
            Config.EnableMcp = enableMcp ?? Config.EnableMcp;
            Config.EnableWebViewDebugLayer = enableWebViewDebugLayer ?? Config.EnableWebViewDebugLayer;
            Config.AutoRunTime = NormalizeTimeOrThrow(autoRunTime);
            Config.PerDay = Math.Clamp(perDay, 1, 30);
            Config.DutyRule = dutyRule;
            Config.ComponentRefreshTime = NormalizeTimeOrThrow(componentRefreshTime);
            Config.AutoRunTriggerNotificationEnabled =
                autoRunTriggerNotificationEnabled ?? Config.AutoRunTriggerNotificationEnabled;
            Config.PythonPath = string.IsNullOrWhiteSpace(pythonPath) ? Config.PythonPath : pythonPath.Trim();
            Config.NotificationTemplates =
                NormalizeNotificationTemplates(notificationTemplates ?? Config.NotificationTemplates);
            Config.DutyReminderEnabled = dutyReminderEnabled ?? Config.DutyReminderEnabled;
            Config.DutyReminderTimes = NormalizeDutyReminderTimes(dutyReminderTimes ?? Config.DutyReminderTimes);
            Config.DutyReminderTemplates =
                NormalizeDutyReminderTemplates(dutyReminderTemplates ?? Config.DutyReminderTemplates);
            SaveConfig();
        }
    }

    public bool RunCoreAgent(string instruction, string applyMode = "append", string? overrideModel = null)
    {
        var result = RunCoreAgentWithMessage(instruction, applyMode, overrideModel);
        if (!result.Success)
        {
            Debug.WriteLine($"RunCoreAgent failed: {result.Message}");
        }

        return result.Success;
    }

    public CoreRunResult RunCoreAgentWithMessage(
        string instruction,
        string applyMode = "append",
        string? overrideModel = null,
        Action<CoreRunProgress>? progress = null)
    {
        static CoreRunProgress BuildProgress(string phase, string message, string? streamChunk = null) =>
            new(phase, message, streamChunk);

        var effectiveInstruction = string.IsNullOrWhiteSpace(instruction)
            ? AutoRunInstruction
            : instruction.Trim();

        if (!_runCoreGate.Wait(0))
        {
            return CoreRunResult.Fail("Another schedule run is already in progress.", code: "busy");
        }

        try
        {
        progress?.Invoke(BuildProgress("read_api_key", "Reading encrypted API key."));
        LoadConfig();
        var apiKeyPlain = Config.DecryptedApiKey.Trim();
        if (string.IsNullOrWhiteSpace(apiKeyPlain))
        {
            return CoreRunResult.Fail("API key is empty or unavailable on this device.", code: "config");
        }

        var pythonPath = ValidatePythonPath(Config.PythonPath, PluginBaseDirectory, _basePath);
        var inputPath = Path.Combine(_dataDir, "ipc_input.json");
        var resultPath = Path.Combine(_dataDir, "ipc_result.json");
        var scriptPath = Path.Combine(_basePath, "core.py");

        if (!File.Exists(scriptPath))
        {
            return CoreRunResult.Fail($"Core script not found: {scriptPath}");
        }

        if (File.Exists(resultPath))
        {
            File.Delete(resultPath);
        }

        progress?.Invoke(BuildProgress("build_prompt", "Building prompt and payload."));
        var inputData = new
        {
            instruction = effectiveInstruction,
            apply_mode = applyMode,
            per_day = Config.PerDay,
            duty_rule = Config.DutyRule,
            base_url = Config.BaseUrl,
            model = overrideModel ?? Config.Model
        };
        File.WriteAllText(inputPath, JsonSerializer.Serialize(inputData), Utf8NoBom);

        var startInfo = new ProcessStartInfo
        {
            FileName = pythonPath,
            Arguments = $"\"{scriptPath}\" --data-dir \"{_dataDir}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data == null)
            {
                return;
            }

            if (!TryHandleCoreProgressLine(args.Data, progress))
            {
                stdout.AppendLine(args.Data);
            }
        };
        process.ErrorDataReceived += (_, args) => { if (args.Data != null) stderr.AppendLine(args.Data); };

        var processStarted = false;
        try
        {
            if (!process.Start())
            {
                return CoreRunResult.Fail("Failed to start Python process.");
            }

            processStarted = true;
            PythonProcessTracker.Register(process);
            process.StandardInput.WriteLine(apiKeyPlain);
            process.StandardInput.Close();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            progress?.Invoke(BuildProgress("wait_response", "Waiting for model response."));
            if (!process.WaitForExit(CoreProcessTimeoutMs))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                return CoreRunResult.Fail(
                    $"Core process timed out after {CoreProcessTimeoutMs / 1000} seconds.");
            }

            process.WaitForExit();
        }
        finally
        {
            if (processStarted)
            {
                PythonProcessTracker.Unregister(process);
            }
        }

        var stderrText = stderr.ToString().Trim();
        var stdoutText = stdout.ToString().Trim();

        if (process.ExitCode != 0)
        {
            var (message, aiResponse) = TryReadCoreResultFields(resultPath);
            if (string.IsNullOrWhiteSpace(message))
            {
                message = SummarizePythonError(stderrText);
            }

            if (string.IsNullOrWhiteSpace(message))
            {
                message = SummarizePythonError(stdoutText);
            }

            if (string.IsNullOrWhiteSpace(message))
            {
                message = "Core process execution failed.";
            }

            return CoreRunResult.Fail(
                $"Schedule generation failed (ExitCode={process.ExitCode}): {message}",
                aiResponse);
        }

        if (!File.Exists(resultPath))
        {
            var message = SummarizePythonError(stderrText);
            if (string.IsNullOrWhiteSpace(message))
            {
                message = SummarizePythonError(stdoutText);
            }

            if (string.IsNullOrWhiteSpace(message))
            {
                message = "ipc_result.json was not generated.";
            }

            return CoreRunResult.Fail(message);
        }

        try
        {
            progress?.Invoke(BuildProgress("organize_result", "Organizing schedule result."));
            var resultJson = File.ReadAllText(resultPath, Encoding.UTF8);
            using var doc = JsonDocument.Parse(resultJson);
            var success = doc.RootElement.TryGetProperty("status", out var status) &&
                          string.Equals(status.GetString(), "success", StringComparison.OrdinalIgnoreCase);
            var aiResponse = doc.RootElement.TryGetProperty("ai_response", out var aiElement)
                ? (aiElement.GetString() ?? string.Empty)
                : string.Empty;
            if (success)
            {
                return CoreRunResult.Ok("Execution succeeded.", aiResponse);
            }

            var errorMessage = doc.RootElement.TryGetProperty("message", out var messageElement)
                ? (messageElement.GetString() ?? string.Empty).Trim()
                : string.Empty;
            if (errorMessage.Length == 0)
            {
                errorMessage = "Core process returned a non-success status.";
            }

            return CoreRunResult.Fail(errorMessage, aiResponse);
        }
        catch (Exception ex)
        {
            return CoreRunResult.Fail($"Failed to parse result file: {ex.Message}");
        }
        }
        finally
        {
            _runCoreGate.Release();
        }
    }

    private static bool TryHandleCoreProgressLine(string line, Action<CoreRunProgress>? progress)
    {
        if (!line.StartsWith(CoreProgressPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rawPayload = line[CoreProgressPrefix.Length..].Trim();
        if (rawPayload.Length == 0)
        {
            return true;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawPayload);
            var phase = doc.RootElement.TryGetProperty("phase", out var phaseElement)
                ? (phaseElement.GetString() ?? string.Empty).Trim()
                : string.Empty;
            if (phase.Length == 0)
            {
                return true;
            }

            var message = doc.RootElement.TryGetProperty("message", out var messageElement)
                ? (messageElement.GetString() ?? string.Empty)
                : string.Empty;
            var streamChunk = doc.RootElement.TryGetProperty("chunk", out var chunkElement)
                ? (chunkElement.GetString() ?? string.Empty)
                : string.Empty;
            progress?.Invoke(new CoreRunProgress(phase, message, streamChunk));
        }
        catch
        {
            // Ignore malformed progress payloads and keep processing.
        }

        return true;
    }

    private static (string? Message, string? AiResponse) TryReadCoreResultFields(string resultPath)
    {
        try
        {
            if (!File.Exists(resultPath))
            {
                return (null, null);
            }

            var resultJson = File.ReadAllText(resultPath, Encoding.UTF8);
            using var doc = JsonDocument.Parse(resultJson);
            var message = doc.RootElement.TryGetProperty("message", out var messageElement)
                ? (messageElement.GetString() ?? string.Empty).Trim()
                : null;
            if (message?.Length == 0)
            {
                message = null;
            }

            var aiResponse = doc.RootElement.TryGetProperty("ai_response", out var aiElement)
                ? (aiElement.GetString() ?? string.Empty).Trim()
                : null;
            if (aiResponse?.Length == 0)
            {
                aiResponse = null;
            }

            return (message, aiResponse);
        }
        catch
        {
            return (null, null);
        }
    }

    public string GetApiKeyMaskForUi()
    {
        lock (_configLock)
        {
            return string.IsNullOrWhiteSpace(Config.EncryptedApiKey) ? string.Empty : ApiKeyMask;
        }
    }

    public static string ResolveApiKeyInput(string? incomingApiKey, string existingApiKey)
    {
        if (incomingApiKey is null)
        {
            return existingApiKey;
        }

        var trimmed = incomingApiKey.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        if (string.Equals(trimmed, ApiKeyMask, StringComparison.Ordinal))
        {
            return existingApiKey;
        }

        return trimmed;
    }

    private static string? SummarizePythonError(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var lines = output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();

        if (lines.Count == 0)
        {
            return null;
        }

        var tracebackTail = lines.LastOrDefault(line => line.Contains(':'));
        if (!string.IsNullOrWhiteSpace(tracebackTail))
        {
            var parts = tracebackTail.Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && parts[1].Length > 0)
            {
                return parts[1];
            }

            return tracebackTail;
        }

        return lines[^1];
    }

    public string GetRosterPath() => Path.Combine(_dataDir, "roster.csv");

    public List<string> GetAreaNames()
    {
        var state = LoadState();
        return InferAreaNamesFromState(state);
    }

    public List<string> GetNotificationTemplates()
    {
        lock (_configLock)
        {
            return NormalizeNotificationTemplates(Config.NotificationTemplates);
        }
    }

    public List<string> GetDutyReminderTimes()
    {
        lock (_configLock)
        {
            return NormalizeDutyReminderTimes(Config.DutyReminderTimes);
        }
    }

    public List<string> GetDutyReminderTemplates()
    {
        lock (_configLock)
        {
            return NormalizeDutyReminderTemplates(Config.DutyReminderTemplates);
        }
    }

    public Dictionary<string, List<string>> GetAreaAssignments(SchedulePoolItem item)
    {
        var assignments = BuildAreaAssignments(item);
        var areaNames = NormalizeAreaNames(item.AreaAssignments.Keys);
        if (areaNames.Count == 0)
        {
            areaNames = GetAreaNames();
        }

        foreach (var area in areaNames)
        {
            assignments.TryAdd(area, []);
        }
        return assignments;
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

    public DutyState LoadState()
    {
        var path = Path.Combine(_dataDir, "state.json");
        if (!File.Exists(path)) return new DutyState();
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<DutyState>(json) ?? new DutyState();
        }
        catch
        {
            return new DutyState();
        }
    }

    public void SaveState(DutyState? state)
    {
        var path = Path.Combine(_dataDir, "state.json");
        Directory.CreateDirectory(_dataDir);

        var normalizedState = state ?? new DutyState();
        normalizedState.SeedAnchor = (normalizedState.SeedAnchor ?? string.Empty).Trim();
        normalizedState.NextRunNote = (normalizedState.NextRunNote ?? string.Empty).Trim();
        normalizedState.SchedulePool = (normalizedState.SchedulePool ?? [])
            .Where(x => x != null)
            .Select(x => new SchedulePoolItem
            {
                Date = (x.Date ?? string.Empty).Trim(),
                Day = (x.Day ?? string.Empty).Trim(),
                AreaAssignments = NormalizeAreaAssignmentsForState(x.AreaAssignments),
                Note = (x.Note ?? string.Empty).Trim()
            })
            .Where(x => x.Date.Length > 0)
            .OrderBy(x => x.Date, StringComparer.Ordinal)
            .ToList();

        var options = new JsonSerializerOptions
        {
            WriteIndented = true
        };
        var json = JsonSerializer.Serialize(normalizedState, options);
        File.WriteAllText(path, json, Utf8NoBom);
    }

    public bool TryUpdateScheduleEntry(
        string? date,
        IDictionary<string, List<string>>? areaAssignments,
        string? note,
        out string message)
    {
        return TrySaveScheduleEntry(
            sourceDate: date,
            targetDate: date,
            day: null,
            areaAssignments: areaAssignments,
            note: note,
            createIfMissing: false,
            out message);
    }

    public bool TrySaveScheduleEntry(
        string? sourceDate,
        string? targetDate,
        string? day,
        IDictionary<string, List<string>>? areaAssignments,
        string? note,
        bool createIfMissing,
        out string message)
    {
        if (!TryNormalizeScheduleDate(targetDate, out var normalizedTargetDate, out var parsedDate))
        {
            message = "Date format is invalid.";
            return false;
        }

        var normalizedSourceDate = (sourceDate ?? string.Empty).Trim();
        var normalizedDay = NormalizeScheduleDay(day, parsedDate.DayOfWeek);
        var normalizedAssignments = NormalizeAreaAssignmentsForState(areaAssignments);
        var normalizedNote = (note ?? string.Empty).Trim();
        var state = LoadState();

        SchedulePoolItem? item = null;
        if (normalizedSourceDate.Length > 0)
        {
            item = state.SchedulePool.LastOrDefault(x =>
                string.Equals(x.Date, normalizedSourceDate, StringComparison.Ordinal));
        }

        var duplicate = state.SchedulePool.LastOrDefault(x =>
            string.Equals(x.Date, normalizedTargetDate, StringComparison.Ordinal));

        if (item == null)
        {
            if (!createIfMissing)
            {
                message = "Schedule entry not found.";
                return false;
            }

            if (duplicate != null)
            {
                message = "Schedule entry already exists for target date.";
                return false;
            }

            item = new SchedulePoolItem();
            state.SchedulePool.Add(item);
        }
        else if (!string.Equals(item.Date, normalizedTargetDate, StringComparison.Ordinal) && duplicate != null)
        {
            message = "Schedule entry already exists for target date.";
            return false;
        }

        item.Date = normalizedTargetDate;
        item.Day = normalizedDay;
        item.AreaAssignments = normalizedAssignments;
        item.Note = normalizedNote;

        state.SchedulePool = state.SchedulePool
            .OrderBy(x => x.Date, StringComparer.Ordinal)
            .ToList();
        SaveState(state);

        message = createIfMissing && normalizedSourceDate.Length == 0
            ? "Schedule created."
            : "Schedule updated.";
        return true;
    }

    public void Dispose()
    {
        _autoRunTimer.Stop();
        _autoRunTimer.Dispose();
        _debounceTimer.Stop();
        _debounceTimer.Dispose();
        _watcher.Dispose();
    }

    private void OnStateFileChanged(object? sender, FileSystemEventArgs e)
    {
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private void TryRunAutoSchedule()
    {
        try
        {
            LoadConfig();
            var now = DateTime.Now;
            TryPublishDutyReminderNotifications(now);

            var mode = (Config.AutoRunMode ?? "Off").Trim();
            if (string.Equals(mode, "Off", StringComparison.OrdinalIgnoreCase)) return;

            if (Config.LastAutoRunDate == now.ToString("yyyy-MM-dd")) return;
            if ((now - _lastAutoRunAttempt).TotalMinutes < AutoRunRetryCooldownMinutes) return;

            if (!IsAutoRunTriggered(mode, Config.AutoRunParameter, Config.LastAutoRunDate, now)) return;
            if (!TimeSpan.TryParse(Config.AutoRunTime, out var targetTime) || now.TimeOfDay < targetTime) return;
            if (_runCoreGate.CurrentCount == 0) return;

            PublishAutoRunTriggeredNotification(now);
            var result = RunCoreAgentWithMessage(AutoRunInstruction, applyMode: "replace_all");
            if (string.Equals(result.Code, "busy", StringComparison.Ordinal))
            {
                return;
            }

            _lastAutoRunAttempt = now;
            PublishRunCompletionNotification(
                instruction: AutoRunInstruction,
                applyMode: "replace_all",
                resultMessage: result.Message,
                success: result.Success,
                isAutoRun: true);
            if (result.Success)
            {
                Config.LastAutoRunDate = now.ToString("yyyy-MM-dd");
                Config.AiConsecutiveFailures = 0;
                SaveConfig();
            }
            else
            {
                Config.AiConsecutiveFailures++;
                SaveConfig();
            }
        }
        catch
        {
        }
    }

    private static string ValidatePythonPath(string configuredPath, string pluginBasePath, string assetsBasePath)
    {
        var embeddedDefaultPath = Path.Combine(assetsBasePath, "python-embed", "python.exe");
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return embeddedDefaultPath;
        }

        var trimmed = configuredPath.Trim();
        if (Path.IsPathRooted(trimmed))
        {
            return trimmed;
        }

        var normalized = trimmed
            .Replace('/', Path.DirectorySeparatorChar)
            .TrimStart('.', Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(pluginBasePath, normalized)),
            Path.GetFullPath(Path.Combine(assetsBasePath, normalized)),
            Path.GetFullPath(embeddedDefaultPath)
        };

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.GetFullPath(Path.Combine(pluginBasePath, normalized));
    }

    private static string NormalizeTimeOrThrow(string? time)
    {
        if (TimeSpan.TryParse(time, out var ts)) return ts.ToString(@"hh\:mm");
        throw new ArgumentException("Invalid time format.");
    }

    /// <summary>
    /// Determines whether the auto-run should trigger right now, based on the
    /// configured mode (Weekly / Monthly / Custom) and its parameter.
    /// </summary>
    private static bool IsAutoRunTriggered(string mode, string parameter, string lastAutoRunDate, DateTime now)
    {
        var param = (parameter ?? string.Empty).Trim();
        switch (mode.ToLowerInvariant())
        {
            case "weekly":
                if (AutoRunDayAliases.TryGetValue(param, out var dow) || Enum.TryParse(param, true, out dow))
                    return now.DayOfWeek == dow;
                return false;

            case "monthly":
            {
                var daysInMonth = DateTime.DaysInMonth(now.Year, now.Month);
                int targetDay;
                if (string.Equals(param, "L", StringComparison.OrdinalIgnoreCase))
                    targetDay = daysInMonth;
                else if (int.TryParse(param, out var parsed))
                    targetDay = Math.Clamp(parsed, 1, daysInMonth); // safe clamp for short months
                else
                    return false;
                return now.Day == targetDay;
            }

            case "custom":
                if (!int.TryParse(param, out var intervalDays) || intervalDays <= 0)
                    return false;
                if (string.IsNullOrWhiteSpace(lastAutoRunDate))
                    return true; // never ran before, trigger immediately
                if (!DateTime.TryParse(lastAutoRunDate, out var lastDate))
                    return true;
                return (now.Date - lastDate.Date).TotalDays >= intervalDays;

            default:
                return false;
        }
    }

    private static string NormalizeAutoRunMode(string mode)
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

    private static List<string> NormalizeNotificationTemplates(IEnumerable<string>? rawTemplates)
    {
        var templates = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (rawTemplates != null)
        {
            foreach (var raw in rawTemplates)
            {
                var template = (raw ?? string.Empty).Trim();
                if (template.Length == 0 || !seen.Add(template))
                {
                    continue;
                }

                templates.Add(template);
            }
        }

        if (templates.Count == 0)
        {
            templates.Add(DefaultNotificationTemplate);
        }

        return templates;
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

    public void PublishRunCompletionNotification(
        string? instruction,
        string? applyMode,
        string? resultMessage,
        bool success = true,
        bool isAutoRun = false)
    {
        try
        {
            LoadConfig();
            var config = Config;
            var now = DateTime.Now;
            var today = now.ToString("yyyy-MM-dd");
            var areaNames = GetAreaNames();
            var state = LoadState();
            var item = state.SchedulePool.LastOrDefault(x => string.Equals(x.Date, today, StringComparison.Ordinal));
            var assignments = item is null
                ? new Dictionary<string, List<string>>(StringComparer.Ordinal)
                : GetAreaAssignments(item);

            var segments = areaNames
                .Select(area =>
                {
                    var students = assignments.TryGetValue(area, out var names) ? names : [];
                    var peopleText = students.Count > 0 ? string.Join("\u3001", students) : "\u65E0";
                    return $"{area}\uFF1A{peopleText}";
                })
                .ToList();

            var scene = isAutoRun ? "\u81EA\u52A8\u6392\u73ED" : "\u6392\u73ED\u4EFB\u52A1";
            var status = success ? "\u5DF2\u5B8C\u6210" : "\u6267\u884C\u5931\u8D25";
            var mode = string.IsNullOrWhiteSpace(applyMode)
                ? (isAutoRun ? "auto_run" : "manual")
                : applyMode.Trim();
            var messageText = (resultMessage ?? string.Empty).Trim();
            if (messageText.Length == 0)
            {
                messageText = success
                    ? "\u6392\u73ED\u5DF2\u5B8C\u6210\u3002"
                    : "\u6392\u73ED\u672A\u6210\u529F\u5B8C\u6210\u3002";
            }

            var instructionText = (instruction ?? string.Empty).Trim();
            if (instructionText.Length == 0 && isAutoRun)
            {
                instructionText = AutoRunInstruction;
            }

            var placeholders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["scene"] = scene,
                ["status"] = status,
                ["date"] = today,
                ["areas"] = string.Join("\u3001", areaNames),

                ["per_day"] = config.PerDay.ToString(),
                ["mode"] = mode,
                ["instruction"] = instructionText,
                ["message"] = messageText,
                ["time"] = now.ToString("HH:mm"),
                ["assignments"] = segments.Count > 0
                    ? string.Join("\uFF1B", segments)
                    : "\u6682\u65E0\u5B89\u6392"
            };

            var fallback = $"{scene}{status}\uFF1A{today} {now:HH:mm}\uFF0C{messageText}";
            _notificationService.PublishFromTemplates(
                GetNotificationTemplates(),
                placeholders,
                fallback,
                durationSeconds: 8);
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

            var dateText = now.ToString("yyyy-MM-dd");
            var timeText = now.ToString("HH:mm");
            var areaNames = GetAreaNames();
            var scene = "\u81EA\u52A8\u6392\u73ED";
            var status = "\u5F00\u59CB\u6267\u884C";
            var placeholders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["scene"] = scene,
                ["status"] = status,
                ["date"] = dateText,
                ["areas"] = string.Join("\u3001", areaNames),

                ["per_day"] = Config.PerDay.ToString(),
                ["mode"] = "auto_run",
                ["instruction"] = AutoRunInstruction,
                ["message"] = "\u5DF2\u89E6\u53D1\u81EA\u52A8\u6392\u73ED",
                ["time"] = timeText,
                ["assignments"] = string.Empty
            };

            var fallback = $"{scene}{status}\uFF1A{dateText} {timeText}";
            _notificationService.PublishFromTemplates(
                GetNotificationTemplates(),
                placeholders,
                fallback,
                durationSeconds: 6);
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
            dateValue = DateTime.Now.ToString("yyyy-MM-dd");
        }

        var timeValue = (timeText ?? string.Empty).Trim();
        if (timeValue.Length == 0)
        {
            timeValue = DateTime.Now.ToString("HH:mm");
        }

        PublishDutyReminderNotification(dateValue, timeValue);
    }

    private void TryPublishDutyReminderNotifications(DateTime now)
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

        var today = now.ToString("yyyy-MM-dd");
        var dueTimes = new List<string>();

        lock (_dutyReminderLock)
        {
            _sentDutyReminderSlots.RemoveWhere(x => !x.StartsWith($"{today}|", StringComparison.Ordinal));

            foreach (var reminderTime in reminderTimes)
            {
                if (!TimeSpan.TryParse(reminderTime, out var triggerTime))
                {
                    continue;
                }

                var triggerAt = now.Date.Add(triggerTime);
                if (now < triggerAt || now >= triggerAt.AddMinutes(1))
                {
                    continue;
                }

                var slotKey = $"{today}|{reminderTime}";
                if (_sentDutyReminderSlots.Add(slotKey))
                {
                    dueTimes.Add(reminderTime);
                }
            }
        }

        foreach (var reminderTime in dueTimes)
        {
            PublishDutyReminderNotification(today, reminderTime);
        }
    }

    private void PublishDutyReminderNotification(string dateText, string timeText)
    {
        var state = LoadState();
        var areaNames = GetAreaNames();
        var item = state.SchedulePool.LastOrDefault(x => string.Equals(x.Date, dateText, StringComparison.Ordinal));
        var assignments = item is null
            ? new Dictionary<string, List<string>>(StringComparer.Ordinal)
            : GetAreaAssignments(item);

        var assignmentSegments = areaNames
            .Select(area =>
            {
                var students = assignments.TryGetValue(area, out var names) ? names : [];
                var studentText = students.Count > 0 ? string.Join("\u3001", students) : "\u65E0";
                return $"{area}\uFF1A{studentText}";
            })
            .ToList();

        var dutyStudents = assignments
            .Values
            .SelectMany(x => x ?? [])
            .Select(x => (x ?? string.Empty).Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var scene = "\u503C\u65E5\u63D0\u9192";
        var status = item is null ? "\u65E0\u5B89\u6392" : "\u8BF7\u5230\u5C97";
        var note = item is null ? string.Empty : (item.Note ?? string.Empty).Trim();
        var noteSuffix = note.Length > 0 ? $", {note}" : string.Empty;
        var assignmentText = assignmentSegments.Count > 0
            ? string.Join("\uFF1B", assignmentSegments)
            : "\u6682\u65E0\u503C\u65E5\u5B89\u6392";
        var fallbackText = item is null
            ? $"{scene}\uFF1A{dateText} {timeText}\uFF0C\u4ECA\u65E5\u6682\u65E0\u503C\u65E5\u5B89\u6392\u3002"
            : $"{scene}\uFF1A{dateText} {timeText}\uFF0C{assignmentText}{noteSuffix}";

        var placeholders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["scene"] = scene,
            ["status"] = status,
            ["date"] = dateText,
            ["time"] = timeText,
            ["areas"] = string.Join("\u3001", areaNames),
            ["duty_students"] = dutyStudents.Count > 0 ? string.Join("\u3001", dutyStudents) : "\u6682\u65E0",
            ["assignments"] = $"{assignmentText}{noteSuffix}",
            ["note"] = note,

            ["per_day"] = Config.PerDay.ToString(),
            ["mode"] = "duty_reminder",
            ["instruction"] = string.Empty,
            ["message"] = item is null
                ? "\u4ECA\u65E5\u6682\u65E0\u503C\u65E5\u5B89\u6392"
                : $"\u8BF7\u6309\u5B89\u6392\u5B8C\u6210\u503C\u65E5{noteSuffix}"
        };

        _notificationService.PublishFromTemplates(
            NormalizeDutyReminderTemplates(Config.DutyReminderTemplates),
            placeholders,
            fallbackText,
            durationSeconds: 8);
    }

    private static List<string> NormalizeDutyReminderTemplates(IEnumerable<string>? rawTemplates)
    {
        var templates = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (rawTemplates != null)
        {
            foreach (var raw in rawTemplates)
            {
                var template = (raw ?? string.Empty).Trim();
                if (template.Length == 0 || !seen.Add(template))
                {
                    continue;
                }

                templates.Add(template);
            }
        }

        if (templates.Count == 0)
        {
            templates.Add(DefaultDutyReminderTemplate);
        }

        return templates;
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

    public sealed class CoreRunResult
    {
        public bool Success { get; init; }
        public string Message { get; init; } = string.Empty;
        public string AiResponse { get; init; } = string.Empty;
        public string Code { get; init; } = string.Empty;

        public static CoreRunResult Ok(string message, string? aiResponse = null, string? code = null)
        {
            return new CoreRunResult
            {
                Success = true,
                Message = message,
                AiResponse = aiResponse ?? string.Empty,
                Code = code ?? string.Empty
            };
        }

        public static CoreRunResult Fail(string message, string? aiResponse = null, string? code = null)
        {
            return new CoreRunResult
            {
                Success = false,
                Message = message,
                AiResponse = aiResponse ?? string.Empty,
                Code = code ?? string.Empty
            };
        }
    }

    public sealed class CoreRunProgress
    {
        public string Phase { get; }
        public string Message { get; }
        public string StreamChunk { get; }

        public CoreRunProgress(string phase, string message, string? streamChunk = null)
        {
            Phase = phase;
            Message = message;
            StreamChunk = streamChunk ?? string.Empty;
        }
    }
}
