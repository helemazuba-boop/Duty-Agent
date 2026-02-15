using System.Text.Json.Serialization;
using DutyAgent.Services;

namespace DutyAgent.Models;

public class DutyConfig
{
    private string _decryptedApiKey = string.Empty;

    [JsonPropertyName("python_path")]
    public string PythonPath { get; set; } = @".\Assets_Duty\python-embed\python.exe";

    [JsonPropertyName("api_key")]
    public string EncryptedApiKey { get; set; } = string.Empty;

    [JsonIgnore]
    public string DecryptedApiKey
    {
        get
        {
            if (!string.IsNullOrEmpty(_decryptedApiKey))
            {
                return _decryptedApiKey;
            }

            if (string.IsNullOrWhiteSpace(EncryptedApiKey))
            {
                return string.Empty;
            }

            try
            {
                _decryptedApiKey = SecurityHelper.DecryptString(EncryptedApiKey);
            }
            catch
            {
                _decryptedApiKey = string.Empty;
            }

            return _decryptedApiKey;
        }
        set
        {
            _decryptedApiKey = value ?? string.Empty;
            EncryptedApiKey = string.IsNullOrWhiteSpace(_decryptedApiKey)
                ? string.Empty
                : SecurityHelper.EncryptString(_decryptedApiKey);
        }
    }

    [JsonPropertyName("base_url")]
    public string BaseUrl { get; set; } = "https://integrate.api.nvidia.com/v1";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "moonshotai/kimi-k2-thinking";

    [JsonPropertyName("enable_auto_run")]
    public bool EnableAutoRun { get; set; }

    [JsonPropertyName("enable_mcp")]
    public bool EnableMcp { get; set; } = false;

    [JsonPropertyName("enable_webview_debug_layer")]
    public bool EnableWebViewDebugLayer { get; set; } = false;

    [JsonPropertyName("auto_run_day")]
    public string AutoRunDay { get; set; } = "Monday";

    [JsonPropertyName("auto_run_time")]
    public string AutoRunTime { get; set; } = "08:00";

    [JsonPropertyName("auto_run_coverage_days")]
    public int AutoRunCoverageDays { get; set; } = 5;

    [JsonPropertyName("per_day")]
    public int PerDay { get; set; } = 2;

    [JsonPropertyName("skip_weekends")]
    public bool SkipWeekends { get; set; } = true;

    [JsonPropertyName("duty_rule")]
    public string DutyRule { get; set; } = string.Empty;

    [JsonPropertyName("start_from_today")]
    public bool StartFromToday { get; set; } = false;

    [JsonPropertyName("auto_run_retry_times")]
    public int AutoRunRetryTimes { get; set; } = 3;

    [JsonPropertyName("ai_consecutive_failures")]
    public int AiConsecutiveFailures { get; set; } = 0;

    [JsonPropertyName("last_auto_run_date")]
    public string LastAutoRunDate { get; set; } = string.Empty;

    [JsonPropertyName("component_refresh_time")]
    public string ComponentRefreshTime { get; set; } = "08:00";

    [JsonPropertyName("area_names")]
    public List<string> AreaNames { get; set; } = ["\u6559\u5BA4", "\u6E05\u6D01\u533A"];

    [JsonPropertyName("area_per_day_counts")]
    public Dictionary<string, int> AreaPerDayCounts { get; set; } = new(StringComparer.Ordinal)
    {
        ["\u6559\u5BA4"] = 2,
        ["\u6E05\u6D01\u533A"] = 2
    };

    [JsonPropertyName("notification_templates")]
    public List<string> NotificationTemplates { get; set; } =
        ["{scene}{status}\uFF0C\u65E5\u671F\uFF1A{date}\uFF0C\u533A\u57DF\uFF1A{areas}"];

    [JsonPropertyName("duty_reminder_enabled")]
    public bool DutyReminderEnabled { get; set; } = false;

    [JsonPropertyName("duty_reminder_times")]
    public List<string> DutyReminderTimes { get; set; } = ["07:40"];

    [JsonPropertyName("duty_reminder_templates")]
    public List<string> DutyReminderTemplates { get; set; } =
        ["\u503C\u65E5\u63D0\u9192\uFF1A{date} {time}\uFF0C{assignments}"];
}
