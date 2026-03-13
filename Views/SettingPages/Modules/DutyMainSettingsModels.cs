using DutyAgent.Models;
using DutyAgent.Services;

namespace DutyAgent.Views.SettingPages.Modules;

internal sealed class DutySettingsFormModel
{
    public bool BackendConfigAvailable { get; init; }
    public string BackendConfigError { get; init; } = string.Empty;
    public string ApiKeyMask { get; init; } = string.Empty;
    public string BaseUrl { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string ModelProfile { get; init; } = "auto";
    public string OrchestrationMode { get; init; } = "auto";
    public string MultiAgentExecutionMode { get; init; } = "auto";
    public string ProviderHint { get; init; } = string.Empty;
    public string AutoRunMode { get; init; } = "Off";
    public string AutoRunParameter { get; init; } = "Monday";
    public string AutoRunTime { get; init; } = "08:00";
    public bool AutoRunTriggerNotificationEnabled { get; init; }
    public bool DutyReminderEnabled { get; init; }
    public string DutyReminderTime { get; init; } = "07:40";
    public bool EnableMcp { get; init; }
    public bool EnableWebViewDebugLayer { get; init; }
    public string ComponentRefreshTime { get; init; } = "08:00";
    public string DutyRule { get; init; } = string.Empty;
    public int NotificationDurationSeconds { get; init; } = 8;
}

internal sealed class DutySettingsApplyRequest
{
    public string? ApiKeyInput { get; init; }
    public string? BaseUrl { get; init; }
    public string? Model { get; init; }
    public string ModelProfile { get; init; } = "auto";
    public string OrchestrationMode { get; init; } = "auto";
    public string MultiAgentExecutionMode { get; init; } = "auto";
    public string? ProviderHint { get; init; }
    public string AutoRunMode { get; init; } = "Off";
    public string AutoRunParameter { get; init; } = "Monday";
    public string AutoRunTime { get; init; } = "08:00";
    public string ComponentRefreshTime { get; init; } = "08:00";
    public bool AutoRunTriggerNotificationEnabled { get; init; }
    public bool DutyReminderEnabled { get; init; }
    public string DutyReminderTime { get; init; } = "07:40";
    public bool EnableMcp { get; init; }
    public bool EnableWebViewDebugLayer { get; init; }
    public string? DutyRule { get; init; }
    public int NotificationDurationSeconds { get; init; } = 8;
}

internal readonly record struct DutySettingsApplyResult(bool Success, bool RestartRequired, string Message, bool BackendConfigAvailable = true);

public sealed class DutyRosterRow
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public DateTime? NextDutyDate { get; init; }
    public string NextDutyDisplay { get; init; } = "未安排";
    public int DutyCount { get; init; }
    public bool Active { get; init; }
    public string ActiveDisplay { get; init; } = "启用";
}

internal sealed class DutyRosterPreview
{
    public List<DutyRosterRow> Rows { get; init; } = [];
    public string Summary { get; init; } = "名单未加载。";
}

internal readonly record struct DutyRosterMutationResult(
    bool Success,
    string Message,
    bool IsDuplicate = false,
    bool? ActiveState = null,
    int ImportedCount = 0);

public sealed class DutyScheduleRow
{
    public string Date { get; init; } = string.Empty;
    public string Day { get; init; } = string.Empty;
    public string AssignmentSummary { get; init; } = "无安排";
    public string Note { get; init; } = string.Empty;
}

internal sealed class DutySchedulePreview
{
    public List<DutyScheduleRow> Rows { get; init; } = [];
    public string Summary { get; init; } = "暂无排班数据。";
    public EngineState EngineStatus { get; init; }
    public string? EngineLastError { get; init; }
}

internal sealed class DutyScheduleEditorData
{
    public bool Exists { get; init; }
    public string Date { get; init; } = string.Empty;
    public string Day { get; init; } = string.Empty;
    public Dictionary<string, List<string>> AreaAssignments { get; init; } = new(StringComparer.Ordinal);
    public string Note { get; init; } = string.Empty;
}

