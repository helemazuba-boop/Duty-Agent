using System.Text.Json.Serialization;

namespace DutyAgent.Models;

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

    [JsonPropertyName("provider_hint")]
    public string ProviderHint { get; set; } = string.Empty;

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

    [JsonPropertyName("provider_hint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProviderHint { get; set; }

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
