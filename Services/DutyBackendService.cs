using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using DutyIsland.Models;
using Timer = System.Timers.Timer;

namespace DutyIsland.Services;

public class DutyBackendService : IDisposable
{
    private const string AutoRunInstruction = "Please generate duty schedule automatically based on roster.csv.";
    private const int AutoRunRetryCooldownMinutes = 30;
    private const int CoreProcessTimeoutMs = 300_000;
    private const string ApiKeyMask = "********";
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
        ["周一"] = DayOfWeek.Monday,
        ["星期一"] = DayOfWeek.Monday,
        ["tue"] = DayOfWeek.Tuesday,
        ["tuesday"] = DayOfWeek.Tuesday,
        ["2"] = DayOfWeek.Tuesday,
        ["周二"] = DayOfWeek.Tuesday,
        ["星期二"] = DayOfWeek.Tuesday,
        ["wed"] = DayOfWeek.Wednesday,
        ["wednesday"] = DayOfWeek.Wednesday,
        ["3"] = DayOfWeek.Wednesday,
        ["周三"] = DayOfWeek.Wednesday,
        ["星期三"] = DayOfWeek.Wednesday,
        ["thu"] = DayOfWeek.Thursday,
        ["thursday"] = DayOfWeek.Thursday,
        ["4"] = DayOfWeek.Thursday,
        ["周四"] = DayOfWeek.Thursday,
        ["星期四"] = DayOfWeek.Thursday,
        ["fri"] = DayOfWeek.Friday,
        ["friday"] = DayOfWeek.Friday,
        ["5"] = DayOfWeek.Friday,
        ["周五"] = DayOfWeek.Friday,
        ["星期五"] = DayOfWeek.Friday,
        ["sat"] = DayOfWeek.Saturday,
        ["saturday"] = DayOfWeek.Saturday,
        ["6"] = DayOfWeek.Saturday,
        ["周六"] = DayOfWeek.Saturday,
        ["星期六"] = DayOfWeek.Saturday,
        ["sun"] = DayOfWeek.Sunday,
        ["sunday"] = DayOfWeek.Sunday,
        ["7"] = DayOfWeek.Sunday,
        ["0"] = DayOfWeek.Sunday,
        ["周日"] = DayOfWeek.Sunday,
        ["周天"] = DayOfWeek.Sunday,
        ["星期日"] = DayOfWeek.Sunday,
        ["星期天"] = DayOfWeek.Sunday
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
                Config.AreaNames = NormalizeAreaNames(Config.AreaNames);
                Config.AreaPerDayCounts = NormalizeAreaPerDayCounts(
                    Config.AreaNames,
                    Config.AreaPerDayCounts,
                    Config.PerDay);
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
                config.AreaNames = NormalizeAreaNames(config.AreaNames);
                config.AreaPerDayCounts = NormalizeAreaPerDayCounts(
                    config.AreaNames,
                    config.AreaPerDayCounts,
                    config.PerDay);
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
                Config.AreaNames = NormalizeAreaNames(Config.AreaNames);
                Config.AreaPerDayCounts = NormalizeAreaPerDayCounts(
                    Config.AreaNames,
                    Config.AreaPerDayCounts,
                    Config.PerDay);
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
        bool enableAutoRun,
        string autoRunDay,
        string autoRunTime,
        int perDay,
        bool skipWeekends,
        string dutyRule,
        bool startFromToday,
        int autoRunCoverageDays,
        string componentRefreshTime,
        string pythonPath,
        IEnumerable<string>? areaNames = null,
        IEnumerable<KeyValuePair<string, int>>? areaPerDayCounts = null,
        IEnumerable<string>? notificationTemplates = null,
        bool? dutyReminderEnabled = null,
        IEnumerable<string>? dutyReminderTimes = null,
        IEnumerable<string>? dutyReminderTemplates = null)
    {
        lock (_configLock)
        {
            Config.DecryptedApiKey = ResolveApiKeyInput(apiKey, Config.DecryptedApiKey);
            Config.BaseUrl = baseUrl;
            Config.Model = model;
            Config.EnableAutoRun = enableAutoRun;
            Config.AutoRunDay = NormalizeAutoRunDay(autoRunDay);
            Config.AutoRunTime = NormalizeTimeOrThrow(autoRunTime);
            Config.PerDay = Math.Clamp(perDay, 1, 30);
            Config.SkipWeekends = skipWeekends;
            Config.DutyRule = dutyRule;
            Config.StartFromToday = startFromToday;
            Config.AutoRunCoverageDays = Math.Clamp(autoRunCoverageDays, 1, 30);
            Config.ComponentRefreshTime = NormalizeTimeOrThrow(componentRefreshTime);
            Config.PythonPath = string.IsNullOrWhiteSpace(pythonPath) ? Config.PythonPath : pythonPath.Trim();
            Config.AreaNames = NormalizeAreaNames(areaNames ?? Config.AreaNames);
            Config.AreaPerDayCounts = NormalizeAreaPerDayCounts(
                Config.AreaNames,
                areaPerDayCounts ?? Config.AreaPerDayCounts,
                Config.PerDay);
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
        static CoreRunProgress BuildProgress(string phase, string message) => new(phase, message);

        if (string.IsNullOrWhiteSpace(instruction))
        {
            return CoreRunResult.Fail("Instruction cannot be empty.", code: "validation");
        }

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

        var areaNames = GetAreaNames();
        var areaPerDayCounts = GetAreaPerDayCounts();
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
            instruction,
            apply_mode = applyMode,
            start_from_today = Config.StartFromToday,
            days_to_generate = Config.AutoRunCoverageDays,
            per_day = Config.PerDay,
            skip_weekends = Config.SkipWeekends,
            duty_rule = Config.DutyRule,
            base_url = Config.BaseUrl,
            model = overrideModel ?? Config.Model,
            area_names = areaNames,
            area_per_day_counts = areaPerDayCounts
        };
        File.WriteAllText(inputPath, JsonSerializer.Serialize(inputData), Utf8NoBom);

        var startInfo = new ProcessStartInfo
        {
            FileName = pythonPath,
            Arguments = $"\"{scriptPath}\" --data-dir \"{_dataDir}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.EnvironmentVariables["DUTY_AGENT_API_KEY"] = apiKeyPlain;

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, args) => { if (args.Data != null) stdout.AppendLine(args.Data); };
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
        lock (_configLock)
        {
            return NormalizeAreaNames(Config.AreaNames);
        }
    }

    public Dictionary<string, int> GetAreaPerDayCounts()
    {
        lock (_configLock)
        {
            var areaNames = NormalizeAreaNames(Config.AreaNames);
            return NormalizeAreaPerDayCounts(areaNames, Config.AreaPerDayCounts, Config.PerDay);
        }
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
        var areaNames = GetAreaNames();
        var assignments = BuildAreaAssignments(item, areaNames);
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
        foreach (var entry in rosterEntries ?? [])
        {
            var baseName = (entry.Name ?? string.Empty).Trim();
            if (baseName.Length == 0)
            {
                continue;
            }

            var uniqueName = ToUniqueRosterName(baseName, nameCounts);
            normalized.Add(new RosterEntry
            {
                Id = normalized.Count + 1,
                Name = uniqueName,
                Active = entry.Active
            });
        }

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
            var state = JsonSerializer.Deserialize<DutyState>(json) ?? new DutyState();
            NormalizeLegacyState(state);
            return state;
        }
        catch
        {
            return new DutyState();
        }
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

            if (!Config.EnableAutoRun) return;

            if (Config.LastAutoRunDate == now.ToString("yyyy-MM-dd")) return;
            if ((now - _lastAutoRunAttempt).TotalMinutes < AutoRunRetryCooldownMinutes) return;

            if (!IsAutoRunDayMatched(Config.AutoRunDay, now.DayOfWeek)) return;
            if (!TimeSpan.TryParse(Config.AutoRunTime, out var targetTime) || now.TimeOfDay < targetTime) return;

            _lastAutoRunAttempt = now;
            if (RunCoreAgent(AutoRunInstruction))
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

    private static bool IsAutoRunDayMatched(string autoRunDay, DayOfWeek currentDay)
    {
        if (string.IsNullOrWhiteSpace(autoRunDay)) return true;
        return TryParseAutoRunDay(autoRunDay, out var parsedDay) && parsedDay == currentDay;
    }

    private static bool TryParseAutoRunDay(string autoRunDay, out DayOfWeek day)
    {
        var normalized = autoRunDay.Trim();
        if (AutoRunDayAliases.TryGetValue(normalized, out day))
        {
            return true;
        }

        return Enum.TryParse(normalized, ignoreCase: true, out day);
    }

    private static string NormalizeAutoRunDay(string autoRunDay)
    {
        return TryParseAutoRunDay(autoRunDay, out var day)
            ? day.ToString()
            : DayOfWeek.Monday.ToString();
    }

    private void NormalizeLegacyState(DutyState state)
    {
        var areaNames = GetAreaNames();
        foreach (var item in state.SchedulePool)
        {
            var assignments = BuildAreaAssignments(item, areaNames);
            item.AreaAssignments = assignments;

            var firstArea = areaNames[0];
            var secondArea = areaNames.Count > 1 ? areaNames[1] : firstArea;
            item.ClassroomStudents = assignments.TryGetValue(firstArea, out var firstStudents) ? [.. firstStudents] : [];
            item.CleaningAreaStudents = assignments.TryGetValue(secondArea, out var secondStudents) ? [.. secondStudents] : [];

            if (item.Students.Count == 0 && item.ClassroomStudents.Count > 0)
            {
                item.Students = [.. item.ClassroomStudents];
            }
        }
    }

    private static Dictionary<string, List<string>> BuildAreaAssignments(
        SchedulePoolItem item,
        IReadOnlyList<string> areaNames)
    {
        var assignments = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        if (item.AreaAssignments.Count > 0)
        {
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
        }

        var firstArea = areaNames[0];
        var secondArea = areaNames.Count > 1 ? areaNames[1] : firstArea;

        var classroomStudents = NormalizeStudents(item.ClassroomStudents);
        var cleaningStudents = NormalizeStudents(item.CleaningAreaStudents);
        var legacyStudents = NormalizeStudents(item.Students);

        if (classroomStudents.Count > 0 && !assignments.ContainsKey(firstArea))
        {
            assignments[firstArea] = classroomStudents;
        }

        if (cleaningStudents.Count > 0 && !assignments.ContainsKey(secondArea))
        {
            assignments[secondArea] = cleaningStudents;
        }

        if (assignments.Count == 0 && legacyStudents.Count > 0)
        {
            assignments[firstArea] = legacyStudents;
        }

        return assignments;
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

    private static Dictionary<string, int> NormalizeAreaPerDayCounts(
        IReadOnlyList<string> areaNames,
        IEnumerable<KeyValuePair<string, int>>? rawCounts,
        int fallbackPerDay)
    {
        var fallback = Math.Clamp(fallbackPerDay, 1, 30);
        var source = new Dictionary<string, int>(StringComparer.Ordinal);

        if (rawCounts != null)
        {
            foreach (var pair in rawCounts)
            {
                var area = (pair.Key ?? string.Empty).Trim();
                if (area.Length == 0)
                {
                    continue;
                }

                source[area] = Math.Clamp(pair.Value, 1, 30);
            }
        }

        var normalized = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var area in areaNames)
        {
            var key = (area ?? string.Empty).Trim();
            if (key.Length == 0)
            {
                continue;
            }

            normalized[key] = source.TryGetValue(key, out var count)
                ? Math.Clamp(count, 1, 30)
                : fallback;
        }

        if (normalized.Count == 0)
        {
            normalized[DefaultAreaClassroom] = fallback;
            normalized[DefaultAreaCleaning] = fallback;
        }

        return normalized;
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
        var areaPerDayCounts = GetAreaPerDayCounts();
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
        var fallbackText = item is null
            ? $"{scene}\uFF1A{dateText} {timeText}\uFF0C\u4ECA\u65E5\u6682\u65E0\u503C\u65E5\u5B89\u6392\u3002"
            : $"{scene}\uFF1A{dateText} {timeText}\uFF0C{string.Join("\uFF1B", assignmentSegments)}";

        var placeholders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["scene"] = scene,
            ["status"] = status,
            ["date"] = dateText,
            ["time"] = timeText,
            ["areas"] = string.Join("\u3001", areaNames),
            ["duty_students"] = dutyStudents.Count > 0 ? string.Join("\u3001", dutyStudents) : "\u6682\u65E0",
            ["assignments"] = assignmentSegments.Count > 0
                ? string.Join("\uFF1B", assignmentSegments)
                : "\u6682\u65E0\u503C\u65E5\u5B89\u6392",
            ["days"] = Config.AutoRunCoverageDays.ToString(),
            ["per_day"] = Config.PerDay.ToString(),
            ["mode"] = "duty_reminder",
            ["instruction"] = string.Empty,
            ["message"] = item is null
                ? "\u4ECA\u65E5\u6682\u65E0\u503C\u65E5\u5B89\u6392"
                : "\u8BF7\u6309\u5B89\u6392\u5B8C\u6210\u503C\u65E5"
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

        public CoreRunProgress(string phase, string message)
        {
            Phase = phase;
            Message = message;
        }
    }
}
