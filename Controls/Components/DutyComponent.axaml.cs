using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Shared;
using DutyIsland.Models;
using DutyIsland.Services;

namespace DutyIsland.Controls.Components;

[ComponentInfo("00318064-DACC-419F-8228-79F3413CAB54", "Duty Staff", "\uE31E", "Displays today's duty staff.")]
public partial class DutyComponent : ComponentBase
{
    private readonly DispatcherTimer _timer;
    private readonly DutyBackendService _service = IAppHost.GetService<DutyBackendService>();

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
                DutyText.Text = "No schedule data";
                DutyText.Foreground = Brushes.Red;
                return;
            }

            var today = DateTime.Now.ToString("yyyy-MM-dd");
            var item = state.SchedulePool.Find(x => x.Date == today);
            if (item != null)
            {
                var classroomStudents = GetClassroomStudents(item);
                var cleaningStudents = GetCleaningStudents(item);
                var classroom = classroomStudents.Count > 0 ? string.Join(", ", classroomStudents) : "-";
                var cleaning = cleaningStudents.Count > 0 ? string.Join(", ", cleaningStudents) : "-";
                DutyText.Text = $"Classroom: {classroom} | Cleaning: {cleaning}";
                DutyText.ClearValue(TextBlock.ForegroundProperty);
            }
            else
            {
                DutyText.Text = "No duty assigned today";
                DutyText.Foreground = Brushes.Red;
            }
        }
        catch
        {
            DutyText.Text = "Failed to load duty data";
            DutyText.Foreground = Brushes.Red;
        }
    }

    private static IReadOnlyList<string> GetClassroomStudents(SchedulePoolItem item)
    {
        return item.ClassroomStudents.Count > 0 ? item.ClassroomStudents : item.Students;
    }

    private static IReadOnlyList<string> GetCleaningStudents(SchedulePoolItem item)
    {
        return item.CleaningAreaStudents.Count > 0 ? item.CleaningAreaStudents : item.Students;
    }
}
