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
    private const string DefaultAreaClassroom = "\u6559\u5BA4";
    private const string DefaultAreaCleaning = "\u6E05\u6D01\u533A";
    private const string DefaultNotificationTemplate =
        "{scene}{status}\uFF0C\u65E5\u671F\uFF1A{date}\uFF0C\u533A\u57DF\uFF1A{areas}";
    private static readonly string PluginBaseDirectory =
        Path.GetDirectoryName(typeof(DutyBackendService).Assembly.Location) ?? AppContext.BaseDirectory;
    private static readonly Dictionary<string, DayOfWeek> AutoRunDayAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mon"] = DayOfWeek.Monday,
        ["monday"] = DayOfWeek.Monday,
        ["1"] = DayOfWeek.Monday,
        ["周一"] = DayOfWeek.Monday,
        ["星期一"] = DayOfWeek.Monday,
        ["鍛ㄤ竴"] = DayOfWeek.Monday,
        ["tue"] = DayOfWeek.Tuesday,
        ["tuesday"] = DayOfWeek.Tuesday,
        ["2"] = DayOfWeek.Tuesday,
        ["周二"] = DayOfWeek.Tuesday,
        ["星期二"] = DayOfWeek.Tuesday,
        ["鍛ㄤ簩"] = DayOfWeek.Tuesday,
        ["wed"] = DayOfWeek.Wednesday,
        ["wednesday"] = DayOfWeek.Wednesday,
        ["3"] = DayOfWeek.Wednesday,
        ["周三"] = DayOfWeek.Wednesday,
        ["星期三"] = DayOfWeek.Wednesday,
        ["鍛ㄤ笁"] = DayOfWeek.Wednesday,
        ["thu"] = DayOfWeek.Thursday,
        ["thursday"] = DayOfWeek.Thursday,
        ["4"] = DayOfWeek.Thursday,
        ["周四"] = DayOfWeek.Thursday,
        ["星期四"] = DayOfWeek.Thursday,
        ["鍛ㄥ洓"] = DayOfWeek.Thursday,
        ["fri"] = DayOfWeek.Friday,
        ["friday"] = DayOfWeek.Friday,
        ["5"] = DayOfWeek.Friday,
        ["周五"] = DayOfWeek.Friday,
        ["星期五"] = DayOfWeek.Friday,
        ["鍛ㄤ簲"] = DayOfWeek.Friday,
        ["sat"] = DayOfWeek.Saturday,
        ["saturday"] = DayOfWeek.Saturday,
        ["6"] = DayOfWeek.Saturday,
        ["周六"] = DayOfWeek.Saturday,
        ["星期六"] = DayOfWeek.Saturday,
        ["鍛ㄥ叚"] = DayOfWeek.Saturday,
        ["sun"] = DayOfWeek.Sunday,
        ["sunday"] = DayOfWeek.Sunday,
        ["7"] = DayOfWeek.Sunday,
        ["0"] = DayOfWeek.Sunday,
        ["周日"] = DayOfWeek.Sunday,
        ["周天"] = DayOfWeek.Sunday,
        ["星期日"] = DayOfWeek.Sunday,
        ["星期天"] = DayOfWeek.Sunday,
        ["鍛ㄦ棩"] = DayOfWeek.Sunday
    };

    private readonly string _basePath = Path.Combine(PluginBaseDirectory, "Assets_Duty");
    private readonly string _dataDir;
    private readonly string _configPath;
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _debounceTimer;
    private readonly Timer _autoRunTimer;
    private readonly object _configLock = new();
    private DateTime _lastAutoRunAttempt = DateTime.MinValue;
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public event EventHandler? ScheduleUpdated;

    public DutyConfig Config { get; private set; } = new();

    public DutyBackendService()
    {
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
                Config.NotificationTemplates = NormalizeNotificationTemplates(Config.NotificationTemplates);
                SaveConfig();
                return;
            }

            try
            {
                var json = File.ReadAllText(_configPath, Encoding.UTF8);
                var config = JsonSerializer.Deserialize<DutyConfig>(json) ?? new DutyConfig();
                config.AreaNames = NormalizeAreaNames(config.AreaNames);
                config.NotificationTemplates = NormalizeNotificationTemplates(config.NotificationTemplates);

                if (!string.IsNullOrWhiteSpace(config.EncryptedApiKey))
                {
                    var plainApiKey = config.DecryptedApiKey;
                    if (string.Equals(plainApiKey, config.EncryptedApiKey, StringComparison.Ordinal))
                    {
                        config.DecryptedApiKey = plainApiKey;
                        Config = config;
                        SaveConfig();
                        return;
                    }
                }

                Config = config;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"LoadConfig Error: {ex.Message}");
                Config = new DutyConfig();
                Config.AreaNames = NormalizeAreaNames(Config.AreaNames);
                Config.NotificationTemplates = NormalizeNotificationTemplates(Config.NotificationTemplates);
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
        IEnumerable<string>? notificationTemplates = null)
    {
        lock (_configLock)
        {
            Config.DecryptedApiKey = apiKey;
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
            Config.NotificationTemplates =
                NormalizeNotificationTemplates(notificationTemplates ?? Config.NotificationTemplates);
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

    public (bool Success, string Message) RunCoreAgentWithMessage(
        string instruction,
        string applyMode = "append",
        string? overrideModel = null)
    {
        if (string.IsNullOrWhiteSpace(instruction))
        {
            return (false, "排班指令不能为空。");
        }

        LoadConfig();
        var apiKeyPlain = Config.DecryptedApiKey.Trim();
        if (string.IsNullOrWhiteSpace(apiKeyPlain))
        {
            return (false, "API Key 为空，或无法在当前设备解密。");
        }

        var areaNames = GetAreaNames();
        var pythonPath = ValidatePythonPath(Config.PythonPath, PluginBaseDirectory, _basePath);
        var inputPath = Path.Combine(_dataDir, "ipc_input.json");
        var resultPath = Path.Combine(_dataDir, "ipc_result.json");
        var scriptPath = Path.Combine(_basePath, "core.py");

        if (!File.Exists(scriptPath))
        {
            return (false, $"未找到核心脚本：{scriptPath}");
        }

        if (File.Exists(resultPath))
        {
            File.Delete(resultPath);
        }

        var inputData = new
        {
            instruction,
            apply_mode = applyMode,
            days_to_generate = Config.AutoRunCoverageDays,
            per_day = Config.PerDay,
            skip_weekends = Config.SkipWeekends,
            duty_rule = Config.DutyRule,
            start_from_today = Config.StartFromToday,
            base_url = Config.BaseUrl,
            model = overrideModel ?? Config.Model,
            area_names = areaNames
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

        if (!process.Start())
        {
            return (false, "无法启动 Python 进程。");
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();

        var stderrText = stderr.ToString().Trim();
        var stdoutText = stdout.ToString().Trim();

        if (process.ExitCode != 0)
        {
            var message = TryReadCoreErrorMessage(resultPath);
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
                message = "核心进程执行失败。";
            }

            return (false, $"排班失败（ExitCode={process.ExitCode}）：{message}");
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
                message = "未生成 ipc_result.json。";
            }

            return (false, message);
        }

        try
        {
            var resultJson = File.ReadAllText(resultPath, Encoding.UTF8);
            using var doc = JsonDocument.Parse(resultJson);
            var success = doc.RootElement.TryGetProperty("status", out var status) &&
                          string.Equals(status.GetString(), "success", StringComparison.OrdinalIgnoreCase);
            if (success)
            {
                return (true, "执行成功。");
            }

            var errorMessage = doc.RootElement.TryGetProperty("message", out var messageElement)
                ? (messageElement.GetString() ?? string.Empty).Trim()
                : string.Empty;
            if (errorMessage.Length == 0)
            {
                errorMessage = "核心进程返回非 success 状态。";
            }

            return (false, errorMessage);
        }
        catch (Exception ex)
        {
            return (false, $"解析结果文件失败：{ex.Message}");
        }
    }

    private static string? TryReadCoreErrorMessage(string resultPath)
    {
        try
        {
            if (!File.Exists(resultPath))
            {
                return null;
            }

            var resultJson = File.ReadAllText(resultPath, Encoding.UTF8);
            using var doc = JsonDocument.Parse(resultJson);
            if (!doc.RootElement.TryGetProperty("message", out var messageElement))
            {
                return null;
            }

            var message = (messageElement.GetString() ?? string.Empty).Trim();
            return message.Length == 0 ? null : message;
        }
        catch
        {
            return null;
        }
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

    public List<string> GetNotificationTemplates()
    {
        lock (_configLock)
        {
            return NormalizeNotificationTemplates(Config.NotificationTemplates);
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

        var map = new Dictionary<int, RosterEntry>();
        foreach (var entry in rosterEntries ?? [])
        {
            var id = entry.Id;
            var name = (entry.Name ?? string.Empty).Trim();
            if (id <= 0 || name.Length == 0)
            {
                continue;
            }

            map[id] = new RosterEntry
            {
                Id = id,
                Name = name,
                Active = entry.Active
            };
        }

        var builder = new StringBuilder();
        builder.AppendLine("id,name,active");
        foreach (var item in map.Values.OrderBy(x => x.Id))
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
            if (!Config.EnableAutoRun) return;

            var now = DateTime.Now;
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

    private static string EscapeCsv(string text)
    {
        if (text.IndexOfAny([',', '"', '\r', '\n']) < 0)
        {
            return text;
        }

        return $"\"{text.Replace("\"", "\"\"")}\"";
    }
}
