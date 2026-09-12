using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace DutyAgentBridge.Models;

/// <summary>
/// 桥接插件设置模型（官方模式：存放在宿主分配的 PluginConfigFolder\Settings.json，
/// 经 ConfigureFileHelper 读写；INPC 变更即自动落盘）。
/// </summary>
public sealed class BridgeSettings : INotifyPropertyChanged
{
    private bool _autoConnect = true;
    private int _connectTimeoutSeconds = 15;
    private int _healthCheckIntervalMs = 5000;

    /// <summary>自动连接（ClassIsland 启动后自动连接独立软件）</summary>
    [JsonPropertyName("auto_connect")]
    public bool AutoConnect
    {
        get => _autoConnect;
        set => Set(ref _autoConnect, value);
    }

    /// <summary>连接超时（秒，3-120）</summary>
    [JsonPropertyName("connect_timeout_seconds")]
    public int ConnectTimeoutSeconds
    {
        get => _connectTimeoutSeconds;
        set => Set(ref _connectTimeoutSeconds, Math.Clamp(value, 3, 120));
    }

    /// <summary>健康检查轮询间隔（毫秒，1000-60000）</summary>
    [JsonPropertyName("health_check_interval_ms")]
    public int HealthCheckIntervalMs
    {
        get => _healthCheckIntervalMs;
        set => Set(ref _healthCheckIntervalMs, Math.Clamp(value, 1000, 60000));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
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
    [JsonPropertyName("font_size")]
    public int FontSize { get; set; } = 14;

    /// <summary>自定义文字颜色（留空跟随 ClassIsland 主题）</summary>
    [JsonPropertyName("font_color")]
    public string FontColor { get; set; } = "";

    [JsonPropertyName("use_dual_row_display")]
    public bool UseDualRowDisplay { get; set; } = false;

    [JsonPropertyName("use_per_area_multiline")]
    public bool UsePerAreaMultiLine { get; set; } = false;
}
