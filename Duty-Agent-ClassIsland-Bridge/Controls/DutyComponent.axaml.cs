using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Attributes;
using ClassIsland.Shared;
using DutyAgentBridge.Models;
using DutyAgentBridge.Services;

namespace DutyAgentBridge.Controls;

/// <summary>
/// 桌面组件（继承 ClassIsland 组件系统）——显示今日值日安排。
///
/// 对齐 DutyIsland 的组件规范：XAML 根为 ci:ComponentBase（x:TypeArguments），
/// Loaded/Unloaded 由 XAML 事件接线，订阅服务事件与 Settings.PropertyChanged
/// 实时刷新。数据源为 DutyStateCache（后端推送 snapshot_changed / schedule_*，
/// 60s 安全轮询兜底）；跨天/分钟级兜底挂 ILessonsService.PostMainTimerTicked。
/// 组件自身不持有定时器，也不直接发起网络请求。
/// </summary>
[ComponentInfo(
    "bc83d764-4c0d-4a36-a6da-27c96d2c339b",
    "\u503C\u65E5\u4EBA\u5458",
    "\uE31E",
    "\u663E\u793A\u4ECA\u65E5\u503C\u65E5\u5B89\u6392\u3002")]
public partial class DutyComponent : ComponentBase<DutyComponentSettings>
{
    private readonly DutyStateCache _cache = IAppHost.GetService<DutyStateCache>();
    private readonly IIpcBridgeService _bridge = IAppHost.GetService<IIpcBridgeService>();
    private readonly ILessonsService? _lessonsService = IAppHost.GetService<ILessonsService>();

    public DutyComponent()
    {
        InitializeComponent();
    }

    private void Component_OnLoaded(object? sender, RoutedEventArgs e)
    {
        _cache.SnapshotUpdated += OnSnapshotUpdated;
        if (_lessonsService != null)
        {
            _lessonsService.PostMainTimerTicked += OnMainTimerTicked;
        }
        _bridge.StateChanged += OnBridgeStateChanged;
        if (Settings != null)
        {
            Settings.PropertyChanged += OnSettingsChanged;
        }

        if (_cache.State == null && _bridge.State == IpcBridgeState.Connected)
        {
            // 冷启动：缓存还没有数据，触发一次拉取。
            _ = _cache.RefreshAsync("component-initial");
        }
        RefreshContent();
    }

    private void Component_OnUnloaded(object? sender, RoutedEventArgs e)
    {
        _cache.SnapshotUpdated -= OnSnapshotUpdated;
        if (_lessonsService != null)
        {
            _lessonsService.PostMainTimerTicked -= OnMainTimerTicked;
        }
        _bridge.StateChanged -= OnBridgeStateChanged;
        if (Settings != null)
        {
            Settings.PropertyChanged -= OnSettingsChanged;
        }
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(RefreshContent);
    }

    private void OnMainTimerTicked(object? sender, EventArgs e)
    {
        // 每分钟兜底：处理跨天（日期翻转后"今天"变化）等缓存事件覆盖不到的场景。
        Dispatcher.UIThread.Post(RefreshContent);
    }

    private void OnSnapshotUpdated(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(RefreshContent);
    }

    private void OnBridgeStateChanged(object? sender, IpcBridgeState state)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (state is IpcBridgeState.Disconnected or IpcBridgeState.NotInstalled)
            {
                ShowSingleRow("\u672A\u8FDE\u63A5\u72B6\u6001", isError: true);
            }
            else
            {
                RefreshContent();
            }
        });
    }

    private void RefreshContent()
    {
        if (_bridge.State != IpcBridgeState.Connected)
        {
            ShowSingleRow("\u672A\u8FDE\u63A5\u72B6\u6001", isError: true);
            return;
        }

        var todayItem = _cache.GetTodayItem();
        if (todayItem == null)
        {
            // 缓存为空且从未成功拉取过：提示刷新中；确认今天无排班：给出明确文案。
            ShowSingleRow(_cache.State == null ? "\u6B63\u5728\u83B7\u53D6\u6392\u73ED\u6570\u636E\u2026" : "\u8BE5\u65E5\u6682\u65E0\u503C\u65E5\u5B89\u6392");
            return;
        }

        var areaOrder = todayItem.AreaAssignments.Keys.ToList();
        if (areaOrder.Count == 0)
        {
            ShowSingleRow("\u8BE5\u65E5\u6682\u65E0\u503C\u65E5\u5B89\u6392");
            return;
        }

        if (Settings?.UseDualRowDisplay == true)
        {
            RenderDualRow(areaOrder, todayItem.AreaAssignments);
        }
        else
        {
            RenderSingleRow(areaOrder, todayItem.AreaAssignments);
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
        ApplyTextStyle(DutyTextRow1, isError: false);
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
        ApplyTextStyle(DutyTextRow1, isError: false);

        DutyTextRow2.Text = FormatRowEntries(row2Entries);
        ApplyTextStyle(DutyTextRow2, isError: false);
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
        ApplyTextStyle(DutyTextRow1, isError);
        DutyTextRow2.IsVisible = false;
    }

    private void ApplyTextStyle(TextBlock textBlock, bool isError)
    {
        // 默认继承 ClassIsland 主题前景色（axaml 未设置 Foreground 即随主题），
        // 仅在用户显式配置了覆盖色或错误态时才写 Foreground。
        if (isError)
        {
            textBlock.Foreground = new SolidColorBrush(Color.Parse("#f44336"));
        }
        else if (!string.IsNullOrWhiteSpace(Settings?.FontColor))
        {
            try
            {
                textBlock.Foreground = new SolidColorBrush(
                    Avalonia.Media.Color.Parse(Settings.FontColor));
            }
            catch
            {
                // 非法颜色值：清除本地覆盖，回到继承的主题前景色。
                textBlock.ClearValue(TextBlock.ForegroundProperty);
            }
        }

        if (Settings != null)
        {
            textBlock.FontSize = Settings.FontSize;
        }
    }
}
