using System.Text.Json.Serialization;

namespace DutyAgentBridge.Models;

/// <summary>
/// 桥接插件设置模型（保存在 ClassIsland 配置目录）
/// </summary>
public sealed class BridgeSettings
{
    /// <summary>独立软件元数据文件路径（可自定义）</summary>
    [JsonPropertyName("meta_file_path")]
    public string MetaFilePath { get; set; } = "";

    /// <summary>自动连接（ClassIsland 启动时自动连接独立软件）</summary>
    [JsonPropertyName("auto_connect")]
    public bool AutoConnect { get; set; } = true;

    /// <summary>连接超时（秒）</summary>
    [JsonPropertyName("connect_timeout_seconds")]
    public int ConnectTimeoutSeconds { get; set; } = 15;

    /// <summary>健康检查轮询间隔（毫秒）</summary>
    [JsonPropertyName("health_check_interval_ms")]
    public int HealthCheckIntervalMs { get; set; } = 5000;
}

/// <summary>
/// 自动化动作设置
/// </summary>
public sealed class DutyRunScheduleActionSettings
{
    [JsonPropertyName("instruction")]
    public string Instruction { get; set; } = "请基于当前名册自动生成值日安排。";

    [JsonPropertyName("publish_completion_notification")]
    public bool PublishCompletionNotification { get; set; } = true;
}

/// <summary>
/// 自动化规则设置（今日值日匹配）
/// </summary>
public sealed class DutyAssignedStudentRuleSettings
{
    [JsonPropertyName("student_name")]
    public string StudentName { get; set; } = "";

    [JsonPropertyName("area_name")]
    public string AreaName { get; set; } = "";
}

/// <summary>
/// 桌面组件设置
/// </summary>
public sealed class DutyComponentSettings
{
    [JsonPropertyName("refresh_interval_seconds")]
    public int RefreshIntervalSeconds { get; set; } = 60;

    [JsonPropertyName("font_size")]
    public int FontSize { get; set; } = 14;

    [JsonPropertyName("font_color")]
    public string FontColor { get; set; } = "";

    [JsonPropertyName("use_dual_row_display")]
    public bool UseDualRowDisplay { get; set; } = false;

    [JsonPropertyName("use_per_area_multiline")]
    public bool UsePerAreaMultiLine { get; set; } = false;
}
