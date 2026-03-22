using System.Globalization;
using DutyAgent.Models;
using DutyAgent.Services;

namespace DutyAgent.Views.SettingPages.Modules;

internal sealed class DutyMainSettingsRosterModule
{
    private readonly DutyScheduleOrchestrator _service;

    public DutyMainSettingsRosterModule(DutyScheduleOrchestrator service)
    {
        _service = service;
    }

    public DutyRosterMutationResult AddStudent(string? rawName)
    {
        var name = (rawName ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            return new DutyRosterMutationResult(false, "请输入学生姓名。");
        }

        var roster = _service.LoadRosterEntries();
        var nextId = roster.Count == 0 ? 1 : roster.Max(x => x.Id) + 1;
        var isDuplicate = roster.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));

        roster.Add(new RosterEntry
        {
            Id = nextId,
            Name = name,
            Active = true
        });
        _service.SaveRosterEntries(roster);

        var message = isDuplicate
            ? $"已添加学生：{name} (库中存在同名，已分配新ID)。"
            : $"已添加学生：{name}";
        return new DutyRosterMutationResult(true, message, IsDuplicate: isDuplicate);
    }

    public DutyRosterMutationResult ToggleStudentActive(int id)
    {
        var roster = _service.LoadRosterEntries();
        var index = roster.FindIndex(x => x.Id == id);
        if (index < 0)
        {
            return new DutyRosterMutationResult(false, "未找到选中的学生记录。");
        }

        roster[index].Active = !roster[index].Active;
        _service.SaveRosterEntries(roster);
        var statusText = roster[index].Active ? "启用" : "停用";
        return new DutyRosterMutationResult(
            true,
            $"已{statusText}学生：{roster[index].Name}",
            ActiveState: roster[index].Active);
    }

    public DutyRosterMutationResult DeleteStudent(int id)
    {
        var roster = _service.LoadRosterEntries();
        var target = roster.FirstOrDefault(x => x.Id == id);
        if (target == null)
        {
            return new DutyRosterMutationResult(false, "未找到选中的学生记录。");
        }

        roster.RemoveAll(x => x.Id == id);
        _service.SaveRosterEntries(roster);
        return new DutyRosterMutationResult(true, $"已删除学生：{target.Name}");
    }

    public DutyRosterMutationResult ImportStudents(IEnumerable<string> names)
    {
        var normalizedNames = (names ?? [])
            .Select(x => (x ?? string.Empty).Trim())
            .Where(x => x.Length > 0)
            .ToList();
        if (normalizedNames.Count == 0)
        {
            return new DutyRosterMutationResult(false, "文件中未解析到有效姓名。");
        }

        var roster = _service.LoadRosterEntries();
        var nextId = roster.Count == 0 ? 1 : roster.Max(x => x.Id) + 1;
        foreach (var name in normalizedNames)
        {
            roster.Add(new RosterEntry
            {
                Id = nextId++,
                Name = name,
                Active = true
            });
        }

        _service.SaveRosterEntries(roster);
        return new DutyRosterMutationResult(
            true,
            $"导入完成：新增 {normalizedNames.Count} 名（同名将保留并分配不同 ID）。",
            ImportedCount: normalizedNames.Count);
    }

    public DutyRosterPreview BuildPreview(List<RosterEntry> roster, DutyState state, DateTime today)
    {
        var dutyCountByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var nextDutyByName = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in state.SchedulePool)
        {
            if (!TryParseScheduleDate(item.Date, out var date))
            {
                continue;
            }

            var namesInDay = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var assignments = _service.GetAreaAssignments(item);
            foreach (var name in assignments.Values.SelectMany(x => x))
            {
                var normalized = (name ?? string.Empty).Trim();
                if (normalized.Length == 0)
                {
                    continue;
                }

                namesInDay.Add(normalized);
            }

            foreach (var name in namesInDay)
            {
                if (date <= today)
                {
                    dutyCountByName[name] = dutyCountByName.TryGetValue(name, out var count) ? count + 1 : 1;
                }

                if (date < today)
                {
                    continue;
                }

                if (!nextDutyByName.TryGetValue(name, out var currentNext) || date < currentNext)
                {
                    nextDutyByName[name] = date;
                }
            }
        }

        var rows = roster
            .Select(r =>
            {
                var normalizedName = (r.Name ?? string.Empty).Trim();
                nextDutyByName.TryGetValue(normalizedName, out var nextDate);
                var hasNext = nextDate != default;
                var dutyCount = dutyCountByName.TryGetValue(normalizedName, out var count) ? count : 0;
                return new DutyRosterRow
                {
                    Id = r.Id,
                    Name = normalizedName,
                    NextDutyDate = hasNext ? nextDate : null,
                    NextDutyDisplay = hasNext ? FormatDutyDate(nextDate) : "未安排",
                    DutyCount = dutyCount,
                    Active = r.Active,
                    ActiveDisplay = r.Active ? "启用" : "停用"
                };
            })
            .OrderBy(x => x.NextDutyDate.HasValue ? 0 : 1)
            .ThenBy(x => x.NextDutyDate)
            .ThenBy(x => x.Id)
            .ToList();

        var scheduledCount = rows.Count(x => x.NextDutyDate.HasValue);
        var activeCount = rows.Count(x => x.Active);
        return new DutyRosterPreview
        {
            Rows = rows,
            Summary = $"共 {rows.Count} 名学生（启用 {activeCount} 名），已安排下一次值日 {scheduledCount} 名。"
        };
    }

    private static bool TryParseScheduleDate(string? rawDate, out DateTime date)
    {
        if (DateTime.TryParseExact(rawDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            return true;
        }

        return DateTime.TryParse(rawDate, out date);
    }

    private static string FormatDutyDate(DateTime date)
    {
        return $"{date:yyyy-MM-dd} ({ToChineseWeekday(date.DayOfWeek)})";
    }

    private static string ToChineseWeekday(DayOfWeek dayOfWeek)
    {
        return dayOfWeek switch
        {
            DayOfWeek.Monday => "周一",
            DayOfWeek.Tuesday => "周二",
            DayOfWeek.Wednesday => "周三",
            DayOfWeek.Thursday => "周四",
            DayOfWeek.Friday => "周五",
            DayOfWeek.Saturday => "周六",
            DayOfWeek.Sunday => "周日",
            _ => string.Empty
        };
    }
}

