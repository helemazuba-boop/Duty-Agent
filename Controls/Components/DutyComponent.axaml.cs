using System.IO;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using DutyIsland.Models;

namespace DutyIsland.Controls.Components;

[ComponentInfo("00318064-DACC-419F-8228-79F3413CAB54", "Duty Staff", "\uE31E", "Displays today's duty staff.")]
public partial class DutyComponent : ComponentBase
{
    private readonly DispatcherTimer _timer;

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
        _timer.Start();
        UpdateState();
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        _timer.Stop();
        base.OnUnloaded(e);
    }

    private void UpdateState()
    {
        try
        {
            var pluginBaseDir = Path.GetDirectoryName(typeof(DutyComponent).Assembly.Location) ?? AppContext.BaseDirectory;
            var path = Path.Combine(pluginBaseDir, "Assets_Duty", "data", "state.json");
            if (!File.Exists(path))
            {
                DutyText.Text = "state.json not found";
                DutyText.Foreground = Brushes.Red;
                return;
            }

            var json = File.ReadAllText(path);
            var state = JsonSerializer.Deserialize<DutyState>(json);
            if (state?.SchedulePool == null)
            {
                DutyText.Text = "Invalid schedule data";
                DutyText.Foreground = Brushes.Red;
                return;
            }

            var today = DateTime.Now.ToString("yyyy-MM-dd");
            var item = state.SchedulePool.Find(x => x.Date == today);
            if (item != null)
            {
                var classroom = string.Join(", ", item.ClassroomStudents);
                var cleaning = string.Join(", ", item.CleaningAreaStudents);
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
}
