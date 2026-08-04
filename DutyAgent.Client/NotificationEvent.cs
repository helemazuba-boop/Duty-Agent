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
}
