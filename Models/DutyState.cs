using System.Text.Json.Serialization;

namespace DutyAgent.Models;

public class DutyState
{
    [JsonPropertyName("seed_anchor")]
    public string SeedAnchor { get; set; } = string.Empty;

    [JsonPropertyName("next_run_note")]
    public string NextRunNote { get; set; } = string.Empty;

    [JsonPropertyName("schedule_pool")]
    public List<SchedulePoolItem> SchedulePool { get; set; } = [];

    [JsonPropertyName("debt_list")]
    public List<int> DebtList { get; set; } = [];

    [JsonPropertyName("credit_list")]
    public List<int> CreditList { get; set; } = [];

    [JsonPropertyName("last_pointer")]
    public int LastPointer { get; set; }
}

public class SchedulePoolItem
{
    [JsonPropertyName("date")]
    public string Date { get; set; } = string.Empty;

    [JsonPropertyName("day")]
    public string Day { get; set; } = string.Empty;

    [JsonPropertyName("area_assignments")]
    public Dictionary<string, List<string>> AreaAssignments { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("note")]
    public string Note { get; set; } = string.Empty;
}
