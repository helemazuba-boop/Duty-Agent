using System.Text.Json.Serialization;

namespace DutyAgentBridge.Models;

/// <summary>
/// 独立软件启动元数据（从 .duty-agent-meta.json 读取）
/// </summary>
public sealed class StandaloneMeta
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "0.50.0";

    [JsonPropertyName("pid")]
    public int ProcessId { get; set; }

    [JsonPropertyName("port")]
    public int Port { get; set; }

    [JsonPropertyName("token_mode")]
    public string TokenMode { get; set; } = "dynamic";

    [JsonPropertyName("token")]
    public string Token { get; set; } = "";

    [JsonPropertyName("started_at")]
    public string StartedAt { get; set; } = "";

    [JsonPropertyName("data_dir")]
    public string DataDir { get; set; } = "";

    [JsonPropertyName("webapp_url")]
    public string WebappUrl { get; set; } = "";
}

/// <summary>
/// 连接状态
/// </summary>
public enum IpcBridgeState
{
    /// <summary>初始状态</summary>
    Initial,

    /// <summary>正在检查独立软件</summary>
    Checking,

    /// <summary>正在连接</summary>
    Connecting,

    /// <summary>已连接</summary>
    Connected,

    /// <summary>已断开</summary>
    Disconnected,

    /// <summary>未检测到独立软件</summary>
    NotInstalled,

    /// <summary>连接错误</summary>
    Error
}

/// <summary>
/// 桥接诊断日志条目
/// </summary>
public sealed class BridgeLogEntry
{
    public DateTimeOffset Timestamp { get; init; }
    public string Level { get; init; } = "INFO";
    public string Scope { get; init; } = "";
    public string Message { get; init; } = "";
    public object? Data { get; init; }
}

/// <summary>
/// API 响应模型（兼容 FastAPI）
/// </summary>
public sealed class CoreRunResult
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("ai_response")]
    public string? AiResponse { get; set; }

    public static CoreRunResult Ok(string message = "Success", string? aiResponse = null)
    {
        return new CoreRunResult
        {
            Success = true,
            Message = message,
            AiResponse = aiResponse
        };
    }

    public static CoreRunResult Fail(string message, string? code = null)
    {
        return new CoreRunResult
        {
            Success = false,
            Message = message,
            Code = code
        };
    }
}

/// <summary>
/// 排班执行进度
/// </summary>
public sealed class CoreRunProgress
{
    public CoreRunProgress(string phase, string message, string? chunk)
    {
        Phase = phase;
        Message = message;
        Chunk = chunk;
    }

    public string Phase { get; }
    public string Message { get; }
    public string? Chunk { get; }
}

/// <summary>
/// 后端配置（来自 GET /api/v1/config）
/// </summary>
public sealed class DutyBackendConfig
{
    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("api_key")]
    public string ApiKey { get; set; } = "";

    [JsonPropertyName("base_url")]
    public string BaseUrl { get; set; } = "";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("model_profile")]
    public string ModelProfile { get; set; } = "auto";

    [JsonPropertyName("orchestration_mode")]
    public string OrchestrationMode { get; set; } = "auto";

    [JsonPropertyName("multi_agent_execution_mode")]
    public string MultiAgentExecutionMode { get; set; } = "auto";

    [JsonPropertyName("single_pass_strategy")]
    public string SinglePassStrategy { get; set; } = "auto";

    [JsonPropertyName("provider_hint")]
    public string ProviderHint { get; set; } = "";

    [JsonPropertyName("selected_plan_id")]
    public string SelectedPlanId { get; set; } = "standard";

    [JsonPropertyName("plan_presets")]
    public List<DutyPlanPreset> PlanPresets { get; set; } = [];

    [JsonPropertyName("duty_rule")]
    public string DutyRule { get; set; } = "";
}

