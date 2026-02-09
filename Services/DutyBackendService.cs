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
                SaveConfig();
                return;
            }

            try
            {
                var json = File.ReadAllText(_configPath, Encoding.UTF8);
                var config = JsonSerializer.Deserialize<DutyConfig>(json) ?? new DutyConfig();

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
            File.WriteAllText(_configPath, json, Encoding.UTF8);
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
        string pythonPath)
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
            SaveConfig();
        }
    }

    public bool RunCoreAgent(string instruction, string applyMode = "append", string? overrideModel = null)
    {
        if (string.IsNullOrWhiteSpace(instruction))
        {
            throw new ArgumentException("Instruction must not be empty.", nameof(instruction));
        }

        LoadConfig();

        var pythonPath = ValidatePythonPath(Config.PythonPath, PluginBaseDirectory, _basePath);
        var inputPath = Path.Combine(_dataDir, "ipc_input.json");
        var resultPath = Path.Combine(_dataDir, "ipc_result.json");
        var scriptPath = Path.Combine(_basePath, "core.py");

        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException($"Core script not found: {scriptPath}");
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
            api_key_plain = Config.DecryptedApiKey,
            base_url = Config.BaseUrl,
            model = overrideModel ?? Config.Model
        };
        File.WriteAllText(inputPath, JsonSerializer.Serialize(inputData), Encoding.UTF8);

        var startInfo = new ProcessStartInfo
        {
            FileName = pythonPath,
            Arguments = $"\"{scriptPath}\" --data-dir \"{_dataDir}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, args) => { if (args.Data != null) stdout.AppendLine(args.Data); };
        process.ErrorDataReceived += (_, args) => { if (args.Data != null) stderr.AppendLine(args.Data); };

        if (!process.Start()) return false;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();

        if (process.ExitCode != 0 || !File.Exists(resultPath))
        {
            Debug.WriteLine($"Core agent failed. ExitCode={process.ExitCode}\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
            return false;
        }

        try
        {
            var resultJson = File.ReadAllText(resultPath, Encoding.UTF8);
            using var doc = JsonDocument.Parse(resultJson);
            return doc.RootElement.TryGetProperty("status", out var status) &&
                   string.Equals(status.GetString(), "success", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public string GetRosterPath() => Path.Combine(_dataDir, "roster.csv");

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

    private static void NormalizeLegacyState(DutyState state)
    {
        foreach (var item in state.SchedulePool)
        {
            if (item.ClassroomStudents.Count == 0 && item.Students.Count > 0)
            {
                item.ClassroomStudents = [.. item.Students];
            }

            if (item.CleaningAreaStudents.Count == 0 && item.Students.Count > 0)
            {
                item.CleaningAreaStudents = [.. item.Students];
            }
        }
    }
}
