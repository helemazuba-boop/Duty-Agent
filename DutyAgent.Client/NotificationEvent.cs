using System.Text.Json.Serialization;

namespace DutyAgent.Client;

internal sealed class NotificationEvent
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "info";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "Duty-Agent";

    [JsonPropertyName("body")]
    public string Body { get; set; } = "";

    [JsonPropertyName("level")]
    public string Level { get; set; } = "info";

    [JsonPropertyName("route")]
    public string Route { get; set; } = "/dashboard";

    [JsonPropertyName("source")]
    public string Source { get; set; } = "backend";

    [JsonPropertyName("targets")]
    public List<string> Targets { get; set; } = [];

    [JsonPropertyName("created_at")]
    public double CreatedAt { get; set; }

    // C4 契约：通知 schema 的新增字段全部可空容错，老后端不携带时保持 null。
    [JsonPropertyName("created_at_iso")]
    public string? CreatedAtIso { get; set; }

    [JsonPropertyName("data")]
    public Dictionary<string, object?>? Data { get; set; }
}
