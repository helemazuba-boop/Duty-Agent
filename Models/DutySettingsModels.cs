using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DutyAgent.Models;

public sealed class DutyEditableHostSettingsDocument
{
    [JsonPropertyName("auto_run_mode")]
    public string AutoRunMode { get; set; } = "Off";

    [JsonPropertyName("auto_run_parameter")]
    public string AutoRunParameter { get; set; } = "Monday";

    [JsonPropertyName("auto_run_time")]
    public string AutoRunTime { get; set; } = "08:00";

    [JsonPropertyName("auto_run_trigger_notification_enabled")]
    public bool AutoRunTriggerNotificationEnabled { get; set; } = true;

    [JsonPropertyName("duty_reminder_enabled")]
    public bool DutyReminderEnabled { get; set; }

    [JsonPropertyName("duty_reminder_times")]
    public List<string> DutyReminderTimes { get; set; } = ["07:40"];

    [JsonPropertyName("enable_mcp")]
    public bool EnableMcp { get; set; }

    [JsonPropertyName("enable_webview_debug_layer")]
    public bool EnableWebViewDebugLayer { get; set; }

    [JsonPropertyName("component_refresh_time")]
    public string ComponentRefreshTime { get; set; } = "08:00";

    [JsonPropertyName("notification_duration_seconds")]
    public int NotificationDurationSeconds { get; set; } = 8;
}

public sealed class DutyEditableBackendSettingsDocument
{
    [JsonPropertyName("selected_plan_id")]
    public string SelectedPlanId { get; set; } = DutyBackendModeIds.Standard;

    [JsonPropertyName("plan_presets")]
    public List<DutyPlanPreset> PlanPresets { get; set; } = [];

    [JsonPropertyName("duty_rule")]
    public string DutyRule { get; set; } = string.Empty;
}

public sealed class DutySettingsDocument
{
    [JsonPropertyName("host_version")]
    public int HostVersion { get; set; } = 1;

    [JsonPropertyName("backend_version")]
    public int BackendVersion { get; set; } = 1;

    [JsonPropertyName("host")]
    public DutyEditableHostSettingsDocument Host { get; set; } = new();

    [JsonPropertyName("backend")]
    public DutyEditableBackendSettingsDocument Backend { get; set; } = new();
}

public sealed class DutySettingsDraftDocument
{
    [JsonPropertyName("draft_id")]
    public string DraftId { get; set; } = string.Empty;

    [JsonPropertyName("saved_at_utc")]
    public DateTimeOffset SavedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("host")]
    public DutyEditableHostSettingsDocument Host { get; set; } = new();

    [JsonPropertyName("backend")]
    public DutyEditableBackendSettingsDocument Backend { get; set; } = new();
}

public sealed class DutySettingsExpectedVersions
{
    [JsonPropertyName("host_version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? HostVersion { get; set; }

    [JsonPropertyName("backend_version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BackendVersion { get; set; }
}

public sealed class DutyEditableHostSettingsPatch
{
    [JsonPropertyName("auto_run_mode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AutoRunMode { get; set; }

    [JsonPropertyName("auto_run_parameter")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AutoRunParameter { get; set; }

    [JsonPropertyName("auto_run_time")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AutoRunTime { get; set; }

    [JsonPropertyName("auto_run_trigger_notification_enabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AutoRunTriggerNotificationEnabled { get; set; }

    [JsonPropertyName("duty_reminder_enabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? DutyReminderEnabled { get; set; }

    [JsonPropertyName("duty_reminder_times")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? DutyReminderTimes { get; set; }

    [JsonPropertyName("enable_mcp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? EnableMcp { get; set; }

    [JsonPropertyName("enable_webview_debug_layer")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? EnableWebViewDebugLayer { get; set; }

    [JsonPropertyName("component_refresh_time")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ComponentRefreshTime { get; set; }

    [JsonPropertyName("notification_duration_seconds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? NotificationDurationSeconds { get; set; }
}

public sealed class DutyEditableBackendSettingsPatch
{
    [JsonPropertyName("selected_plan_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SelectedPlanId { get; set; }

    [JsonPropertyName("plan_presets")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<DutyPlanPreset>? PlanPresets { get; set; }

    [JsonPropertyName("duty_rule")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DutyRule { get; set; }
}

public sealed class DutySettingsPatchChanges
{
    [JsonPropertyName("host")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DutyEditableHostSettingsPatch? Host { get; set; }

    [JsonPropertyName("backend")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DutyEditableBackendSettingsPatch? Backend { get; set; }
}

public sealed class DutySettingsPatchRequest
{
    [JsonPropertyName("expected")]
    public DutySettingsExpectedVersions Expected { get; set; } = new();

    [JsonPropertyName("changes")]
    public DutySettingsPatchChanges Changes { get; set; } = new();
}

public sealed class DutySettingsAppliedChanges
{
    [JsonPropertyName("host")]
    public DutyEditableHostSettingsDocument? Host { get; set; }

    [JsonPropertyName("backend")]
    public DutyEditableBackendSettingsDocument? Backend { get; set; }
}

public sealed class DutySettingsVersions
{
    [JsonPropertyName("host")]
    public int Host { get; set; } = 1;

    [JsonPropertyName("backend")]
    public int Backend { get; set; } = 1;
}

public sealed class DutySettingsMutationResult
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("restart_required")]
    public bool RestartRequired { get; set; }

    [JsonPropertyName("document")]
    public DutySettingsDocument Document { get; set; } = new();

    [JsonPropertyName("applied")]
    public DutySettingsAppliedChanges Applied { get; set; } = new();

    [JsonPropertyName("versions")]
    public DutySettingsVersions Versions { get; set; } = new();

    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; set; } = [];

    [JsonPropertyName("trace_id")]
    public string TraceId { get; set; } = string.Empty;
}
