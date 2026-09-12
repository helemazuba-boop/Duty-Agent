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

    [JsonPropertyName("debt_counts")]
    public Dictionary<int, int> DebtCounts { get; set; } = new();

    [JsonPropertyName("credit_counts")]
    public Dictionary<int, int> CreditCounts { get; set; } = new();

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

/// <summary>
/// GET /api/v1/state 的响应封装（契约 C1，Python 后端提供）：
/// {"state": load_state() 原样 dict, "mtime_ns": int|null}。
/// mtime_ns 为 null 时（旧后端/无文件），消费方退化为按内容比较感知变更。
/// </summary>
public sealed class DutyBackendStateEnvelope
{
    [JsonPropertyName("state")]
    public DutyState? State { get; set; }

    [JsonPropertyName("mtime_ns")]
    public long? MtimeNs { get; set; }
}
