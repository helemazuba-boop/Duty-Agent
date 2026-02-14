using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Shared;
using DutyIsland.Models;
using DutyIsland.Services;

namespace DutyIsland.Views.SettingPages;

[FullWidthPage]
[HidePageTitle]
[SettingsPageInfo("duty.settings", "Duty-Agent", "\uE31E", "\uE31E")]
public partial class DutyMainSettingsPage : SettingsPageBase
{
    private DutyBackendService Service { get; } = IAppHost.GetService<DutyBackendService>();
    private bool _isLoadingConfig;
    private bool _isApplyingConfig;
    private string _lastConfigState = "未应用";
    private DateTime? _lastConfigAt;
    private string _lastRunState = "待命";
    private DateTime? _lastRunAt;
    private string _lastDataState = "未刷新";
    private DateTime? _lastDataAt;

    private readonly List<AreaSettingItem> _areaSettings = [];

    public DutyMainSettingsPage()
    {
        InitializeComponent();
        InitializeAreaCountOptions();
        ApplyModeComboBox.SelectedIndex = 0;
        LoadConfigForm();
        UpdateConfigTracking("已加载");
        UpdateRunTracking("待命");
        LoadData("页面初始化");
    }

    private void InitializeAreaCountOptions()
    {
        var options = Enumerable.Range(1, 30).ToList();
        NewAreaCountComboBox.ItemsSource = options;
        NewAreaCountComboBox.SelectedItem = 2;
    }

    private async void OnRunAgentClick(object? sender, RoutedEventArgs e)
    {
        var instruction = (InstructionBox.Text ?? string.Empty).Trim();
        if (instruction.Length == 0)
        {
            SetStatus("请输入排班指令。", Brushes.Orange);
            return;
        }

        var applyMode = ApplyModeComboBox.SelectedIndex switch
        {
            1 => "replace_future",
            2 => "replace_overlap",
            3 => "replace_all",
            _ => "append"
        };

        try
        {
            RunAgentBtn.IsEnabled = false;
            UpdateRunTracking("执行中");
            SetStatus("正在执行排班...", Brushes.Gray);

            var success = await Task.Run(() => Service.RunCoreAgent(instruction, applyMode));
            if (success)
            {
                LoadData("排班完成");
                UpdateRunTracking("执行成功");
                SetStatus("排班执行成功。", Brushes.Green);
            }
            else
            {
                UpdateRunTracking("执行失败");
                SetStatus("排班执行失败。", Brushes.Red);
            }
        }
        catch (Exception ex)
        {
            UpdateRunTracking("执行异常");
            SetStatus($"执行失败：{ex.Message}", Brushes.Red);
        }
        finally
        {
            RunAgentBtn.IsEnabled = true;
        }
    }

    private void OnConfigInputLostFocus(object? sender, RoutedEventArgs e)
    {
        _ = ApplyConfigFromControls("输入框失焦");
    }

