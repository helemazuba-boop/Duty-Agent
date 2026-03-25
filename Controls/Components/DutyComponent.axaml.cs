using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Shared;
using DutyAgent.Models;
using DutyAgent.Services;

namespace DutyAgent.Controls.Components;

[ComponentInfo("F364E328-6702-4C20-8A94-BA04FC0F2815", "\u503C\u65E5\u4EBA\u5458", "\uE31E", "\u663E\u793A\u4ECA\u65E5\u503C\u65E5\u5B89\u6392\u3002")]
public partial class DutyComponent : ComponentBase<DutyComponentSettings>
{
    private readonly DispatcherTimer _timer;
    private readonly DutyScheduleOrchestrator _service = IAppHost.GetService<DutyScheduleOrchestrator>();

    public DutyComponent()
    {
        InitializeComponent();

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(60)
        };
        _timer.Tick += (_, _) => UpdateState();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        _service.ScheduleUpdated += OnScheduleUpdated;
        _timer.Start();
        UpdateState();
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        _timer.Stop();
        _service.ScheduleUpdated -= OnScheduleUpdated;
        base.OnUnloaded(e);
    }

    private void OnScheduleUpdated(object? sender, EventArgs e)
    {
        UpdateState();
    }

    private void UpdateState()
    {
        try
        {
            var state = _service.LoadState();
            if (state.SchedulePool.Count == 0)
            {
                ShowSingleRow("\u6682\u65E0\u6392\u73ED\u6570\u636E", isError: true);
                return;
            }

            var item = _service.GetCurrentScheduleItem();
            if (item == null)
            {
                ShowSingleRow("\u8BE5\u65E5\u6682\u65E0\u503C\u65E5\u5B89\u6392", isError: true);
                return;
            }

            var assignments = _service.GetAreaAssignments(item);
            var areaOrder = assignments.Keys.ToList();

            if (areaOrder.Count == 0)
            {
                ShowSingleRow("\u8BE5\u65E5\u6682\u65E0\u503C\u65E5\u5B89\u6392");
                return;
            }

            if (Settings?.UseDualRowDisplay == true)
            {
                RenderDualRow(areaOrder, assignments);
            }
            else
            {
                RenderSingleRow(areaOrder, assignments);
            }
        }
        catch
        {
            ShowSingleRow("\u52A0\u8F7D\u503C\u65E5\u6570\u636E\u5931\u8D25", isError: true);
        }
    }

    private void RenderSingleRow(List<string> areaOrder, Dictionary<string, List<string>> assignments)
    {
        var segments = areaOrder
            .Select(area =>
            {
                var students = assignments.TryGetValue(area, out var names) ? names : [];
                var text = students.Count > 0 ? string.Join("\u3001", students) : "\u65E0";
                return $"{area}\uFF1A{text}";
            })
            .ToList();

        var separator = Settings?.UsePerAreaMultiLine == true
            ? Environment.NewLine
            : "\uFF1B";

        DutyTextRow1.Text = string.Join(separator, segments);
        DutyTextRow1.ClearValue(TextBlock.ForegroundProperty);
        DutyTextRow2.IsVisible = false;
    }

    private void RenderDualRow(List<string> areaOrder, Dictionary<string, List<string>> assignments)
    {
        // Flatten all area-student entries into a single ordered list
        var allEntries = new List<(string Area, string Student)>();
        foreach (var area in areaOrder)
        {
            var students = assignments.TryGetValue(area, out var names) ? names : [];
            if (students.Count == 0)
            {
                allEntries.Add((area, "\u65E0"));
            }
            else
            {
                foreach (var student in students)
                {
                    allEntries.Add((area, student));
                }
            }
        }

        if (allEntries.Count <= 1)
        {
            RenderSingleRow(areaOrder, assignments);
            return;
        }

        var mid = (allEntries.Count + 1) / 2;
        var row1Entries = allEntries.Take(mid).ToList();
        var row2Entries = allEntries.Skip(mid).ToList();

        DutyTextRow1.Text = FormatRowEntries(row1Entries);
        DutyTextRow1.ClearValue(TextBlock.ForegroundProperty);

        DutyTextRow2.Text = FormatRowEntries(row2Entries);
        DutyTextRow2.ClearValue(TextBlock.ForegroundProperty);
        DutyTextRow2.IsVisible = true;
    }

    private static string FormatRowEntries(List<(string Area, string Student)> entries)
    {
        // Group consecutive entries by area: "区域：张三、李四；区域2：王五"
        var segments = new List<string>();
        string? currentArea = null;
        var currentStudents = new List<string>();

        foreach (var (area, student) in entries)
        {
            if (area != currentArea)
            {
                if (currentArea != null && currentStudents.Count > 0)
                {
                    segments.Add($"{currentArea}\uFF1A{string.Join("\u3001", currentStudents)}");
                }
                currentArea = area;
                currentStudents = [student];
            }
            else
            {
                currentStudents.Add(student);
            }
        }

        if (currentArea != null && currentStudents.Count > 0)
        {
            segments.Add($"{currentArea}\uFF1A{string.Join("\u3001", currentStudents)}");
        }

        return string.Join("\uFF1B", segments);
    }

    private void ShowSingleRow(string text, bool isError = false)
    {
        DutyTextRow1.Text = text;
        if (isError)
        {
            DutyTextRow1.Foreground = Brushes.Red;
        }
        else
        {
            DutyTextRow1.ClearValue(TextBlock.ForegroundProperty);
        }
        DutyTextRow2.IsVisible = false;
    }
}
