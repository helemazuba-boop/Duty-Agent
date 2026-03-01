using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Shared;
using DutyAgent.Models;
using DutyAgent.Services;

namespace DutyAgent.Views.SettingPages;

[FullWidthPage]
[HidePageTitle]
[SettingsPageInfo("duty-agent.settings", "Duty-Agent", "\uE31E", "\uE31E")]
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
    private readonly DispatcherTimer _configApplyDebounceTimer;
    private bool _hasPendingConfigApply;
    private string _pendingScheduleSelectionDate = string.Empty;
    private bool _isPopulatingEditor;

    public DutyMainSettingsPage()
    {
        InitializeComponent();
        _configApplyDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _configApplyDebounceTimer.Tick += OnConfigApplyDebounceTick;
        Unloaded += (_, _) => _configApplyDebounceTimer.Stop();
        InitializeAutoRunTimeOptions();
        InitializeAutoRunModeOptions();
        InitializeComponentRefreshTimeOptions();
        InitializeDutyReminderTimeOptions();
        InitializeScheduleDayOptions();
        LoadConfigForm();
        UpdateConfigTracking("已加载");
        UpdateRunTracking("待命");
        LoadData("页面初始化");
    }

    private void InitializeAutoRunTimeOptions()
    {
        AutoRunHourComboBox.ItemsSource = Enumerable.Range(0, 24).Select(x => x.ToString("D2")).ToList();
        AutoRunMinuteComboBox.ItemsSource = Enumerable.Range(0, 60).Select(x => x.ToString("D2")).ToList();
        AutoRunHourComboBox.SelectedItem = "08";
        AutoRunMinuteComboBox.SelectedItem = "00";
    }

    private void InitializeAutoRunModeOptions()
    {
        // Populate monthly dropdown with dynamic days + "L" option
        PopulateMonthDayComboBox();
        // Set initial visibility
        UpdateAutoRunSecondaryVisibility();
    }

    private void PopulateMonthDayComboBox()
    {
        var now = DateTime.Now;
        var daysInMonth = DateTime.DaysInMonth(now.Year, now.Month);
        var items = new List<string> { "L" }; // "当月最后一天" always first
        for (var d = 1; d <= daysInMonth; d++)
            items.Add(d.ToString());
        AutoRunMonthDayComboBox.ItemsSource = items;
        AutoRunMonthDayComboBox.SelectedIndex = 0;
    }

    private void UpdateAutoRunSecondaryVisibility()
    {
        var mode = GetSelectedAutoRunMode();
        AutoRunDayComboBox.IsVisible = mode == "Weekly";
        AutoRunMonthDayComboBox.IsVisible = mode == "Monthly";
        AutoRunIntervalBox.IsVisible = mode == "Custom";
    }

    private void InitializeComponentRefreshTimeOptions()
    {
        ComponentRefreshHourComboBox.ItemsSource = Enumerable.Range(0, 24).Select(x => x.ToString("D2")).ToList();
        ComponentRefreshMinuteComboBox.ItemsSource = Enumerable.Range(0, 60).Select(x => x.ToString("D2")).ToList();
        ComponentRefreshHourComboBox.SelectedItem = "08";
        ComponentRefreshMinuteComboBox.SelectedItem = "00";
    }

    private void InitializeDutyReminderTimeOptions()
    {
        DutyReminderHourComboBox.ItemsSource = Enumerable.Range(0, 24).Select(x => x.ToString("D2")).ToList();
        DutyReminderMinuteComboBox.ItemsSource = Enumerable.Range(0, 60).Select(x => x.ToString("D2")).ToList();
        DutyReminderHourComboBox.SelectedItem = "07";
        DutyReminderMinuteComboBox.SelectedItem = "40";
    }

    private void InitializeScheduleDayOptions()
    {
        ScheduleDayEditorComboBox.ItemsSource = new List<string>
        {
            "周一",
            "周二",
            "周三",
            "周四",
            "周五",
            "周六",
            "周日"
        };
        ScheduleDayEditorComboBox.SelectedItem = "周一";
    }

    private async void OnRunAgentClick(object? sender, RoutedEventArgs e)
    {
        var instruction = (InstructionBox.Text ?? string.Empty).Trim();
        var useDefaultInstruction = instruction.Length == 0;

        _configApplyDebounceTimer.Stop();
        FlushPendingConfigApply();

        const string applyMode = "replace_all";

        try
        {
            RunAgentBtn.IsEnabled = false;
            ReasoningBoardContainer.IsVisible = true;
            ReasoningBoardText.Text = string.Empty;
            ReasoningBoardContainer.BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#20000000")); // Subtle default border
            UpdateRunTracking("执行中");
            SetStatus(
                useDefaultInstruction
                    ? "正在执行默认排班（覆盖模式）..."
                    : "正在执行排班（覆盖模式）...",
                Brushes.Gray);

            var result = await Task.Run(() => Service.RunCoreAgentWithMessage(
                instruction,
                applyMode,
                progress: progress =>
                {
                    var phase = (progress.Phase ?? string.Empty).Trim().ToLowerInvariant();
                    if (phase.Length == 0)
                    {
                        return;
                    }

                    if (phase == "stream_chunk")
                    {
                        Dispatcher.UIThread.Post(() => 
                        {
                            SetStatus("AI 正在流式返回中...", Brushes.Gray);
                            ReasoningBoardText.Text += progress.StreamChunk;
                            ReasoningBoardScrollViewer.ScrollToEnd();
                        });
                        return;
                    }

                    var message = (progress.Message ?? string.Empty).Trim();
                    if (message.Length == 0)
                    {
                        return;
                    }

                    Dispatcher.UIThread.Post(() => SetStatus(message, Brushes.Gray));
                }));
            if (result.Success)
            {
                ReasoningBoardContainer.BorderBrush = Brushes.Green;
                LoadData("排班完成");
                UpdateRunTracking("执行成功");
                SetStatus(
                    useDefaultInstruction
                        ? "默认排班执行成功（覆盖模式）。"
                        : "排班执行成功（覆盖模式）。",
                    Brushes.Green);
            }
            else
            {
                ReasoningBoardContainer.BorderBrush = Brushes.Red;
                UpdateRunTracking("执行失败");
                var message = string.IsNullOrWhiteSpace(result.Message) ? "排班执行失败。" : $"排班执行失败：{result.Message}";
                SetStatus(message, Brushes.Red);
            }
        }
        catch (Exception ex)
        {
            ReasoningBoardContainer.BorderBrush = Brushes.Red;
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
        QueueConfigApply();
    }

    private void OnConfigInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        QueueConfigApply(immediate: true);
    }

    private void OnConfigSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender == AutoRunModeComboBox)
        {
            UpdateAutoRunSecondaryVisibility();
        }
        QueueConfigApply();
    }

    private void OnConfigToggleChanged(object? sender, RoutedEventArgs e)
    {
        QueueConfigApply();
    }

    private void QueueConfigApply(bool immediate = false)
    {
        if (_isLoadingConfig || _isApplyingConfig)
        {
            return;
        }

        _hasPendingConfigApply = true;

        _configApplyDebounceTimer.Stop();
        if (immediate)
        {
            FlushPendingConfigApply();
            return;
        }

        _configApplyDebounceTimer.Start();
    }

    private void OnConfigApplyDebounceTick(object? sender, EventArgs e)
    {
        _configApplyDebounceTimer.Stop();
        FlushPendingConfigApply();
    }

    private void FlushPendingConfigApply()
    {
        if (!_hasPendingConfigApply)
        {
            return;
        }

        _hasPendingConfigApply = false;
        _ = ApplyConfigFromControls();
    }

    private bool ApplyConfigFromControls()
    {
        if (_isLoadingConfig || _isApplyingConfig)
        {
            return false;
        }

        Service.LoadConfig();
        var current = Service.Config;
        var requestedEnableMcp = EnableMcpSwitch.IsChecked == true;
        var requestedEnableWebDebugLayer = EnableWebDebugLayerSwitch.IsChecked == true;
        var previousEnableMcp = current.EnableMcp;
        var previousEnableWebDebugLayer = current.EnableWebViewDebugLayer;

        var dutyReminderEnabled = DutyReminderEnabledSwitch.IsChecked == true;
        var dutyReminderTimes = new List<string> { GetSelectedDutyReminderTime() };

        var perDay = Math.Clamp(current.PerDay, 1, 30);

        var autoRunTime = GetSelectedAutoRunTime();
        var componentRefreshTime = GetSelectedComponentRefreshTime();

        try
        {
            _isApplyingConfig = true;
            var resolvedApiKey = DutyBackendService.ResolveApiKeyInput(ApiKeyBox.Text, current.DecryptedApiKey);
            Service.SaveUserConfig(
                apiKey: resolvedApiKey,
                baseUrl: BaseUrlBox.Text?.Trim() ?? string.Empty,
                model: ModelBox.Text?.Trim() ?? string.Empty,
                autoRunMode: GetSelectedAutoRunMode(),
                autoRunParameter: GetSelectedAutoRunParameter(),
                autoRunTime: autoRunTime,
                perDay: perDay,
                dutyRule: DutyRuleBox.Text ?? string.Empty,
                componentRefreshTime: componentRefreshTime,
                // 普通用户页不提供 Python 路径编辑。
                pythonPath: current.PythonPath,
                dutyReminderEnabled: dutyReminderEnabled,
                dutyReminderTimes: dutyReminderTimes,
                enableMcp: requestedEnableMcp,
                enableWebViewDebugLayer: requestedEnableWebDebugLayer,
                autoRunTriggerNotificationEnabled: AutoRunTriggerNotificationSwitch.IsChecked == true);

            var restartRequired = previousEnableMcp != requestedEnableMcp ||
                                  previousEnableWebDebugLayer != requestedEnableWebDebugLayer;
            SetStatus(
                restartRequired
                    ? "✓ 设置已自动保存。调试层/MCP 服务变更将在重启 ClassIsland 后生效。"
                    : "✓ 设置已自动保存。",
                Brushes.Green);

            UpdateConfigTracking(
                restartRequired
                    ? "✓ 自动保存（重启后调试层/MCP生效）"
                    : "✓ 自动保存");

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

    private void OnAddStudentClick(object? sender, RoutedEventArgs e)
    {
        var name = (NewStudentNameBox.Text ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            SetStatus("请输入学生姓名。", Brushes.Orange);
            return;
        }

        var roster = Service.LoadRosterEntries();
        var nextId = roster.Count == 0 ? 1 : roster.Max(x => x.Id) + 1;
        
        var isDuplicate = roster.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        
        roster.Add(new RosterEntry
        {
            Id = nextId,
            Name = name,
            Active = true
        });
        Service.SaveRosterEntries(roster);

        NewStudentNameBox.Text = string.Empty;
        LoadData("名单变更");
        
        if (isDuplicate)
        {
            SetStatus($"已添加学生：{name} (库中存在同名，已分配新ID)。", Brushes.Orange);
        }
        else
        {
            SetStatus($"已添加学生：{name}", Brushes.Green);
        }
    }

    private void OnToggleStudentActiveClick(object? sender, RoutedEventArgs e)
    {
        if (RosterListBox.SelectedItem is not RosterListItem selected)
        {
            SetStatus("请先选择要操作的学生。", Brushes.Orange);
            return;
        }

        var roster = Service.LoadRosterEntries();
        var index = roster.FindIndex(x => x.Id == selected.Id);
        if (index < 0)
        {
            SetStatus("未找到选中的学生记录。", Brushes.Orange);
            return;
        }

        roster[index].Active = !roster[index].Active;
        Service.SaveRosterEntries(roster);
        LoadData("名单变更");
        var statusText = roster[index].Active ? "启用" : "停用";
        SetStatus($"已{statusText}学生：{roster[index].Name}", Brushes.Green);
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

    private async void OnImportRosterFromFileClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null)
            {
                SetStatus("当前环境不支持文件选择。", Brushes.Orange);
                return;
            }

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择学生名单文件",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("名单文本 (*.txt)") { Patterns = ["*.txt"] },
                    new FilePickerFileType("Excel 名单 (*.xlsx)") { Patterns = ["*.xlsx"] }
                ]
            });
            var file = files.FirstOrDefault();
            if (file == null)
            {
                SetStatus("已取消导入。", Brushes.Gray);
                return;
            }

            var localPath = file.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(localPath))
            {
                SetStatus("仅支持导入本地文件。", Brushes.Orange);
                return;
            }

            var parsedNames = RosterImportFileHelper.LoadStudentNames(localPath);
            if (parsedNames.Count == 0)
            {
                SetStatus("文件中未解析到有效姓名。", Brushes.Orange);
                return;
            }

            var roster = Service.LoadRosterEntries();
            var nextId = roster.Count == 0 ? 1 : roster.Max(x => x.Id) + 1;
            foreach (var name in parsedNames)
            {
                roster.Add(new RosterEntry
                {
                    Id = nextId++,
                    Name = name,
                    Active = true
                });
            }

            Service.SaveRosterEntries(roster);
            LoadData("名单导入");
            SetStatus($"导入完成：新增 {parsedNames.Count} 名（同名将保留并分配不同 ID）。", Brushes.Green);
        }
        catch (Exception ex)
        {
            SetStatus($"导入失败：{ex.Message}", Brushes.Red);
        }
    }

    private void OnRosterSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateStudentActionButtons();
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

            ApiKeyBox.Text = Service.GetApiKeyMaskForUi();
            BaseUrlBox.Text = config.BaseUrl;
            ModelBox.Text = config.Model;
            SetAutoRunModeSelection(config.AutoRunMode);
            SetAutoRunParameterSelection(config.AutoRunMode, config.AutoRunParameter);
            SetAutoRunTimeSelection(config.AutoRunTime);
            AutoRunTriggerNotificationSwitch.IsChecked = config.AutoRunTriggerNotificationEnabled;
            DutyReminderEnabledSwitch.IsChecked = config.DutyReminderEnabled;
            SetDutyReminderTimeSelection(Service.GetDutyReminderTimes().FirstOrDefault());
            EnableMcpSwitch.IsChecked = config.EnableMcp;
            EnableWebDebugLayerSwitch.IsChecked = config.EnableWebViewDebugLayer;
            SetComponentRefreshTimeSelection(config.ComponentRefreshTime);
            DutyRuleBox.Text = config.DutyRule;
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
        UpdateStudentActionButtons();
        UpdateDataTracking(reason);
    }

    private void UpdateStudentActionButtons()
    {
        if (RosterListBox.SelectedItem is RosterListItem selected)
        {
            ToggleStudentActiveBtn.IsEnabled = true;
            ToggleStudentActiveBtn.Content = selected.Active ? "停用选中" : "启用选中";
            return;
        }

        ToggleStudentActiveBtn.IsEnabled = false;
        ToggleStudentActiveBtn.Content = "启用/停用选中";
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
                    Active = r.Active,
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
        var activeCount = rows.Count(x => x.Active);
        RosterSummaryText.Text = $"共 {rows.Count} 名学生（启用 {activeCount} 名），已安排下一次值日 {scheduledCount} 名。";
        RosterEmptyStatePanel.IsVisible = rows.Count == 0;
    }

    private void BuildSchedulePreview(DutyState state)
    {
        var previousSelectedDate = string.IsNullOrWhiteSpace(_pendingScheduleSelectionDate)
            ? (ScheduleListBox.SelectedItem as ScheduleRowItem)?.Date
            : _pendingScheduleSelectionDate;
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
                    AssignmentSummary = summary,
                    Note = (item.Note ?? string.Empty).Trim()
                };
            })
            .ToList();

        ScheduleListBox.ItemsSource = rows;
        if (!string.IsNullOrWhiteSpace(previousSelectedDate))
        {
            ScheduleListBox.SelectedItem = rows.LastOrDefault(x =>
                string.Equals(x.Date, previousSelectedDate, StringComparison.Ordinal));
        }
        _pendingScheduleSelectionDate = string.Empty;

        PopulateScheduleEditorFromSelection();
        ScheduleSummaryText.Text = rows.Count == 0
            ? "暂无排班数据。"
            : $"共 {rows.Count} 条排班记录。";
        ScheduleEmptyStatePanel.IsVisible = rows.Count == 0;
    }

    private void OnScheduleSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        PopulateScheduleEditorFromSelection();
    }

    private void OnReloadScheduleEditClick(object? sender, RoutedEventArgs e)
    {
        PopulateScheduleEditorFromSelection();
    }

    private void OnSaveScheduleEditClick(object? sender, RoutedEventArgs e)
    {
        if (ScheduleListBox.SelectedItem is not ScheduleRowItem selected)
        {
            SetStatus("请先选择要编辑的排班记录。", Brushes.Orange);
            return;
        }

        if (!TryParseAreaAssignmentsEditorText(
                ScheduleAssignmentsEditorBox.Text,
                out var areaAssignments,
                out var invalidLine))
        {
            SetStatus($"值日安排格式错误：{invalidLine}（请使用“区域: 姓名1, 姓名2”）。", Brushes.Orange);
            return;
        }

        if (!TryNormalizeScheduleDate(ScheduleDateEditorBox.Text, out var targetDate, out var targetDateValue))
        {
            SetStatus("日期格式错误，请使用 yyyy-MM-dd。", Brushes.Orange);
            return;
        }

        var selectedDay = ScheduleDayEditorComboBox.SelectedItem as string;
        var targetDay = string.IsNullOrWhiteSpace(selectedDay)
            ? ToChineseWeekday(targetDateValue.DayOfWeek)
            : selectedDay.Trim();

        var note = (ScheduleNoteEditorBox.Text ?? string.Empty).Trim();
        if (!Service.TrySaveScheduleEntry(
                sourceDate: selected.Date,
                targetDate: targetDate,
                day: targetDay,
                areaAssignments: areaAssignments,
                note: note,
                createIfMissing: false,
                out var message))
        {
            SetStatus($"保存失败：{message}", Brushes.Red);
            return;
        }

        _pendingScheduleSelectionDate = targetDate;
        LoadData("手动编辑排班");
        SetStatus("排班编辑已保存。", Brushes.Green);
    }

    private void OnCreateScheduleEditClick(object? sender, RoutedEventArgs e)
    {
        if (!TryParseAreaAssignmentsEditorText(
                ScheduleAssignmentsEditorBox.Text,
                out var areaAssignments,
                out var invalidLine))
        {
            SetStatus($"值日安排格式错误：{invalidLine}（请使用“区域: 姓名1, 姓名2”）。", Brushes.Orange);
            return;
        }

        if (!TryNormalizeScheduleDate(ScheduleDateEditorBox.Text, out var targetDate, out var targetDateValue))
        {
            SetStatus("日期格式错误，请使用 yyyy-MM-dd。", Brushes.Orange);
            return;
        }

        var selectedDay = ScheduleDayEditorComboBox.SelectedItem as string;
        var targetDay = string.IsNullOrWhiteSpace(selectedDay)
            ? ToChineseWeekday(targetDateValue.DayOfWeek)
            : selectedDay.Trim();
        var note = (ScheduleNoteEditorBox.Text ?? string.Empty).Trim();

        if (!Service.TrySaveScheduleEntry(
                sourceDate: null,
                targetDate: targetDate,
                day: targetDay,
                areaAssignments: areaAssignments,
                note: note,
                createIfMissing: true,
                out var message))
        {
            SetStatus($"新建失败：{message}", Brushes.Red);
            return;
        }

        _pendingScheduleSelectionDate = targetDate;
        LoadData("手动新建排班");
        SetStatus("已新建值日安排。", Brushes.Green);
    }

    private void PopulateScheduleEditorFromSelection()
    {
        _isPopulatingEditor = true;
        try
        {
            if (ScheduleListBox.SelectedItem is not ScheduleRowItem selected)
            {
                SelectedScheduleMetaText.Text = "请先在上方列表选择一条排班记录。";
                ScheduleDateEditorBox.Text = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                ScheduleDayEditorComboBox.SelectedItem = ToChineseWeekday(DateTime.Today.DayOfWeek);
                ScheduleAssignmentsEditorBox.Text = string.Empty;
                ScheduleNoteEditorBox.Text = string.Empty;
                SaveScheduleEditBtn.IsEnabled = false;
                return;
            }

            var state = Service.LoadState();
            var item = state.SchedulePool.LastOrDefault(x => string.Equals(x.Date, selected.Date, StringComparison.Ordinal));
            if (item == null)
            {
                SelectedScheduleMetaText.Text = "当前记录不存在，可能已被刷新。";
                ScheduleDateEditorBox.Text = string.Empty;
                ScheduleDayEditorComboBox.SelectedItem = null;
                ScheduleAssignmentsEditorBox.Text = string.Empty;
                ScheduleNoteEditorBox.Text = string.Empty;
                SaveScheduleEditBtn.IsEnabled = false;
                return;
            }

            var assignments = Service.GetAreaAssignments(item);
            ScheduleDateEditorBox.Text = (item.Date ?? string.Empty).Trim();
            ScheduleDayEditorComboBox.SelectedItem = ResolveScheduleDaySelection(item.Day, item.Date);
            ScheduleAssignmentsEditorBox.Text = FormatAreaAssignmentsForEditor(assignments);
            ScheduleNoteEditorBox.Text = (item.Note ?? string.Empty).Trim();
            SelectedScheduleMetaText.Text = $"当前编辑：{selected.Date} {selected.Day}";
            SaveScheduleEditBtn.IsEnabled = true;
        }
        finally
        {
            ScheduleListBox.IsEnabled = true;
            _isPopulatingEditor = false;
        }
    }

    private void OnScheduleEditorTextChanged(object? sender, Avalonia.Controls.TextChangedEventArgs e)
    {
        HandleScheduleEditorChanged();
    }

    private void OnScheduleEditorSelectionChanged(object? sender, Avalonia.Controls.SelectionChangedEventArgs e)
    {
        HandleScheduleEditorChanged();
    }

    private void HandleScheduleEditorChanged()
    {
        if (_isPopulatingEditor) return;
        ScheduleListBox.IsEnabled = false;
        SelectedScheduleMetaText.Text = "（您有未保存的修改。若想切换日期，请先保存或点击【重新载入】）";
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

    private string GetSelectedDutyReminderTime()
    {
        var hour = DutyReminderHourComboBox.SelectedItem as string ?? "07";
        var minute = DutyReminderMinuteComboBox.SelectedItem as string ?? "40";
        return $"{hour}:{minute}";
    }

    private void SetDutyReminderTimeSelection(string? dutyReminderTime)
    {
        if (!TimeSpan.TryParse(dutyReminderTime, out var parsed))
        {
            parsed = new TimeSpan(7, 40, 0);
        }

        DutyReminderHourComboBox.SelectedItem = parsed.Hours.ToString("D2");
        DutyReminderMinuteComboBox.SelectedItem = parsed.Minutes.ToString("D2");
    }

    private static bool TryNormalizeScheduleDate(string? rawDate, out string normalizedDate, out DateTime parsedDate)
    {
        normalizedDate = string.Empty;
        parsedDate = default;
        var text = (rawDate ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return false;
        }

        if (DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsedDate) ||
            DateTime.TryParse(text, out parsedDate))
        {
            normalizedDate = parsedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return true;
        }

        return false;
    }

    private static string ResolveScheduleDaySelection(string? day, string? date)
    {
        var normalizedDay = (day ?? string.Empty).Trim();
        if (normalizedDay.Length > 0)
        {
            return normalizedDay;
        }

        if (TryNormalizeScheduleDate(date, out _, out var parsedDate))
        {
            return ToChineseWeekday(parsedDate.DayOfWeek);
        }

        return "周一";
    }

    private static string FormatAreaAssignmentsForEditor(IDictionary<string, List<string>> assignments)
    {
        var lines = assignments
            .Where(x => !string.IsNullOrWhiteSpace(x.Key))
            .Select(x =>
            {
                var names = (x.Value ?? [])
                    .Select(v => (v ?? string.Empty).Trim())
                    .Where(v => v.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                var nameText = names.Count > 0 ? string.Join(", ", names) : string.Empty;
                return $"{x.Key}: {nameText}".TrimEnd();
            })
            .ToList();

        return string.Join(Environment.NewLine, lines);
    }

    private static bool TryParseAreaAssignmentsEditorText(
        string? text,
        out Dictionary<string, List<string>> assignments,
        out string invalidLine)
    {
        assignments = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        invalidLine = string.Empty;

        var rawText = text ?? string.Empty;
        foreach (var rawLine in rawText.Split(['\r', '\n', ';', '\uFF1B'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var colonIndex = line.IndexOf(':');
            var cnColonIndex = line.IndexOf('\uFF1A');
            if (colonIndex < 0 || (cnColonIndex >= 0 && cnColonIndex < colonIndex))
            {
                colonIndex = cnColonIndex;
            }

            if (colonIndex <= 0)
            {
                invalidLine = line;
                return false;
            }

            var area = line[..colonIndex].Trim();
            if (area.Length == 0)
            {
                invalidLine = line;
                return false;
            }

            var studentsText = line[(colonIndex + 1)..].Trim();
            var students = studentsText.Split([',', '\uFF0C', '\u3001', '/', '|', ' ', '\t', '\u200B'], StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            assignments[area] = students;
        }

        return true;
    }

    private string GetSelectedAutoRunTime()
    {
        var hour = AutoRunHourComboBox.SelectedItem as string ?? "08";
        var minute = AutoRunMinuteComboBox.SelectedItem as string ?? "00";
        return $"{hour}:{minute}";
    }

    private string GetSelectedComponentRefreshTime()
    {
        var hour = ComponentRefreshHourComboBox.SelectedItem as string ?? "08";
        var minute = ComponentRefreshMinuteComboBox.SelectedItem as string ?? "00";
        return $"{hour}:{minute}";
    }

    private void SetAutoRunTimeSelection(string? autoRunTime)
    {
        if (!TimeSpan.TryParse(autoRunTime, out var parsed))
        {
            parsed = new TimeSpan(8, 0, 0);
        }

        AutoRunHourComboBox.SelectedItem = parsed.Hours.ToString("D2");
        AutoRunMinuteComboBox.SelectedItem = parsed.Minutes.ToString("D2");
    }

    private void SetComponentRefreshTimeSelection(string? refreshTime)
    {
        if (!TimeSpan.TryParse(refreshTime, out var parsed))
        {
            parsed = new TimeSpan(8, 0, 0);
        }

        ComponentRefreshHourComboBox.SelectedItem = parsed.Hours.ToString("D2");
        ComponentRefreshMinuteComboBox.SelectedItem = parsed.Minutes.ToString("D2");
    }

    private string GetSelectedAutoRunMode()
    {
        return AutoRunModeComboBox.SelectedIndex switch
        {
            0 => "Off",
            1 => "Weekly",
            2 => "Monthly",
            3 => "Custom",
            _ => "Off"
        };
    }

    private string GetSelectedAutoRunParameter()
    {
        return GetSelectedAutoRunMode() switch
        {
            "Weekly" => AutoRunDayComboBox.SelectedIndex switch
            {
                0 => "Monday",
                1 => "Tuesday",
                2 => "Wednesday",
                3 => "Thursday",
                4 => "Friday",
                5 => "Saturday",
                6 => "Sunday",
                _ => "Monday"
            },
            "Monthly" => AutoRunMonthDayComboBox.SelectedItem as string ?? "L",
            "Custom" => AutoRunIntervalBox.Text?.Trim() ?? "14",
            _ => "Monday"
        };
    }

    private void SetAutoRunModeSelection(string mode)
    {
        AutoRunModeComboBox.SelectedIndex = (mode ?? "Off").Trim().ToLowerInvariant() switch
        {
            "weekly" => 1,
            "monthly" => 2,
            "custom" => 3,
            _ => 0
        };
    }

    private void SetAutoRunParameterSelection(string mode, string parameter)
    {
        var param = (parameter ?? string.Empty).Trim();
        switch ((mode ?? "Off").Trim().ToLowerInvariant())
        {
            case "weekly":
            {
                var normalized = param.ToLowerInvariant();
                AutoRunDayComboBox.SelectedIndex = normalized switch
                {
                    "tue" or "tuesday" or "2" => 1,
                    "wed" or "wednesday" or "3" => 2,
                    "thu" or "thursday" or "4" => 3,
                    "fri" or "friday" or "5" => 4,
                    "sat" or "saturday" or "6" => 5,
                    "sun" or "sunday" or "7" or "0" => 6,
                    _ => 0
                };
                break;
            }
            case "monthly":
            {
                var items = AutoRunMonthDayComboBox.ItemsSource as IList<string>;
                if (items != null && items.Contains(param))
                    AutoRunMonthDayComboBox.SelectedItem = param;
                else
                    AutoRunMonthDayComboBox.SelectedIndex = 0; // "L"
                break;
            }
            case "custom":
                AutoRunIntervalBox.Text = int.TryParse(param, out var days) ? days.ToString() : "14";
                break;
        }
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

    public sealed class RosterListItem
    {
        public int Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public DateTime? NextDutyDate { get; init; }
        public string NextDutyDisplay { get; init; } = "未安排";
        public int DutyCount { get; init; }
        public bool Active { get; init; }
        public string ActiveDisplay { get; init; } = "启用";
    }

    public sealed class ScheduleRowItem
    {
        public string Date { get; init; } = string.Empty;
        public string Day { get; init; } = string.Empty;
        public string AssignmentSummary { get; init; } = "无安排";
        public string Note { get; init; } = string.Empty;
    }
}