/// <summary>
/// 排班方案预设
/// </summary>
public sealed class DutyPlanPreset
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "standard";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "标准";

    [JsonPropertyName("mode_id")]
    public string ModeId { get; set; } = "standard";

    [JsonPropertyName("api_key")]
    public string ApiKey { get; set; } = "";

    [JsonPropertyName("base_url")]
    public string BaseUrl { get; set; } = "";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("model_profile")]
    public string ModelProfile { get; set; } = "auto";

    [JsonPropertyName("provider_hint")]
    public string ProviderHint { get; set; } = "";

    [JsonPropertyName("multi_agent_execution_mode")]
    public string MultiAgentExecutionMode { get; set; } = "auto";
}

/// <summary>
/// 配置更新请求
/// </summary>
public sealed class DutyBackendConfigPatch
{
    [JsonPropertyName("expected_version")]
    public int? ExpectedVersion { get; set; }

    [JsonPropertyName("selected_plan_id")]
    public string? SelectedPlanId { get; set; }

    [JsonPropertyName("plan_presets")]
    public List<DutyPlanPreset>? PlanPresets { get; set; }

    [JsonPropertyName("duty_rule")]
    public string? DutyRule { get; set; }
}

/// <summary>
/// 名册条目
/// </summary>
public sealed class RosterEntry
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("active")]
    public bool Active { get; set; } = true;
}

/// <summary>
/// 排班池条目
/// </summary>
public sealed class SchedulePoolItem
{
    [JsonPropertyName("date")]
    public string Date { get; set; } = "";

    [JsonPropertyName("day")]
    public string Day { get; set; } = "";

    [JsonPropertyName("area_assignments")]
    public Dictionary<string, List<string>> AreaAssignments { get; set; } = new();

    [JsonPropertyName("note")]
    public string Note { get; set; } = "";
}

/// <summary>
/// 完整状态快照
/// </summary>
public sealed class DutyState
{
    [JsonPropertyName("schedule_pool")]
    public List<SchedulePoolItem> SchedulePool { get; set; } = [];

    [JsonPropertyName("next_run_note")]
    public string NextRunNote { get; set; } = "";

    [JsonPropertyName("debt_counts")]
    public Dictionary<string, int> DebtCounts { get; set; } = new();

    [JsonPropertyName("credit_counts")]
    public Dictionary<string, int> CreditCounts { get; set; } = new();

    [JsonPropertyName("last_pointer")]
    public int LastPointer { get; set; }
}

/// <summary>
/// 完整快照（config + roster + state）
/// </summary>
public sealed class DutyBackendSnapshot
{
    [JsonPropertyName("config")]
    public DutyBackendConfig Config { get; set; } = new();

    [JsonPropertyName("roster")]
    public List<RosterEntry> Roster { get; set; } = [];

    [JsonPropertyName("state")]
    public DutyState State { get; set; } = new();
}

/// <summary>
/// 排班条目保存请求
/// </summary>
public sealed class DutyScheduleEntrySaveRequest
{
    [JsonPropertyName("source_date")]
    public string? SourceDate { get; set; }

    [JsonPropertyName("target_date")]
    public string? TargetDate { get; set; }

    [JsonPropertyName("day")]
    public string? Day { get; set; }

    [JsonPropertyName("area_assignments")]
    public Dictionary<string, List<string>>? AreaAssignments { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }

    [JsonPropertyName("confirm_overwrite")]
    public bool ConfirmOverwrite { get; set; }

    [JsonPropertyName("ledger_mode")]
    public string LedgerMode { get; set; } = "record";
}

/// <summary>
/// 排班条目保存响应
/// </summary>
public sealed class DutyScheduleEntrySaveResponse
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("ledger_mode")]
    public string LedgerMode { get; set; } = "record";

    [JsonPropertyName("ledger_applied")]
    public bool LedgerApplied { get; set; }

    [JsonPropertyName("snapshot")]
    public DutyBackendSnapshot? Snapshot { get; set; }
}
