using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Shared;
using DutyAgentBridge.Models;
using DutyAgentBridge.Services;

namespace DutyAgentBridge.Controls;

/// <summary>
/// 桌面组件（继承 ClassIsland 组件系统）
/// 显示今日值日安排
/// </summary>
[ComponentInfo(
    "DUTY-BRIDGE-COMP-001",
    "\u503C\u65E5\u4EBA\u5458",
    "\uE31E",
    "\u663E\u793A\u4ECA\u65E5\u503C\u65E5\u5B89\u6392\u3002")]
public partial class DutyComponent : ComponentBase<DutyComponentSettings>
{
    private readonly DispatcherTimer _timer;
    private readonly IIpcBridgeService _bridge = IAppHost.GetService<IIpcBridgeService>();

    public DutyComponent()
    {
        InitializeComponent();

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(Settings?.RefreshIntervalSeconds ?? 60)
        };
        _timer.Tick += (_, _) => _ = UpdateStateAsync();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        _bridge.StateChanged += OnBridgeStateChanged;
        _timer.Start();
        _ = UpdateStateAsync();
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        _timer.Stop();
        _bridge.StateChanged -= OnBridgeStateChanged;
        base.OnUnloaded(e);
    }

    private void OnBridgeStateChanged(object? sender, IpcBridgeState state)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (state == IpcBridgeState.Connected)
            {
                _ = UpdateStateAsync();
            }
            else if (state == IpcBridgeState.Disconnected || state == IpcBridgeState.NotInstalled)
            {
                ShowSingleRow("\u672A\u8FDE\u63A5\u72B6\u6001", isError: true);
            }
        });
    }

    private async Task UpdateStateAsync()
    {
        try
        {
            if (_bridge.State != IpcBridgeState.Connected)
            {
                ShowSingleRow("\u672A\u8FDE\u63A5\u72B6\u6001", isError: true);
                return;
            }

            var data = await Task.Run(() =>
            {
                try
                {
                    var snapshot = _bridge.GetSnapshotAsync().GetAwaiter().GetResult();
                    if (snapshot.State.SchedulePool.Count == 0)
                    {
                        return (HasData: false, Item: (SchedulePoolItem?)null, Assignments: (Dictionary<string, List<string>>?)null, Error: "\u6682\u65E0\u6392\u73ED\u6570\u636E");
                    }

                    var today = DateTime.Now.ToString("yyyy-MM-dd");
                    var item = snapshot.State.SchedulePool
                        .FirstOrDefault(x => string.Equals(x.Date, today, StringComparison.Ordinal));

                    if (item == null)
                    {
                        return (HasData: false, Item: (SchedulePoolItem?)null, Assignments: (Dictionary<string, List<string>>?)null, Error: "\u8BE5\u65E5\u6682\u65E0\u503C\u65E5\u5B89\u6392");
                    }

                    return (HasData: true, Item: item, Assignments: item.AreaAssignments, Error: (string?)null);
                }
                catch
                {
                    return (HasData: false, Item: (SchedulePoolItem?)null, Assignments: (Dictionary<string, List<string>>?)null, Error: "\u52A0\u8F7D\u6570\u636E\u5931\u8D25");
                }
            });

            if (!data.HasData)
            {
                ShowSingleRow(data.Error!, isError: true);
                return;
            }

            var areaOrder = data.Assignments!.Keys.ToList();
            if (areaOrder.Count == 0)
            {
                ShowSingleRow("\u8BE5\u65E5\u6682\u65E0\u503C\u65E5\u5B89\u6392");
                return;
            }

            if (Settings?.UseDualRowDisplay == true)
            {
                RenderDualRow(areaOrder, data.Assignments!);
            }
            else
            {
                RenderSingleRow(areaOrder, data.Assignments!);
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
        ApplyTextStyle(DutyTextRow1);
        DutyTextRow2.IsVisible = false;
    }

    private void RenderDualRow(List<string> areaOrder, Dictionary<string, List<string>> assignments)
    {
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
        ApplyTextStyle(DutyTextRow1);

        DutyTextRow2.Text = FormatRowEntries(row2Entries);
        ApplyTextStyle(DutyTextRow2);
        DutyTextRow2.IsVisible = true;
    }

    private static string FormatRowEntries(List<(string Area, string Student)> entries)
    {
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
            ApplyTextStyle(DutyTextRow1);
        }
        DutyTextRow2.IsVisible = false;
    }

    private void ApplyTextStyle(TextBlock textBlock)
    {
        if (Settings == null) return;
        textBlock.FontSize = Settings.FontSize;
        if (!string.IsNullOrWhiteSpace(Settings.FontColor))
        {
            try
            {
                textBlock.Foreground = new SolidColorBrush(
                    Avalonia.Media.Color.Parse(Settings.FontColor));
            }
            catch
            {
                textBlock.Foreground = Brushes.Black;
            }
        }
    }
}
