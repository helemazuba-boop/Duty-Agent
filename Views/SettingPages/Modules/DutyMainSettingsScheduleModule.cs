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