    private void OnConfigInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        _ = ApplyConfigFromControls("按下 Enter");
    }

    private void OnConfigSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _ = ApplyConfigFromControls("选项变更");
    }

    private void OnConfigToggleChanged(object? sender, RoutedEventArgs e)
    {
        _ = ApplyConfigFromControls("开关切换");
    }

    private bool ApplyConfigFromControls(string trigger)
    {
        if (_isLoadingConfig || _isApplyingConfig)
        {
            return false;
        }

        Service.LoadConfig();
        var current = Service.Config;
        var requestedEnableMcp = EnableMcpSwitch.IsChecked == true;
        var requestedEnableWebDebugLayer = EnableWebDebugLayerSwitch.IsChecked == true;

        if (!TryParseBoundedInt(CoverageDaysBox.Text, 1, 30, out var coverageDays))
        {
            UpdateConfigTracking($"校验失败（{trigger}）");
            SetStatus("生成天数必须是 1-30 的整数。", Brushes.Orange);
            return false;
        }

        if (!TryParseBoundedInt(PerDayBox.Text, 1, 30, out var perDay))
        {
            UpdateConfigTracking($"校验失败（{trigger}）");
            SetStatus("默认每区域每日人数必须是 1-30 的整数。", Brushes.Orange);
            return false;
        }

        if (!BuildAreaConfigFromList(perDay, out var areaNames, out var areaPerDayCounts, out var areaError))
        {
            UpdateConfigTracking($"校验失败（{trigger}）");
            SetStatus(areaError, Brushes.Orange);
            return false;
        }

        var autoRunTime = string.IsNullOrWhiteSpace(AutoRunTimeBox.Text) ? "08:00" : AutoRunTimeBox.Text.Trim();
        var componentRefreshTime = string.IsNullOrWhiteSpace(ComponentRefreshTimeBox.Text)
            ? "08:00"
            : ComponentRefreshTimeBox.Text.Trim();

        try
        {
            _isApplyingConfig = true;
            Service.SaveUserConfig(
                apiKey: ApiKeyBox.Text ?? string.Empty,
                baseUrl: BaseUrlBox.Text?.Trim() ?? string.Empty,
                model: ModelBox.Text?.Trim() ?? string.Empty,
                enableAutoRun: EnableAutoRunSwitch.IsChecked == true,
                autoRunDay: GetSelectedAutoRunDay(),
                autoRunTime: autoRunTime,
                perDay: perDay,
                skipWeekends: SkipWeekendsSwitch.IsChecked == true,
                dutyRule: DutyRuleBox.Text ?? string.Empty,
                startFromToday: StartFromTodaySwitch.IsChecked == true,
                autoRunCoverageDays: coverageDays,
                componentRefreshTime: componentRefreshTime,
                // 普通用户页不提供 Python 路径编辑。
                pythonPath: current.PythonPath,
                areaNames: areaNames,
                areaPerDayCounts: areaPerDayCounts,
                enableMcp: requestedEnableMcp,
                enableWebViewDebugLayer: requestedEnableWebDebugLayer);

            var restartRequired = current.EnableMcp != requestedEnableMcp ||
                                  current.EnableWebViewDebugLayer != requestedEnableWebDebugLayer;
            SetStatus(
                restartRequired
                    ? $"设置已应用（{trigger}）。调试层/MCP 服务变更将在重启 ClassIsland 后生效。"
                    : $"设置已应用（{trigger}）。",
                Brushes.Green);

            UpdateConfigTracking(
                restartRequired
                    ? $"已应用（{trigger}，重启后调试层/MCP生效）"
                    : $"已应用（{trigger}）");

            return true;
        }
        catch (Exception ex)
        {
            UpdateConfigTracking("应用失败");
            SetStatus($"应用失败：{ex.Message}", Brushes.Red);
            return false;
        }
        finally
        {
            _isApplyingConfig = false;
            LoadConfigForm();
            LoadData("配置应用");
        }
    }

    private bool BuildAreaConfigFromList(
        int fallbackPerDay,
        out List<string> areaNames,
        out Dictionary<string, int> areaPerDayCounts,
        out string error)
    {
        areaNames = [];
        areaPerDayCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        error = string.Empty;

        foreach (var item in _areaSettings)
        {
            var name = NormalizeAreaName(item.Name);
            if (name.Length == 0)
            {
                continue;
            }

            if (areaPerDayCounts.ContainsKey(name))
            {
                error = $"区域名称重复：{name}";
                return false;
            }

            var count = Math.Clamp(item.Count, 1, 30);
            areaNames.Add(name);
            areaPerDayCounts[name] = count;
        }

        if (areaNames.Count == 0)
        {
            error = "至少需要保留一个值日区域。";
            return false;
        }

        var fallback = Math.Clamp(fallbackPerDay, 1, 30);
        foreach (var area in areaNames)
        {
            if (!areaPerDayCounts.TryGetValue(area, out var count))
            {
                areaPerDayCounts[area] = fallback;
            }
            else
            {
                areaPerDayCounts[area] = Math.Clamp(count, 1, 30);
            }
        }

        return true;
    }

    private void OnAreaSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (AreaSettingsListBox.SelectedItem is AreaSettingItem selected)
        {
            SelectedAreaNameBox.Text = selected.Name;
            UpdateAreaBtn.IsEnabled = true;
            DeleteAreaBtn.IsEnabled = true;
            return;
        }

        SelectedAreaNameBox.Text = string.Empty;
        UpdateAreaBtn.IsEnabled = false;
        DeleteAreaBtn.IsEnabled = false;
    }

    private void OnAddAreaClick(object? sender, RoutedEventArgs e)
    {
        var name = NormalizeAreaName(NewAreaNameBox.Text);
        if (name.Length == 0)
        {
            SetStatus("新增区域名不能为空。", Brushes.Orange);
            return;
        }

        if (_areaSettings.Any(x => string.Equals(x.Name, name, StringComparison.Ordinal)))
        {
            SetStatus("区域已存在，请勿重复添加。", Brushes.Orange);
            return;
        }

        var count = GetSelectedCount(NewAreaCountComboBox, 2);
        _areaSettings.Add(new AreaSettingItem { Name = name, Count = count });
        RenderAreaSettingsList(name);

        if (!ApplyConfigFromControls("新增区域"))
        {
            LoadConfigForm();
            LoadData("配置回滚");
            return;
        }

        NewAreaNameBox.Text = string.Empty;
        NewAreaCountComboBox.SelectedItem = 2;
        SetStatus($"已新增区域：{name}（{count} 人/天）。", Brushes.Green);
    }

    private void OnUpdateAreaClick(object? sender, RoutedEventArgs e)
    {
        if (AreaSettingsListBox.SelectedItem is not AreaSettingItem selected)
        {
            SetStatus("请先选择要修改的区域。", Brushes.Orange);
            return;
        }

        var name = NormalizeAreaName(SelectedAreaNameBox.Text);
        if (name.Length == 0)
        {
            SetStatus("区域名称不能为空。", Brushes.Orange);
            return;
        }

        var count = Math.Clamp(selected.Count, 1, 30);
        var selectedIndex = _areaSettings.IndexOf(selected);
        if (selectedIndex < 0)
        {
            selectedIndex = _areaSettings.FindIndex(x => string.Equals(x.Name, selected.Name, StringComparison.Ordinal));
        }

        if (selectedIndex < 0)
        {
            SetStatus("未找到选中的区域记录，请重试。", Brushes.Orange);
            return;
        }

        var duplicate = _areaSettings
            .Where((_, i) => i != selectedIndex)
            .Any(x => string.Equals(x.Name, name, StringComparison.Ordinal));
        if (duplicate)
        {
            SetStatus($"区域名称冲突：{name}。", Brushes.Orange);
            return;
        }

        _areaSettings[selectedIndex] = new AreaSettingItem { Name = name, Count = count };
        RenderAreaSettingsList(name);

        if (!ApplyConfigFromControls("修改区域"))
        {
            LoadConfigForm();
            LoadData("配置回滚");
            return;
        }

        SetStatus($"已更新区域：{name}（{count} 人/天）。", Brushes.Green);
    }

    private void OnAreaCountTextPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not TextBlock countText ||
            countText.DataContext is not AreaSettingItem item ||
            countText.Parent is not Panel parentPanel)
        {
            return;
        }

        var editor = parentPanel.Children.OfType<TextBox>().FirstOrDefault();
        if (editor is null)
        {
            return;
        }

        AreaSettingsListBox.SelectedItem = item;
        editor.Text = Math.Clamp(item.Count, 1, 30).ToString(CultureInfo.InvariantCulture);
        editor.Tag = item;
        countText.IsVisible = false;
        editor.IsVisible = true;
        editor.Focus();
        editor.SelectAll();
        e.Handled = true;
    }

    private void OnAreaCountEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox editor)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitInlineAreaCountEdit(editor);
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseInlineAreaCountEditor(editor);
        }
    }

    private void OnAreaCountEditorLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox editor)
        {
            CommitInlineAreaCountEdit(editor);
        }
    }

    private void CommitInlineAreaCountEdit(TextBox editor)
    {
        if (editor.Tag is not AreaSettingItem item)
        {
            CloseInlineAreaCountEditor(editor);
            return;
        }

        editor.Tag = null;
        var rawText = (editor.Text ?? string.Empty).Trim();
        if (!TryParseBoundedInt(rawText, 1, 30, out var newCount))
        {
            CloseInlineAreaCountEditor(editor);
            SetStatus("区域人数必须是 1-30 的整数。", Brushes.Orange);
            return;
        }

        CloseInlineAreaCountEditor(editor);
        if (Math.Clamp(item.Count, 1, 30) == newCount)
        {
            return;
        }

        item.Count = newCount;
        RenderAreaSettingsList(item.Name);

        if (!ApplyConfigFromControls("区域人数修改"))
        {
            LoadConfigForm();
            LoadData("配置回滚");
            return;
        }

        SetStatus($"已更新区域人数：{item.Name}（{newCount} 人/天）。", Brushes.Green);
    }

    private static void CloseInlineAreaCountEditor(TextBox editor)
    {
        editor.IsVisible = false;
        editor.Tag = null;

        if (editor.Parent is not Panel parentPanel)
        {
            return;
        }

        var countText = parentPanel.Children.OfType<TextBlock>().FirstOrDefault();
        if (countText is not null)
        {
            countText.IsVisible = true;
        }
    }

    private void OnDeleteAreaClick(object? sender, RoutedEventArgs e)
    {
        if (AreaSettingsListBox.SelectedItem is not AreaSettingItem selected)
        {
            SetStatus("请先选择要删除的区域。", Brushes.Orange);
            return;
        }

        if (_areaSettings.Count <= 1)
        {
            SetStatus("至少需要保留一个值日区域。", Brushes.Orange);
            return;
        }

        var removed = _areaSettings.Remove(selected);
        if (!removed)
        {
            SetStatus("未找到选中的区域记录。", Brushes.Orange);
            return;
        }

        RenderAreaSettingsList();

        if (!ApplyConfigFromControls("删除区域"))
        {
            LoadConfigForm();
            LoadData("配置回滚");
            return;
        }

        SetStatus($"已删除区域：{selected.Name}。", Brushes.Green);
    }

    private void RenderAreaSettingsList(string? selectName = null)
    {
        AreaSettingsListBox.ItemsSource = null;
        AreaSettingsListBox.ItemsSource = _areaSettings;

        if (!string.IsNullOrWhiteSpace(selectName))
        {
            AreaSettingsListBox.SelectedItem = _areaSettings.FirstOrDefault(x =>
                string.Equals(x.Name, selectName.Trim(), StringComparison.Ordinal));
        }
        else if (_areaSettings.Count > 0)
        {
            AreaSettingsListBox.SelectedIndex = 0;
        }

        var totalPerDay = _areaSettings.Sum(x => Math.Clamp(x.Count, 1, 30));
        AreaSummaryText.Text = _areaSettings.Count == 0
            ? "共 0 个值日区域。"
            : $"共 {_areaSettings.Count} 个值日区域，合计每日 {totalPerDay} 人。";
    }

    private void BuildAreaSettingsFromConfig(DutyConfig config)
    {
        _areaSettings.Clear();

        var names = (config.AreaNames ?? [])
            .Select(NormalizeAreaName)
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (names.Count == 0)
        {
            names.AddRange(["教室", "清洁区"]);
        }

        var fallback = Math.Clamp(config.PerDay, 1, 30);
        var counts = config.AreaPerDayCounts ?? new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var name in names)
        {
            var count = counts.TryGetValue(name, out var rawCount)
                ? Math.Clamp(rawCount, 1, 30)
                : fallback;

            _areaSettings.Add(new AreaSettingItem
            {
                Name = name,
                Count = count
            });
        }
    }

    private static string NormalizeAreaName(string? rawName)
    {
        return (rawName ?? string.Empty).Trim();
    }

    private static int GetSelectedCount(ComboBox comboBox, int fallback)
    {
        var normalizedFallback = Math.Clamp(fallback, 1, 30);

        if (comboBox.SelectedItem is int selectedInt)
        {
            return Math.Clamp(selectedInt, 1, 30);
        }

        if (comboBox.SelectedItem is string selectedText && int.TryParse(selectedText, out var parsed))
        {
            return Math.Clamp(parsed, 1, 30);
        }

        return normalizedFallback;
    }

    private void OnAddStudentClick(object? sender, RoutedEventArgs e)
    {
        var name = (NewStudentNameBox.Text ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            SetStatus("请输入学生姓名。", Brushes.Orange);
            return;
        }

        var roster = Service.LoadRosterEntries();
        if (roster.Any(x => string.Equals((x.Name ?? string.Empty).Trim(), name, StringComparison.OrdinalIgnoreCase)))
        {
            SetStatus("学生已存在，请勿重复添加。", Brushes.Orange);
            return;
        }

        var nextId = roster.Count == 0 ? 1 : roster.Max(x => x.Id) + 1;
        roster.Add(new RosterEntry
        {
            Id = nextId,
            Name = name,
            Active = true
        });
        Service.SaveRosterEntries(roster);

        NewStudentNameBox.Text = string.Empty;
        LoadData("名单变更");
        SetStatus($"已添加学生：{name}", Brushes.Green);
    }

    private void OnDeleteStudentClick(object? sender, RoutedEventArgs e)
    {
        if (RosterListBox.SelectedItem is not RosterListItem selected)
        {
            SetStatus("请先选择要删除的学生。", Brushes.Orange);
            return;
        }

        var roster = Service.LoadRosterEntries();
        var removed = roster.RemoveAll(x => x.Id == selected.Id);
        if (removed == 0)
        {
            SetStatus("未找到选中的学生记录。", Brushes.Orange);
            return;
        }

        Service.SaveRosterEntries(roster);
        LoadData("名单变更");
        SetStatus($"已删除学生：{selected.Name}", Brushes.Green);
    }

    private void OnRefreshDataClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            LoadConfigForm();
            LoadData("手动刷新");
            SetStatus("预览已刷新。", Brushes.Green);
        }
        catch (Exception ex)
        {
            SetStatus($"刷新失败：{ex.Message}", Brushes.Red);
        }
    }

    private void LoadConfigForm()
    {
        _isLoadingConfig = true;
        try
        {
            Service.LoadConfig();
            var config = Service.Config;

            ApiKeyBox.Text = config.DecryptedApiKey;
            BaseUrlBox.Text = config.BaseUrl;
            ModelBox.Text = config.Model;
            EnableAutoRunSwitch.IsChecked = config.EnableAutoRun;
            AutoRunDayComboBox.SelectedIndex = GetAutoRunDayIndex(config.AutoRunDay);
            AutoRunTimeBox.Text = config.AutoRunTime;
            CoverageDaysBox.Text = config.AutoRunCoverageDays.ToString(CultureInfo.InvariantCulture);
            PerDayBox.Text = config.PerDay.ToString(CultureInfo.InvariantCulture);
            SkipWeekendsSwitch.IsChecked = config.SkipWeekends;
            StartFromTodaySwitch.IsChecked = config.StartFromToday;
            EnableMcpSwitch.IsChecked = config.EnableMcp;
            EnableWebDebugLayerSwitch.IsChecked = config.EnableWebViewDebugLayer;
            ComponentRefreshTimeBox.Text = config.ComponentRefreshTime;
            DutyRuleBox.Text = config.DutyRule;

            BuildAreaSettingsFromConfig(config);
            RenderAreaSettingsList();
            NewAreaNameBox.Text = string.Empty;
            NewAreaCountComboBox.SelectedItem = Math.Clamp(config.PerDay, 1, 30);
        }
        finally
        {
            _isLoadingConfig = false;
        }
    }

    private void LoadData(string reason = "数据刷新")
    {
        var state = Service.LoadState();
        var roster = Service.LoadRosterEntries();
        BuildRosterList(roster, state);
        BuildSchedulePreview(state);
        UpdateDataTracking(reason);
    }

    private void BuildRosterList(List<RosterEntry> roster, DutyState state)
    {
        var previousSelectedId = (RosterListBox.SelectedItem as RosterListItem)?.Id;
        var today = DateTime.Today;
        var dutyCountByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var nextDutyByName = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in state.SchedulePool)
        {
            if (!TryParseScheduleDate(item.Date, out var date))
            {
                continue;
            }

            var namesInDay = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var assignments = Service.GetAreaAssignments(item);
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
                dutyCountByName[name] = dutyCountByName.TryGetValue(name, out var count) ? count + 1 : 1;
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

        var rows = roster.Select(r =>
            {
                var normalizedName = (r.Name ?? string.Empty).Trim();
                nextDutyByName.TryGetValue(normalizedName, out var nextDate);
                var hasNext = nextDate != default;
                var dutyCount = dutyCountByName.TryGetValue(normalizedName, out var count) ? count : 0;
                return new RosterListItem
                {
                    Id = r.Id,
                    Name = normalizedName,
                    NextDutyDate = hasNext ? nextDate : null,
                    NextDutyDisplay = hasNext ? FormatDutyDate(nextDate) : "未安排",
                    DutyCount = dutyCount,
                    ActiveDisplay = r.Active ? "启用" : "停用"
                };
            })
            .OrderBy(x => x.NextDutyDate.HasValue ? 0 : 1)
            .ThenBy(x => x.NextDutyDate)
            .ThenBy(x => x.Id)
            .ToList();

        RosterListBox.ItemsSource = rows;
        if (previousSelectedId.HasValue)
        {
            RosterListBox.SelectedItem = rows.FirstOrDefault(x => x.Id == previousSelectedId.Value);
        }

        var scheduledCount = rows.Count(x => x.NextDutyDate.HasValue);
        RosterSummaryText.Text = $"共 {rows.Count} 名学生，已安排下一次值日 {scheduledCount} 名。";
    }

    private void BuildSchedulePreview(DutyState state)
    {
        var rows = state.SchedulePool
            .OrderBy(x => x.Date, StringComparer.Ordinal)
            .Select(item =>
            {
                var assignments = Service.GetAreaAssignments(item);
                var segments = assignments
                    .Where(x => !string.IsNullOrWhiteSpace(x.Key))
                    .Select(x =>
                    {
                        var students = x.Value?.Where(n => !string.IsNullOrWhiteSpace(n)).ToList() ?? [];
                        var text = students.Count > 0 ? string.Join("、", students) : "无";
                        return $"{x.Key}: {text}";
                    })
                    .ToList();

                var summary = segments.Count > 0 ? string.Join("；", segments) : "无安排";
                return new ScheduleRowItem
                {
                    Date = item.Date,
                    Day = item.Day,
                    AssignmentSummary = summary
                };
            })
            .ToList();

        ScheduleListBox.ItemsSource = rows;
        ScheduleSummaryText.Text = rows.Count == 0
            ? "暂无排班数据。"
            : $"共 {rows.Count} 条排班记录。";
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

    private void OnOpenRosterClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var path = Service.GetRosterPath();
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            SetStatus("已打开名单文件。", Brushes.Green);
        }
        catch (Exception ex)
        {
            SetStatus($"打开名单文件失败：{ex.Message}", Brushes.Red);
        }
    }

    private static bool TryParseBoundedInt(string? text, int min, int max, out int value)
    {
        if (int.TryParse(text, out value))
        {
            return value >= min && value <= max;
        }

        value = min;
        return false;
    }

    private string GetSelectedAutoRunDay()
    {
        return AutoRunDayComboBox.SelectedIndex switch
        {
            0 => "Monday",
            1 => "Tuesday",
            2 => "Wednesday",
            3 => "Thursday",
            4 => "Friday",
            5 => "Saturday",
            6 => "Sunday",
            _ => "Monday"
        };
    }

    private static int GetAutoRunDayIndex(string? day)
    {
        var normalized = (day ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "tue" or "tuesday" or "2" or "周二" or "星期二" => 1,
            "wed" or "wednesday" or "3" or "周三" or "星期三" => 2,
            "thu" or "thursday" or "4" or "周四" or "星期四" => 3,
            "fri" or "friday" or "5" or "周五" or "星期五" => 4,
            "sat" or "saturday" or "6" or "周六" or "星期六" => 5,
            "sun" or "sunday" or "7" or "0" or "周日" or "周天" or "星期日" or "星期天" => 6,
            _ => 0
        };
    }

    private void UpdateConfigTracking(string state)
    {
        _lastConfigState = string.IsNullOrWhiteSpace(state) ? "未应用" : state.Trim();
        _lastConfigAt = DateTime.Now;
        RefreshTrackingMeta();
    }

    private void UpdateRunTracking(string state)
    {
        _lastRunState = string.IsNullOrWhiteSpace(state) ? "待命" : state.Trim();
        _lastRunAt = DateTime.Now;
        RefreshTrackingMeta();
    }

    private void UpdateDataTracking(string state)
    {
        _lastDataState = string.IsNullOrWhiteSpace(state) ? "已刷新" : state.Trim();
        _lastDataAt = DateTime.Now;
        RefreshTrackingMeta();
    }

    private void RefreshTrackingMeta()
    {
        StatusMetaText.Text = FormatTrackingSegment("配置状态", _lastConfigState, _lastConfigAt);
        RuntimeMetaText.Text =
            $"{FormatTrackingSegment("运行状态", _lastRunState, _lastRunAt)} | {FormatTrackingSegment("数据状态", _lastDataState, _lastDataAt)}";
    }

    private static string FormatTrackingSegment(string label, string state, DateTime? timestamp)
    {
        var safeLabel = string.IsNullOrWhiteSpace(label) ? "状态" : label.Trim();
        var safeState = string.IsNullOrWhiteSpace(state) ? "未知" : state.Trim();
        var time = timestamp.HasValue ? timestamp.Value.ToString("HH:mm:ss", CultureInfo.InvariantCulture) : "--:--:--";
        return $"{safeLabel}：{safeState}（{time}）";
    }

    private void SetStatus(string text, IBrush brush)
    {
        StatusText.Text = text;
        StatusText.Foreground = brush;
    }

    public sealed class AreaSettingItem
    {
        public string Name { get; set; } = string.Empty;
        public int Count { get; set; }
        public string CountDisplay => $"{Math.Clamp(Count, 1, 30)} 人/天";
    }

    public sealed class RosterListItem
    {
        public int Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public DateTime? NextDutyDate { get; init; }
        public string NextDutyDisplay { get; init; } = "未安排";
        public int DutyCount { get; init; }
        public string ActiveDisplay { get; init; } = "启用";
    }

    public sealed class ScheduleRowItem
    {
        public string Date { get; init; } = string.Empty;
        public string Day { get; init; } = string.Empty;
        public string AssignmentSummary { get; init; } = "无安排";
    }
}
