using System.Text.Json.Serialization;

namespace DutyIsland.Models;

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

    [JsonPropertyName("students")]
    public List<string> Students { get; set; } = [];

    [JsonPropertyName("classroom_students")]
    public List<string> ClassroomStudents { get; set; } = [];

    [JsonPropertyName("cleaning_area_students")]
    public List<string> CleaningAreaStudents { get; set; } = [];

    [JsonPropertyName("area_assignments")]
    public Dictionary<string, List<string>> AreaAssignments { get; set; } = new(StringComparer.Ordinal);
}
