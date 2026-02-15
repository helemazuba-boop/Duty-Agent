using System.Text.Json.Serialization;

namespace DutyAgent.Models;

public class DutyState
{
    [JsonPropertyName("seed_anchor")]
    public string SeedAnchor { get; set; } = string.Empty;

    [JsonPropertyName("schedule_pool")]
    public List<SchedulePoolItem> SchedulePool { get; set; } = [];
}

public class SchedulePoolItem
{
    [JsonPropertyName("date")]
    public string Date { get; set; } = string.Empty;

    [JsonPropertyName("day")]
    public string Day { get; set; } = string.Empty;

    [JsonPropertyName("area_assignments")]
    public Dictionary<string, List<string>> AreaAssignments { get; set; } = new(StringComparer.Ordinal);
}
