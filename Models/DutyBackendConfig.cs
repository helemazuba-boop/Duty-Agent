using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DutyAgent.Models;

public static class DutyBackendModeIds
{
    public const string Standard = "standard";
    public const string Campus6Agent = "campus_6agent";
    public const string IncrementalSmall = "incremental_small";
}

public sealed class DutyPlanPreset
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = DutyBackendModeIds.Standard;

    [JsonPropertyName("name")]
    public string Name { get; set; } = "\u6807\u51c6";

    [JsonPropertyName("mode_id")]
    public string ModeId { get; set; } = DutyBackendModeIds.Standard;

    [JsonPropertyName("api_key")]
    public string ApiKey { get; set; } = string.Empty;

    [JsonPropertyName("base_url")]
    public string BaseUrl { get; set; } = "https://integrate.api.nvidia.com/v1";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "moonshotai/kimi-k2-thinking";

    [JsonPropertyName("model_profile")]
    public string ModelProfile { get; set; } = "auto";

    [JsonPropertyName("provider_hint")]
    public string ProviderHint { get; set; } = string.Empty;

    [JsonPropertyName("multi_agent_execution_mode")]
    public string MultiAgentExecutionMode { get; set; } = "auto";
}

public sealed class DutyBackendConfig
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("api_key")]
    public string ApiKey { get; set; } = string.Empty;

    [JsonPropertyName("base_url")]
    public string BaseUrl { get; set; } = "https://integrate.api.nvidia.com/v1";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "moonshotai/kimi-k2-thinking";

    [JsonPropertyName("model_profile")]
    public string ModelProfile { get; set; } = "auto";

    [JsonPropertyName("orchestration_mode")]
    public string OrchestrationMode { get; set; } = "single_pass";

    [JsonPropertyName("multi_agent_execution_mode")]
    public string MultiAgentExecutionMode { get; set; } = "auto";

    [JsonPropertyName("single_pass_strategy")]
    public string SinglePassStrategy { get; set; } = "auto";

    [JsonPropertyName("provider_hint")]
    public string ProviderHint { get; set; } = string.Empty;

    [JsonPropertyName("selected_plan_id")]
    public string SelectedPlanId { get; set; } = DutyBackendModeIds.Standard;

    [JsonPropertyName("plan_presets")]
    public List<DutyPlanPreset> PlanPresets { get; set; } = [];

    [JsonPropertyName("duty_rule")]
    public string DutyRule { get; set; } = string.Empty;
}

public sealed class DutyBackendConfigPatch
{
    [JsonPropertyName("expected_version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ExpectedVersion { get; set; }

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

public sealed class DutyBackendSnapshot
{
    [JsonPropertyName("config")]
    public DutyBackendConfig Config { get; set; } = new();

    [JsonPropertyName("roster")]
    public List<RosterEntry> Roster { get; set; } = [];

    [JsonPropertyName("state")]
    public DutyState State { get; set; } = new();
}

public sealed class DutyScheduleEntrySaveRequest
{
    [JsonPropertyName("source_date")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceDate { get; set; }

    [JsonPropertyName("target_date")]
    public string TargetDate { get; set; } = string.Empty;

    [JsonPropertyName("day")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Day { get; set; }

    [JsonPropertyName("area_assignments")]
    public Dictionary<string, List<string>> AreaAssignments { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("note")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; set; }

    [JsonPropertyName("create_if_missing")]
    public bool CreateIfMissing { get; set; }

    [JsonPropertyName("ledger_mode")]
    public string LedgerMode { get; set; } = "record";
}

public sealed class DutyScheduleEntrySaveResponse
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("ledger_mode")]
    public string LedgerMode { get; set; } = "record";

    [JsonPropertyName("ledger_applied")]
    public bool LedgerApplied { get; set; }

    [JsonPropertyName("snapshot")]
    public DutyBackendSnapshot Snapshot { get; set; } = new();
}
