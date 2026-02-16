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
                DutyText.Text = "\u6682\u65E0\u6392\u73ED\u6570\u636E";
                DutyText.Foreground = Brushes.Red;
                return;
            }

            var today = DateTime.Now.ToString("yyyy-MM-dd");
            var item = state.SchedulePool.LastOrDefault(x => x.Date == today);
            if (item == null)
            {
                DutyText.Text = "\u4ECA\u65E5\u6682\u65E0\u503C\u65E5\u5B89\u6392";
                DutyText.Foreground = Brushes.Red;
                return;
            }

            var areaOrder = _service.GetAreaNames();
            var assignments = _service.GetAreaAssignments(item);
            foreach (var area in assignments.Keys)
            {
                if (!areaOrder.Contains(area, StringComparer.Ordinal))
                {
                    areaOrder.Add(area);
                }
            }

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

            DutyText.Text = string.Join(separator, segments);
            DutyText.ClearValue(TextBlock.ForegroundProperty);
        }
        catch
        {
            DutyText.Text = "\u52A0\u8F7D\u503C\u65E5\u6570\u636E\u5931\u8D25";
            DutyText.Foreground = Brushes.Red;
        }
    }
}
