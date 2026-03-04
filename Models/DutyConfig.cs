using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using DutyAgent.Services;

namespace DutyAgent.Models;

public partial class DutyConfig : ObservableObject
{
    private string _decryptedApiKey = string.Empty;

    [ObservableProperty]
    [property: JsonPropertyName("python_path")]
    private string _pythonPath = @".\Assets_Duty\python-embed\python.exe";

    [ObservableProperty]
    [property: JsonPropertyName("api_key")]
    private string _encryptedApiKey = string.Empty;

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

    [ObservableProperty]
    [property: JsonPropertyName("base_url")]
    private string _baseUrl = "https://integrate.api.nvidia.com/v1";

    [ObservableProperty]
    [property: JsonPropertyName("model")]
    private string _model = "moonshotai/kimi-k2-thinking";

    [ObservableProperty]
    [property: JsonPropertyName("auto_run_mode")]
    private string _autoRunMode = "Off";

    [ObservableProperty]
    [property: JsonPropertyName("auto_run_parameter")]
    private string _autoRunParameter = "Monday";

    [ObservableProperty]
    [property: JsonPropertyName("enable_mcp")]
    private bool _enableMcp = false;

    [ObservableProperty]
    [property: JsonPropertyName("enable_webview_debug_layer")]
    private bool _enableWebViewDebugLayer = false;

    [ObservableProperty]
    [property: JsonPropertyName("auto_run_time")]
    private string _autoRunTime = "08:00";

    [ObservableProperty]
    [property: JsonPropertyName("auto_run_trigger_notification_enabled")]
    private bool _autoRunTriggerNotificationEnabled = true;

    [ObservableProperty]
    [property: JsonPropertyName("per_day")]
    private int _perDay = 2;

    [ObservableProperty]
    [property: JsonPropertyName("duty_rule")]
    private string _dutyRule = string.Empty;

    [ObservableProperty]
    [property: JsonPropertyName("auto_run_retry_times")]
    private int _autoRunRetryTimes = 3;

    [ObservableProperty]
    [property: JsonPropertyName("ai_consecutive_failures")]
    private int _aiConsecutiveFailures = 0;

    [ObservableProperty]
    [property: JsonPropertyName("last_auto_run_date")]
    private string _lastAutoRunDate = string.Empty;

    [ObservableProperty]
    [property: JsonPropertyName("component_refresh_time")]
    private string _componentRefreshTime = "08:00";

    [ObservableProperty]
    [property: JsonPropertyName("notification_templates")]
    private List<string> _notificationTemplates =
        ["{scene}{status}\uFF0C\u65E5\u671F\uFF1A{date}\uFF0C\u533A\u57DF\uFF1A{areas}"];

    [ObservableProperty]
    [property: JsonPropertyName("duty_reminder_enabled")]
    private bool _dutyReminderEnabled = false;

    [ObservableProperty]
    [property: JsonPropertyName("duty_reminder_times")]
    private List<string> _dutyReminderTimes = ["07:40"];

    [ObservableProperty]
    [property: JsonPropertyName("duty_reminder_templates")]
    private List<string> _dutyReminderTemplates =
        ["\u503C\u65E5\u63D0\u9192\uFF1A{date} {time}\uFF0C{assignments}"];
}
