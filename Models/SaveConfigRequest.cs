using System.Text.Json.Serialization;

namespace DutyAgent.Models;

/// <summary>
/// DTO for <see cref="DutyAgent.Services.DutyBackendService.SaveUserConfig"/>.
/// All properties are nullable — <c>null</c> means "keep current value".
/// </summary>
public sealed class SaveConfigRequest
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

    [JsonPropertyName("enable_mcp")]
    public bool? EnableMcp { get; set; }

    [JsonPropertyName("enable_webview_debug_layer")]
    public bool? EnableWebViewDebugLayer { get; set; }

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
