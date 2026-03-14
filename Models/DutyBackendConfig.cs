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
    public string Name { get; set; } = "标准";

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

public sealed class DutyModelPreset
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "default";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "默认模型";

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
}

public sealed class DutyModeProfile
{
    [JsonPropertyName("mode_id")]
    public string ModeId { get; set; } = DutyBackendModeIds.Standard;

    [JsonPropertyName("preset_id")]
    public string PresetId { get; set; } = "default";

    [JsonPropertyName("orchestration_mode")]
    public string OrchestrationMode { get; set; } = "single_pass";

    [JsonPropertyName("multi_agent_execution_mode")]
    public string MultiAgentExecutionMode { get; set; } = "auto";

    [JsonPropertyName("single_pass_strategy")]
    public string SinglePassStrategy { get; set; } = "auto";
}

public sealed class DutyBackendConfig
{
    [JsonPropertyName("api_key")]
    public string ApiKey { get; set; } = string.Empty;

    [JsonPropertyName("base_url")]
    public string BaseUrl { get; set; } = "https://integrate.api.nvidia.com/v1";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "moonshotai/kimi-k2-thinking";

    [JsonPropertyName("model_profile")]
    public string ModelProfile { get; set; } = "auto";

    [JsonPropertyName("orchestration_mode")]
    public string OrchestrationMode { get; set; } = "auto";

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

    [JsonPropertyName("model_presets")]
    public List<DutyModelPreset> ModelPresets { get; set; } = [];

    [JsonPropertyName("mode_profiles")]
    public List<DutyModeProfile> ModeProfiles { get; set; } = [];

    [JsonPropertyName("per_day")]
    public int PerDay { get; set; } = 2;

    [JsonPropertyName("duty_rule")]
    public string DutyRule { get; set; } = string.Empty;
}

public sealed class DutyBackendConfigPatch
{
    [JsonPropertyName("api_key")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ApiKey { get; set; }

    [JsonPropertyName("base_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BaseUrl { get; set; }

    [JsonPropertyName("model")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Model { get; set; }

    [JsonPropertyName("model_profile")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModelProfile { get; set; }

    [JsonPropertyName("orchestration_mode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OrchestrationMode { get; set; }

    [JsonPropertyName("multi_agent_execution_mode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MultiAgentExecutionMode { get; set; }

    [JsonPropertyName("single_pass_strategy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SinglePassStrategy { get; set; }

    [JsonPropertyName("provider_hint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProviderHint { get; set; }

    [JsonPropertyName("selected_plan_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SelectedPlanId { get; set; }

    [JsonPropertyName("plan_presets")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<DutyPlanPreset>? PlanPresets { get; set; }

    [JsonPropertyName("model_presets")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<DutyModelPreset>? ModelPresets { get; set; }

    [JsonPropertyName("mode_profiles")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<DutyModeProfile>? ModeProfiles { get; set; }

    [JsonPropertyName("per_day")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PerDay { get; set; }

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
