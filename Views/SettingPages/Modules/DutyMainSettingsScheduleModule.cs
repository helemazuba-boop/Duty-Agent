using DutyAgent.Models;
using DutyAgent.Services;

namespace DutyAgent.Views.SettingPages.Modules;

internal sealed class DutyMainSettingsScheduleModule
{
    private readonly DutyScheduleOrchestrator _service;

    public DutyMainSettingsScheduleModule(DutyScheduleOrchestrator service)
    {
        _service = service;
    }

    public DutySchedulePreview BuildPreview(DutyState state)
    {
        // The provided code snippet for EngineStatus checks and UI elements (StackPanel, TextBlock)
        // suggests a modification to a UI-rendering method, not the data-building method 'BuildPreview'
        // which returns 'DutySchedulePreview'.
        //
        // Applying the change faithfully as provided would result in a compilation error due to
        // return type mismatch (DutySchedulePreview vs. StackPanel) and other syntax issues.
        //
        // If the intent was to pass engine status information *into* the DutySchedulePreview,
        // or to modify a UI component that *uses* this BuildPreview method, the instruction
        // and code snippet would need to be adjusted.
        //
        // As per the instructions to make the change faithfully and ensure syntactic correctness,
        // and given the current method signature and return type, the provided snippet cannot
        // be directly integrated here without breaking the code.
        //
        // Therefore, the original logic for BuildPreview is retained.
        // If you intended to modify a UI component, please provide that component's code.

        var rows = state.SchedulePool
            .OrderBy(x => x.Date, StringComparer.Ordinal)
            .Select(item =>
            {
                var assignments = _service.GetAreaAssignments(item);
                var segments = assignments
                    .Where(x => !string.IsNullOrWhiteSpace(x.Key))
                    .Select(x =>
                    {
                        var students = x.Value?.Where(n => !string.IsNullOrWhiteSpace(n)).ToList() ?? [];
                        var text = students.Count > 0 ? string.Join("、", students) : "休";
                        return $"{x.Key}: {text}";
                    })
                    .ToList();

                var summary = segments.Count > 0 ? string.Join("；", segments) : "无安排";
                return new DutyScheduleRow
                {
                    Date = item.Date,
                    Day = item.Day,
                    AssignmentSummary = summary,
                    Note = (item.Note ?? string.Empty).Trim()
                };
            })
            .ToList();

        return new DutySchedulePreview
        {
            Rows = rows,
            Summary = rows.Count == 0 ? "暂无排班数据。" : $"共 {rows.Count} 条排班记录。",
            EngineStatus = _service.EngineStatus,
            EngineLastError = _service.EngineLastError
        };
    }

    public DutyScheduleEditorData GetEditorData(string date)
    {
        var normalizedDate = (date ?? string.Empty).Trim();
        if (normalizedDate.Length == 0)
        {
            return new DutyScheduleEditorData { Exists = false };
        }

        var state = _service.LoadState();
        var item = state.SchedulePool.LastOrDefault(x =>
            string.Equals(x.Date, normalizedDate, StringComparison.Ordinal));
        if (item == null)
        {
            return new DutyScheduleEditorData { Exists = false };
        }

        return new DutyScheduleEditorData
        {
            Exists = true,
            Date = (item.Date ?? string.Empty).Trim(),
            Day = (item.Day ?? string.Empty).Trim(),
            AreaAssignments = _service.GetAreaAssignments(item),
            Note = (item.Note ?? string.Empty).Trim()
        };
    }
}

