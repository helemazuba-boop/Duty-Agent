using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DutyAgentBridge.Models;

/// <summary>组件文本换行模式。</summary>
public enum DutyWrapMode
{
    /// <summary>不换行：所有区域排在同一行。</summary>
    None = 0,

    /// <summary>按区域换行：每个区域独占一行。</summary>
    PerArea = 1,

    /// <summary>对半拆分：按人数对半拆成两行。</summary>
    Split = 2,
}

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
/// 桌面组件设置（INPC：组件订阅变更实时重渲染，对齐 DutyIsland 的组件规范）。
/// </summary>
public sealed class DutyComponentSettings : INotifyPropertyChanged, IJsonOnDeserialized
{
    private int _fontSize = 14;
    private string _fontColor = "";
    private DutyWrapMode _wrapMode = DutyWrapMode.None;

    [JsonPropertyName("font_size")]
    public int FontSize
    {
        get => _fontSize;
        set => Set(ref _fontSize, Math.Clamp(value, 8, 72));
    }

    /// <summary>自定义文字颜色（留空跟随 ClassIsland 主题）</summary>
    [JsonPropertyName("font_color")]
    public string FontColor
    {
        get => _fontColor;
        set => Set(ref _fontColor, value);
    }

    /// <summary>换行模式：不换行 / 按区域换行 / 按人数对半拆分两行。</summary>
    [JsonPropertyName("wrap_mode")]
    public DutyWrapMode WrapMode
    {
        get => _wrapMode;
        set => Set(ref _wrapMode, value);
    }

    /// <summary>换行模式（0=不换行 1=按区域 2=对半拆分），供 UI 数字型选择控件绑定。</summary>
    [JsonIgnore]
    public int WrapModeIndex
    {
        get => (int)WrapMode;
        set => WrapMode = (DutyWrapMode)Math.Clamp(value, 0, 2);
    }

    /// <summary>
    /// 反序列化时捕获的未知旧键：0.50.x 曾以 use_dual_row_display /
    /// use_per_area_multiline 两个布尔表达换行方式，此处一次性映射成
    /// wrap_mode 后丢弃，避免旧键在新模型上互相打架。
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? LegacyData { get; set; }

    public void OnDeserialized()
    {
        if (LegacyData == null || LegacyData.Count == 0)
        {
            return;
        }

        if (WrapMode == DutyWrapMode.None)
        {
            if (TryGetLegacyBool("use_dual_row_display"))
            {
                WrapMode = DutyWrapMode.Split;
            }
            else if (TryGetLegacyBool("use_per_area_multiline"))
            {
                WrapMode = DutyWrapMode.PerArea;
            }
        }

        LegacyData = null;
    }

    private bool TryGetLegacyBool(string key)
    {
        return LegacyData != null &&
               LegacyData.TryGetValue(key, out var value) &&
               value.ValueKind == JsonValueKind.True;
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
