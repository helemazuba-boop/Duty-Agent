using DutyAgent.Models;
using DutyAgent.Services;

namespace DutyAgent.Views.SettingPages.Modules;

internal enum DutyBackendConfigLoadState
{
    NotLoaded,
    Loaded,
    LoadFailed
}

internal sealed class DutyHostSettingsValues
{
    public string AutoRunMode { get; init; } = "Off";
    public string AutoRunParameter { get; init; } = "Monday";
    public string AutoRunTime { get; init; } = "08:00";
    public bool AutoRunTriggerNotificationEnabled { get; init; }
    public bool DutyReminderEnabled { get; init; }
    public string DutyReminderTime { get; init; } = "07:40";
    public string ServerPortMode { get; init; } = DutyServerPortModes.Random;
    public string FixedServerPortText { get; init; } = string.Empty;
    public bool EnableMcp { get; init; }
    public bool EnableWebViewDebugLayer { get; init; }
    public string ComponentRefreshTime { get; init; } = "08:00";
    public int NotificationDurationSeconds { get; init; } = 8;
}

internal sealed class DutyAccessSecurityValues
{
    public string AccessTokenMode { get; init; } = DutyAccessTokenModes.Dynamic;
    public bool StaticAccessTokenConfigured { get; init; }
}

internal sealed class DutyBackendSettingsValues
{
    public string SelectedPlanId { get; init; } = DutyBackendModeIds.Standard;
    public List<DutyPlanPreset> PlanPresets { get; init; } = [];
    public string? DutyRule { get; init; }
}

internal sealed class DutySettingsPageValues
{
    public DutyHostSettingsValues Host { get; init; } = new();
    public DutyBackendSettingsValues Backend { get; init; } = new();
}

internal sealed class DutySettingsSaveContext
{
    public DutySettingsPageValues Current { get; init; } = new();
    public DutyHostSettingsValues LastAppliedHost { get; init; } = new();
    public DutyBackendConfig? LastAppliedBackend { get; init; }
    public DutySettingsDocument? LastLoadedDocument { get; init; }
    public DutyBackendConfigLoadState BackendLoadState { get; init; } = DutyBackendConfigLoadState.NotLoaded;
    public string BackendErrorMessage { get; init; } = string.Empty;
}

internal enum DutySettingsSaveMessageLevel
{
    Info,
    Warning,
    Error
}

internal readonly record struct DutySettingsSaveOutcome(
    bool Success,
    bool NoChanges,
    bool RestartRequired,
    bool HostChanged,
    bool HostSaved,
    bool BackendChanged,
    bool BackendSaved,
    string Message,
    DutySettingsSaveMessageLevel MessageLevel,
    DutySettingsDocument? AppliedDocument = null,
    DutyHostSettingsValues? AppliedHost = null,
    DutyBackendConfig? AppliedBackend = null);

internal readonly record struct DutyHostSettingsSaveResult(
    bool RestartRequired,
    DutyHostSettingsValues AppliedValues);

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

