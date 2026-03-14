using System.Diagnostics;
using System.Globalization;
using System.Threading;
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
using DutyAgent.Views.SettingPages.Modules;
using FluentAvalonia.UI.Controls;

namespace DutyAgent.Views.SettingPages;

[FullWidthPage]
[HidePageTitle]
[SettingsPageInfo("duty-agent.settings", "Duty-Agent", "\uE31E", "\uE31E")]
public partial class DutyMainSettingsPage : SettingsPageBase
{
    private DutyScheduleOrchestrator Service { get; } = IAppHost.GetService<DutyScheduleOrchestrator>();
    private DutyNotificationService NotificationService { get; } = IAppHost.GetService<DutyNotificationService>();
    private readonly DutyMainSettingsHostModule _hostModule;
    private readonly DutyMainSettingsBackendModule _backendModule;
    private readonly DutyMainSettingsSaveCoordinator _saveCoordinator;
    private readonly DutyMainSettingsRosterModule _rosterModule;
    private readonly DutyMainSettingsScheduleModule _scheduleModule;
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
    private DutyBackendConfigLoadState _backendConfigState = DutyBackendConfigLoadState.NotLoaded;
    private bool _isLoadingBackendConfig;
    private string? _backendConfigErrorMessage;
    private int _configLoadRevision;
    private int _dataLoadRevision;
    private DutyHostSettingsValues _lastAppliedHostSettings = new();
    private DutyBackendConfig? _lastAppliedBackendConfig;
    private List<DutyPlanPreset> _planPresetDrafts = [];
    private string _currentPlanId = string.Empty;

    public DutyMainSettingsPage()
    {
        InitializeComponent();
        _hostModule = new DutyMainSettingsHostModule(Service);
        _backendModule = new DutyMainSettingsBackendModule(Service);
        _saveCoordinator = new DutyMainSettingsSaveCoordinator(_hostModule, _backendModule);
        _rosterModule = new DutyMainSettingsRosterModule(Service);
        _scheduleModule = new DutyMainSettingsScheduleModule(Service);
        _configApplyDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _configApplyDebounceTimer.Tick += OnConfigApplyDebounceTick;
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
        InitializeAutoRunTimeOptions();
        InitializeAutoRunModeOptions();
        InitializeComponentRefreshTimeOptions();
        InitializeDutyReminderTimeOptions();
        InitializeScheduleDayOptions();
        UpdateExecutionModeVisibility();
        SetDataView(showScheduleView: true);
        LoadLocalConfigForm();
        UpdateConfigTracking("已加载本地配置");
        UpdateRunTracking("待命");
        _ = LoadDataAsync("页面初始化");
    }

    private async void OnPageLoaded(object? sender, RoutedEventArgs e)
    {
        DutyDiagnosticsLogger.Info("SettingsPage", "Settings page loaded; beginning backend config load.");
        await LoadBackendConfigAsync();
    }

    private void OnPageUnloaded(object? sender, RoutedEventArgs e)
    {
        _configApplyDebounceTimer.Stop();
        Interlocked.Increment(ref _configLoadRevision);
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
        var isWeekly = mode == "Weekly";
        var isMonthly = mode == "Monthly";
        var isCustom = mode == "Custom";

        AutoRunWeeklyItem.IsVisible = isWeekly;
        AutoRunMonthlyItem.IsVisible = isMonthly;
        AutoRunIntervalItem.IsVisible = isCustom;
        AutoRunDayComboBox.IsVisible = isWeekly;
        AutoRunMonthDayComboBox.IsVisible = isMonthly;
        AutoRunIntervalBox.IsVisible = isCustom;
    }

    private void UpdateExecutionModeVisibility()
    {
        var isMultiAgent = string.Equals(GetSelectedPlanModeId(), DutyBackendModeIds.Campus6Agent, StringComparison.Ordinal);
        MultiAgentExecutionModeItem.IsVisible = isMultiAgent;
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
        await FlushPendingConfigApplyAsync();

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

            var result = await Service.RunCoreAgentAsync(
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
                });
            if (result.Success)
            {
                ReasoningBoardContainer.BorderBrush = Brushes.Green;
                await LoadDataAsync("排班完成");
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
        CaptureCurrentPlanDraft();
        RefreshPlanSelectors();
        QueueConfigApply();
    }

    private void OnConfigInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        CaptureCurrentPlanDraft();
        RefreshPlanSelectors();
        QueueConfigApply(immediate: true);
    }

    private void OnConfigSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender == AutoRunModeComboBox)
        {
            UpdateAutoRunSecondaryVisibility();
        }
        else if (sender == PlanModeComboBox)
        {
            UpdateExecutionModeVisibility();
            UpdatePlanModeHint();
        }

        CaptureCurrentPlanDraft();
        QueueConfigApply();
    }

    private void OnCurrentPlanSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingConfig)
        {
            return;
        }

        CaptureCurrentPlanDraft();
        _currentPlanId = GetSelectedPlanOptionId();
        ApplyCurrentPlanToControls();
        RefreshPlanSelectors();
        QueueConfigApply();
    }

    private void OnCreatePlanClick(object? sender, RoutedEventArgs e)
    {
        if (_isLoadingConfig)
        {
            return;
        }

        CaptureCurrentPlanDraft();
        var plan = CreateDraftPlan(copyFromCurrent: false);
        _planPresetDrafts.Add(plan);
        _currentPlanId = plan.Id;
        RefreshPlanSelectors();
        ApplyCurrentPlanToControls();
        QueueConfigApply(immediate: true);
    }

    private void OnDuplicatePlanClick(object? sender, RoutedEventArgs e)
    {
        if (_isLoadingConfig)
        {
            return;
        }

        CaptureCurrentPlanDraft();
        var plan = CreateDraftPlan(copyFromCurrent: true);
        _planPresetDrafts.Add(plan);
        _currentPlanId = plan.Id;
        RefreshPlanSelectors();
        ApplyCurrentPlanToControls();
        QueueConfigApply(immediate: true);
    }

    private void OnDeletePlanClick(object? sender, RoutedEventArgs e)
    {
        if (_isLoadingConfig || _planPresetDrafts.Count <= 1)
        {
            return;
        }

        CaptureCurrentPlanDraft();
        var removedPlanId = GetSelectedPlanOptionId();
        var remaining = _planPresetDrafts
            .Where(plan => !string.Equals(plan.Id, removedPlanId, StringComparison.Ordinal))
            .ToList();

        if (remaining.Count == 0)
        {
            return;
        }

        _planPresetDrafts = remaining;
        _currentPlanId = remaining[0].Id;
        RefreshPlanSelectors();
        ApplyCurrentPlanToControls();
        QueueConfigApply(immediate: true);
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
            _ = FlushPendingConfigApplyAsync();
            return;
        }

        _configApplyDebounceTimer.Start();
    }

    private void OnConfigApplyDebounceTick(object? sender, EventArgs e)
    {
        _configApplyDebounceTimer.Stop();
        _ = FlushPendingConfigApplyAsync();
    }

    private async Task FlushPendingConfigApplyAsync()
    {
        if (!_hasPendingConfigApply || _isApplyingConfig || _isLoadingConfig)
        {
            return;
        }

        _hasPendingConfigApply = false;
        await ApplyConfigFromControlsAsync();
    }

    private async Task<bool> ApplyConfigFromControlsAsync()
    {
        if (_isLoadingConfig || _isApplyingConfig)
        {
            return false;
        }

        try
        {
            _isApplyingConfig = true;
            var outcome = await _saveCoordinator.ApplyAsync(new DutySettingsSaveContext
            {
                Current = BuildSettingsPageValuesFromControls(),
                LastAppliedHost = _lastAppliedHostSettings,
                LastAppliedBackend = _lastAppliedBackendConfig,
                BackendLoadState = _backendConfigState,
                BackendErrorMessage = _backendConfigErrorMessage ?? string.Empty
            });

            if (outcome.NoChanges)
            {
                return true;
            }

            if (outcome.AppliedHost != null)
            {
                _lastAppliedHostSettings = outcome.AppliedHost;
            }

            if (outcome.AppliedBackend != null)
            {
                _lastAppliedBackendConfig = _backendModule.CloneConfig(outcome.AppliedBackend);
                _backendConfigState = DutyBackendConfigLoadState.Loaded;
                _backendConfigErrorMessage = null;
                ApplyBackendConfig(outcome.AppliedBackend);
            }

            if (!outcome.Success)
            {
                UpdateConfigTracking("应用失败");
                SetStatus(outcome.Message, GetStatusBrush(outcome.MessageLevel));
                return false;
            }

            UpdateConfigTracking(
                outcome.RestartRequired
                    ? "自动保存（需重启）"
                    : "自动保存");

            if (outcome.RestartRequired)
            {
                RequestRestart();
            }

            SetStatus(outcome.Message, GetStatusBrush(outcome.MessageLevel));
            return true;
        }
        finally
        {
            _isApplyingConfig = false;
            if (_hasPendingConfigApply)
            {
                _ = FlushPendingConfigApplyAsync();
            }
        }
    }

    private void OnAddStudentClick(object? sender, RoutedEventArgs e)
    {
        var result = _rosterModule.AddStudent(NewStudentNameBox.Text);
        if (!result.Success)
        {
            SetStatus(result.Message, Brushes.Orange);
            return;
        }

        NewStudentNameBox.Text = string.Empty;
        _ = LoadDataAsync("名单变更");
        SetStatus(result.Message, result.IsDuplicate ? Brushes.Orange : Brushes.Green);
    }

    private void OnToggleStudentActiveClick(object? sender, RoutedEventArgs e)
    {
        if (RosterListBox.SelectedItem is not DutyRosterRow selected)
        {
            SetStatus("请先选择要操作的学生。", Brushes.Orange);
            return;
        }

        var result = _rosterModule.ToggleStudentActive(selected.Id);
        if (!result.Success)
        {
            SetStatus(result.Message, Brushes.Orange);
            return;
        }

        _ = LoadDataAsync("名单变更");
        SetStatus(result.Message, Brushes.Green);
    }

    private void OnDeleteStudentClick(object? sender, RoutedEventArgs e)
    {
        if (RosterListBox.SelectedItem is not DutyRosterRow selected)
        {
            SetStatus("请先选择要删除的学生。", Brushes.Orange);
            return;
        }

        var result = _rosterModule.DeleteStudent(selected.Id);
        if (!result.Success)
        {
            SetStatus(result.Message, Brushes.Orange);
            return;
        }

        _ = LoadDataAsync("名单变更");
        SetStatus(result.Message, Brushes.Green);
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

            var result = _rosterModule.ImportStudents(parsedNames);
            if (!result.Success)
            {
                SetStatus(result.Message, Brushes.Orange);
                return;
            }

            await LoadDataAsync("名单导入");
            SetStatus(result.Message, Brushes.Green);
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

    private void OnShowScheduleViewClick(object? sender, RoutedEventArgs e)
    {
        SetDataView(showScheduleView: true);
    }

    private void OnShowRosterViewClick(object? sender, RoutedEventArgs e)
    {
        SetDataView(showScheduleView: false);
    }

    private async void OnRefreshDataClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            await Task.WhenAll(
                LoadDataAsync("手动刷新"),
                LoadBackendConfigAsync(forceReload: true));
            if (_backendConfigState == DutyBackendConfigLoadState.Loaded)
            {
                SetStatus("预览已刷新。", Brushes.Green);
            }
        }
        catch (Exception ex)
        {
            SetStatus($"刷新失败：{ex.Message}", Brushes.Red);
        }
    }

    private void LoadLocalConfigForm()
    {
        var hostSettings = _hostModule.Load();
        ApplyHostFormModel(hostSettings);
        _lastAppliedHostSettings = hostSettings;
        _backendConfigState = DutyBackendConfigLoadState.NotLoaded;
        _backendConfigErrorMessage = null;
        _lastAppliedBackendConfig = null;
        _planPresetDrafts = [];
        _currentPlanId = string.Empty;
        SetBackendConfigControlsEnabled(false);
        SetStatus("正在连接后端配置...", Brushes.Gray);
        DutyDiagnosticsLogger.Info("SettingsPage", "Loaded host-only settings and marked backend config as not loaded.");
    }

    private async Task<bool> LoadBackendConfigAsync(bool forceReload = false)
    {
        if (_isLoadingBackendConfig)
        {
            return _backendConfigState == DutyBackendConfigLoadState.Loaded;
        }

        if (_backendConfigState == DutyBackendConfigLoadState.Loaded && !forceReload)
        {
            return true;
        }

        var revision = Interlocked.Increment(ref _configLoadRevision);
        var traceId = DutyDiagnosticsLogger.CreateTraceId("cfg-load");
        var stopwatch = Stopwatch.StartNew();
        _isLoadingBackendConfig = true;
        SetBackendConfigControlsEnabled(false);
        SetStatus(forceReload ? "正在刷新后端配置..." : "正在连接后端配置...", Brushes.Gray);
        DutyDiagnosticsLogger.Info("SettingsPage", "Starting async backend config load.",
            new
            {
                traceId,
                forceReload,
                revision
            });

        try
        {
            var backendConfig = await _backendModule.LoadAsync("host_settings", traceId);
            if (revision != _configLoadRevision)
            {
                DutyDiagnosticsLogger.Warn("SettingsPage", "Discarded stale backend config load result.",
                    new { traceId, revision, latestRevision = _configLoadRevision });
                return _backendConfigState == DutyBackendConfigLoadState.Loaded;
            }

            ApplyBackendConfig(backendConfig);
            _lastAppliedBackendConfig = _backendModule.CloneConfig(backendConfig);
            _backendConfigState = DutyBackendConfigLoadState.Loaded;
            _backendConfigErrorMessage = null;
            UpdateConfigTracking("已加载");
            SetStatus("后端配置已加载。", Brushes.Gray);
            stopwatch.Stop();
            DutyDiagnosticsLogger.Info("SettingsPage", "Backend config load succeeded.",
                new
                {
                    traceId,
                    revision,
                    durationMs = stopwatch.ElapsedMilliseconds,
                    baseUrl = backendConfig.BaseUrl,
                    model = backendConfig.Model,
                    modelProfile = backendConfig.ModelProfile,
                    orchestrationMode = backendConfig.OrchestrationMode,
                    multiAgentExecutionMode = backendConfig.MultiAgentExecutionMode,
                    apiKey = DutyDiagnosticsLogger.MaskSecret(backendConfig.ApiKey)
                });
            return true;
        }
        catch (Exception ex)
        {
            if (revision != _configLoadRevision)
            {
                DutyDiagnosticsLogger.Warn("SettingsPage", "Discarded stale backend config load failure.",
                    new { traceId, revision, latestRevision = _configLoadRevision });
                return _backendConfigState == DutyBackendConfigLoadState.Loaded;
            }

            _backendConfigState = DutyBackendConfigLoadState.LoadFailed;
            _backendConfigErrorMessage = ex.Message;
            _lastAppliedBackendConfig = null;
            UpdateConfigTracking("后端不可用");
            SetStatus($"后端配置不可用：{ex.Message}", Brushes.Orange);
            stopwatch.Stop();
            DutyDiagnosticsLogger.Error("SettingsPage", "Backend config load failed.", ex,
                new
                {
                    traceId,
                    revision,
                    durationMs = stopwatch.ElapsedMilliseconds
                });
            return false;
        }
        finally
        {
            if (revision == _configLoadRevision)
            {
                _isLoadingBackendConfig = false;
            }
        }
    }

    private void ApplyHostFormModel(DutyHostSettingsValues hostSettings)
    {
        _isLoadingConfig = true;
        try
        {
            SetAutoRunModeSelection(hostSettings.AutoRunMode);
            SetAutoRunParameterSelection(hostSettings.AutoRunMode, hostSettings.AutoRunParameter);
            SetAutoRunTimeSelection(hostSettings.AutoRunTime);
            AutoRunTriggerNotificationSwitch.IsChecked = hostSettings.AutoRunTriggerNotificationEnabled;
            DutyReminderEnabledSwitch.IsChecked = hostSettings.DutyReminderEnabled;
            SetDutyReminderTimeSelection(hostSettings.DutyReminderTime);
            EnableMcpSwitch.IsChecked = hostSettings.EnableMcp;
            EnableWebDebugLayerSwitch.IsChecked = hostSettings.EnableWebViewDebugLayer;
            SetComponentRefreshTimeSelection(hostSettings.ComponentRefreshTime);
            NotificationDurationSlider.Value = hostSettings.NotificationDurationSeconds;
            NotificationDurationLabel.Text = $"{hostSettings.NotificationDurationSeconds} 秒";
        }
        finally
        {
            _isLoadingConfig = false;
        }
    }

    private void ApplyBackendConfig(DutyBackendConfig backendConfig)
    {
        _isLoadingConfig = true;
        try
        {
            _planPresetDrafts = _backendModule.NormalizePlanPresets(backendConfig.PlanPresets, backendConfig);
            _currentPlanId = _backendModule.NormalizeSelectedPlanId(backendConfig.SelectedPlanId, _planPresetDrafts, backendConfig);
            RefreshPlanSelectors();
            ApplyCurrentPlanToControls();
            UpdateExecutionModeVisibility();
            UpdatePlanModeHint();
            DutyRuleBox.Text = backendConfig.DutyRule;
            SetBackendConfigControlsEnabled(true);
        }
        finally
        {
            _isLoadingConfig = false;
        }
    }

    private void SetBackendConfigControlsEnabled(bool enabled)
    {
        CurrentPlanComboBox.IsEnabled = enabled;
        CreatePlanBtn.IsEnabled = enabled;
        DuplicatePlanBtn.IsEnabled = enabled;
        DeletePlanBtn.IsEnabled = enabled;
        PlanNameBox.IsEnabled = enabled;
        PlanModeComboBox.IsEnabled = enabled;
        ApiKeyBox.IsEnabled = enabled;
        BaseUrlBox.IsEnabled = enabled;
        ModelBox.IsEnabled = enabled;
        ModelProfileComboBox.IsEnabled = enabled;
        MultiAgentExecutionModeComboBox.IsEnabled = enabled;
        DutyRuleBox.IsEnabled = enabled;
        RunAgentBtn.IsEnabled = enabled;
        UpdatePlanActionButtons();
    }

    private async Task LoadDataAsync(string reason = "数据刷新")
    {
        var revision = Interlocked.Increment(ref _dataLoadRevision);
        var traceId = DutyDiagnosticsLogger.CreateTraceId("data-load");
        var stopwatch = Stopwatch.StartNew();
        DutyDiagnosticsLogger.Info("SettingsPage", "Starting preview data load.",
            new { traceId, reason, revision });
        try
        {
            var data = await Task.Run(() =>
            {
                var state = Service.LoadState();
                var roster = Service.LoadRosterEntries();
                var rosterPreview = _rosterModule.BuildPreview(roster, state, DateTime.Today);
                var schedulePreview = _scheduleModule.BuildPreview(state);
                return (rosterPreview, schedulePreview);
            });

            if (revision != _dataLoadRevision)
            {
                return;
            }

            ApplyRosterPreview(data.rosterPreview);
            ApplySchedulePreview(data.schedulePreview);
            UpdateStudentActionButtons();
            UpdateDataTracking(reason);
            stopwatch.Stop();
            DutyDiagnosticsLogger.Info("SettingsPage", "Preview data load completed.",
                new
                {
                    traceId,
                    reason,
                    revision,
                    durationMs = stopwatch.ElapsedMilliseconds,
                    rosterCount = data.rosterPreview.Rows.Count,
                    scheduleCount = data.schedulePreview.Rows.Count
                });
        }
        catch (Exception ex)
        {
            if (revision != _dataLoadRevision)
            {
                return;
            }

            UpdateDataTracking("刷新失败");
            SetStatus($"刷新数据失败：{ex.Message}", Brushes.Orange);
            stopwatch.Stop();
            DutyDiagnosticsLogger.Error("SettingsPage", "Preview data load failed.", ex,
                new { traceId, reason, revision, durationMs = stopwatch.ElapsedMilliseconds });
        }
    }

    private void UpdateStudentActionButtons()
    {
        if (RosterListBox.SelectedItem is DutyRosterRow selected)
        {
            ToggleStudentActiveBtn.IsEnabled = true;
            ToggleStudentActiveBtn.Content = selected.Active ? "停用选中" : "启用选中";
            return;
        }

        ToggleStudentActiveBtn.IsEnabled = false;
        ToggleStudentActiveBtn.Content = "启用/停用选中";
    }

    private void ApplyRosterPreview(DutyRosterPreview preview)
    {
        var previousSelectedId = (RosterListBox.SelectedItem as DutyRosterRow)?.Id;
        RosterListBox.ItemsSource = preview.Rows;
        if (previousSelectedId.HasValue)
        {
            RosterListBox.SelectedItem = preview.Rows.FirstOrDefault(x => x.Id == previousSelectedId.Value);
        }

        RosterSummaryText.Text = preview.Summary;
        RosterEmptyStatePanel.IsVisible = preview.Rows.Count == 0;
    }

    private void ApplySchedulePreview(DutySchedulePreview preview)
    {
        var previousSelectedDate = string.IsNullOrWhiteSpace(_pendingScheduleSelectionDate)
            ? (ScheduleListBox.SelectedItem as DutyScheduleRow)?.Date
            : _pendingScheduleSelectionDate;

        ScheduleListBox.ItemsSource = preview.Rows;
        if (!string.IsNullOrWhiteSpace(previousSelectedDate))
        {
            ScheduleListBox.SelectedItem = preview.Rows.LastOrDefault(x =>
                string.Equals(x.Date, previousSelectedDate, StringComparison.Ordinal));
        }
        _pendingScheduleSelectionDate = string.Empty;

        PopulateScheduleEditorFromSelection();
        ScheduleSummaryText.Text = preview.Summary;
        ScheduleEmptyStatePanel.IsVisible = preview.Rows.Count == 0;
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
        if (ScheduleListBox.SelectedItem is not DutyScheduleRow selected)
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
        _ = LoadDataAsync("手动编辑排班");
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
        _ = LoadDataAsync("手动新建排班");
        SetStatus("已新建值日安排。", Brushes.Green);
    }

    private void PopulateScheduleEditorFromSelection()
    {
        _isPopulatingEditor = true;
        try
        {
            if (ScheduleListBox.SelectedItem is not DutyScheduleRow selected)
            {
                SelectedScheduleMetaText.Text = "请先在上方列表选择一条排班记录。";
                ScheduleDateEditorBox.Text = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                ScheduleDayEditorComboBox.SelectedItem = ToChineseWeekday(DateTime.Today.DayOfWeek);
                ScheduleAssignmentsEditorBox.Text = string.Empty;
                ScheduleNoteEditorBox.Text = string.Empty;
                SaveScheduleEditBtn.IsEnabled = false;
                return;
            }

            var editorData = _scheduleModule.GetEditorData(selected.Date);
            if (!editorData.Exists)
            {
                SelectedScheduleMetaText.Text = "当前记录不存在，可能已被刷新。";
                ScheduleDateEditorBox.Text = string.Empty;
                ScheduleDayEditorComboBox.SelectedItem = null;
                ScheduleAssignmentsEditorBox.Text = string.Empty;
                ScheduleNoteEditorBox.Text = string.Empty;
                SaveScheduleEditBtn.IsEnabled = false;
                return;
            }

            ScheduleDateEditorBox.Text = editorData.Date;
            ScheduleDayEditorComboBox.SelectedItem = ResolveScheduleDaySelection(editorData.Day, editorData.Date);
            ScheduleAssignmentsEditorBox.Text = FormatAreaAssignmentsForEditor(editorData.AreaAssignments);
            ScheduleNoteEditorBox.Text = editorData.Note;
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

    // 保留：仍被 UI 排班编辑器（默认天数预填）特殊流程依赖
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

    private void OnNotificationDurationChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_isLoadingConfig) return;
        var val = (int)e.NewValue;
        NotificationDurationLabel.Text = $"{val} \u79d2";
        QueueConfigApply();
    }

    private void OnTestNotificationClicked(object? sender, RoutedEventArgs e)
    {
        var duration = (int)NotificationDurationSlider.Value;
        NotificationService.Publish(
            "\u6D4B\u8BD5\u901A\u77E5",
            "\u6559\u5BA4\uFF1A\u5F20\u4E09\u3001\u674E\u56DB\uFF1B\u6E05\u6D01\u533A\uFF1A\u738B\u4E94\u3001\u8D75\u516D",
            duration);
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

    private string GetSelectedModelProfile()
    {
        return ModelProfileComboBox.SelectedItem is ComboBoxItem { Tag: string tag } ? tag : "auto";
    }

    private void SetModelProfileSelection(string modelProfile)
    {
        var targetTag = (modelProfile ?? "auto").Trim();
        var item = ModelProfileComboBox.Items.Cast<ComboBoxItem>().FirstOrDefault(x => string.Equals(x.Tag as string, targetTag, StringComparison.OrdinalIgnoreCase));
        ModelProfileComboBox.SelectedItem = item ?? ModelProfileComboBox.Items.Cast<ComboBoxItem>().First();
    }

    private string GetSelectedPlanId()
    {
        return GetSelectedPlanOptionId();
    }

    private void SetSelectedPlanId(string selectedPlanId)
    {
        SetSelectedPlanOptionId(CurrentPlanComboBox, selectedPlanId);
    }

    private string GetSelectedPlanModeId()
    {
        return PlanModeComboBox.SelectedItem is ComboBoxItem { Tag: string tag }
            ? _backendModule.NormalizePlanModeId(tag)
            : DutyBackendModeIds.Standard;
    }

    private void SetPlanModeSelection(string modeId)
    {
        var targetTag = _backendModule.NormalizePlanModeId(modeId);
        var item = PlanModeComboBox.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(x => string.Equals(x.Tag as string, targetTag, StringComparison.OrdinalIgnoreCase));
        PlanModeComboBox.SelectedItem = item ?? PlanModeComboBox.Items.Cast<ComboBoxItem>().First();
    }

    private string GetSelectedMultiAgentExecutionMode()
    {
        return MultiAgentExecutionModeComboBox.SelectedItem is ComboBoxItem { Tag: string tag } ? tag : "auto";
    }

    private void SetMultiAgentExecutionModeSelection(string executionMode)
    {
        var targetTag = (executionMode ?? "auto").Trim();
        var item = MultiAgentExecutionModeComboBox.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(x => string.Equals(x.Tag as string, targetTag, StringComparison.OrdinalIgnoreCase));
        MultiAgentExecutionModeComboBox.SelectedItem = item ?? MultiAgentExecutionModeComboBox.Items.Cast<ComboBoxItem>().First();
    }

    private void RefreshPlanSelectors()
    {
        if (_planPresetDrafts.Count == 0)
        {
            _planPresetDrafts = _backendModule.NormalizePlanPresets([], _lastAppliedBackendConfig);
        }

        _currentPlanId = _backendModule.NormalizeSelectedPlanId(_currentPlanId, _planPresetDrafts, _lastAppliedBackendConfig);

        var previousLoadingState = _isLoadingConfig;
        _isLoadingConfig = true;
        try
        {
            var options = _planPresetDrafts
                .Select(plan => new PlanSelectOption(plan.Id, plan.Name))
                .ToList();

            CurrentPlanComboBox.ItemsSource = options;
            SetSelectedPlanOptionId(CurrentPlanComboBox, _currentPlanId);
            UpdatePlanActionButtons();
        }
        finally
        {
            _isLoadingConfig = previousLoadingState;
        }
    }

    private void UpdatePlanModeHint()
    {
        PlanModeHintText.Text = GetPlanModeHint(GetSelectedPlanModeId());
    }

    private void ApplyCurrentPlanToControls()
    {
        if (_planPresetDrafts.Count == 0)
        {
            return;
        }

        var plan = GetCurrentPlan();
        if (plan == null)
        {
            return;
        }

        var previousLoadingState = _isLoadingConfig;
        _isLoadingConfig = true;
        try
        {
            _currentPlanId = plan.Id;
            SetSelectedPlanOptionId(CurrentPlanComboBox, plan.Id);
            PlanNameBox.Text = plan.Name;
            SetPlanModeSelection(plan.ModeId);
            ApiKeyBox.Text = string.IsNullOrWhiteSpace(plan.ApiKey) ? string.Empty : "********";
            ModelBox.Text = plan.Model;
            BaseUrlBox.Text = plan.BaseUrl;
            SetModelProfileSelection(plan.ModelProfile);
            SetMultiAgentExecutionModeSelection(plan.MultiAgentExecutionMode);
        }
        finally
        {
            _isLoadingConfig = previousLoadingState;
        }

        UpdateExecutionModeVisibility();
        UpdatePlanModeHint();
        UpdatePlanActionButtons();
    }

    private void CaptureCurrentPlanDraft()
    {
        var plan = GetCurrentPlan();
        if (plan == null)
        {
            return;
        }

        plan.Name = (PlanNameBox.Text ?? string.Empty).Trim();
        plan.ModeId = GetSelectedPlanModeId();
        plan.ApiKey = DutyScheduleOrchestrator.ResolveApiKeyInput(ApiKeyBox.Text, plan.ApiKey);
        plan.Model = (ModelBox.Text ?? string.Empty).Trim();
        plan.BaseUrl = (BaseUrlBox.Text ?? string.Empty).Trim();
        plan.ModelProfile = GetSelectedModelProfile();
        plan.MultiAgentExecutionMode = string.Equals(plan.ModeId, DutyBackendModeIds.Campus6Agent, StringComparison.Ordinal)
            ? GetSelectedMultiAgentExecutionMode()
            : "auto";
    }

    private DutyPlanPreset? GetCurrentPlan()
    {
        if (_planPresetDrafts.Count == 0)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(_currentPlanId))
        {
            _currentPlanId = _planPresetDrafts[0].Id;
        }

        return _planPresetDrafts.FirstOrDefault(plan => string.Equals(plan.Id, _currentPlanId, StringComparison.Ordinal))
               ?? _planPresetDrafts[0];
    }

    private string GetSelectedPlanOptionId()
    {
        return CurrentPlanComboBox.SelectedItem is PlanSelectOption option
            ? option.Id
            : _backendModule.NormalizeSelectedPlanId(_currentPlanId, _planPresetDrafts, _lastAppliedBackendConfig);
    }

    private static void SetSelectedPlanOptionId(ComboBox comboBox, string planId)
    {
        if (comboBox.ItemsSource is not IEnumerable<PlanSelectOption> options)
        {
            comboBox.SelectedItem = null;
            return;
        }

        comboBox.SelectedItem = options.FirstOrDefault(option => string.Equals(option.Id, planId, StringComparison.Ordinal))
            ?? options.FirstOrDefault();
    }

    private void UpdatePlanActionButtons()
    {
        var hasPlan = _planPresetDrafts.Count > 0;
        DuplicatePlanBtn.IsEnabled = hasPlan && CurrentPlanComboBox.IsEnabled;
        DeletePlanBtn.IsEnabled = hasPlan && _planPresetDrafts.Count > 1 && CurrentPlanComboBox.IsEnabled;
    }

    private DutyPlanPreset CreateDraftPlan(bool copyFromCurrent)
    {
        var source = copyFromCurrent ? GetCurrentPlan() : null;
        var usedIds = _planPresetDrafts.Select(plan => plan.Id).ToHashSet(StringComparer.Ordinal);
        var idBase = source == null ? "plan" : source.Id;
        var nextId = idBase;
        var suffix = 2;
        while (!usedIds.Add(nextId))
        {
            nextId = $"{idBase}-{suffix}";
            suffix++;
        }

        var nextName = source == null
            ? $"方案预设 {_planPresetDrafts.Count + 1}"
            : $"{(string.IsNullOrWhiteSpace(source.Name) ? GetPlanModeDisplayName(source.ModeId) : source.Name)} 副本";

        var modeId = source?.ModeId ?? DutyBackendModeIds.Standard;
        return new DutyPlanPreset
        {
            Id = nextId,
            Name = nextName,
            ModeId = modeId,
            ApiKey = source?.ApiKey ?? string.Empty,
            BaseUrl = source?.BaseUrl ?? "https://integrate.api.nvidia.com/v1",
            Model = source?.Model ?? "moonshotai/kimi-k2-thinking",
            ModelProfile = source?.ModelProfile ?? "auto",
            ProviderHint = source?.ProviderHint ?? string.Empty,
            MultiAgentExecutionMode = string.Equals(modeId, DutyBackendModeIds.Campus6Agent, StringComparison.Ordinal)
                ? source?.MultiAgentExecutionMode ?? "auto"
                : "auto"
        };
    }

    private static string GetPlanModeDisplayName(string modeId)
    {
        return modeId switch
        {
            DutyBackendModeIds.Campus6Agent => "6Agent",
            DutyBackendModeIds.IncrementalSmall => "增量小模型",
            _ => "标准"
        };
    }

    private static string GetPlanModeHint(string modeId)
    {
        return modeId switch
        {
            DutyBackendModeIds.Campus6Agent => "使用 6Agent 执行链路，适合校园计算中心模型与更稳定的结构化排班。",
            DutyBackendModeIds.IncrementalSmall => "使用详细提示词的单次执行方案，适合推理稳定的小模型。",
            _ => "默认的单次执行方案，优先控制 token 消耗并保持响应速度。"
        };
    }

    private sealed record PlanSelectOption(string Id, string Name)
    {
        public override string ToString() => Name;
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

    private DutySettingsPageValues BuildSettingsPageValuesFromControls()
    {
        return new DutySettingsPageValues
        {
            Host = BuildHostSettingsValuesFromControls(),
            Backend = BuildBackendSettingsValuesFromControls()
        };
    }

    private DutyHostSettingsValues BuildHostSettingsValuesFromControls()
    {
        return new DutyHostSettingsValues
        {
            AutoRunMode = GetSelectedAutoRunMode(),
            AutoRunParameter = GetSelectedAutoRunParameter(),
            AutoRunTime = GetSelectedAutoRunTime(),
            ComponentRefreshTime = GetSelectedComponentRefreshTime(),
            AutoRunTriggerNotificationEnabled = AutoRunTriggerNotificationSwitch.IsChecked == true,
            DutyReminderEnabled = DutyReminderEnabledSwitch.IsChecked == true,
            DutyReminderTime = GetSelectedDutyReminderTime(),
            EnableMcp = EnableMcpSwitch.IsChecked == true,
            EnableWebViewDebugLayer = EnableWebDebugLayerSwitch.IsChecked == true,
            NotificationDurationSeconds = (int)NotificationDurationSlider.Value
        };
    }

    private DutyBackendSettingsValues BuildBackendSettingsValuesFromControls()
    {
        CaptureCurrentPlanDraft();
        return new DutyBackendSettingsValues
        {
            SelectedPlanId = GetSelectedPlanId(),
            PlanPresets = _backendModule.ClonePlanPresets(_planPresetDrafts),
            DutyRule = DutyRuleBox.Text
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
        StatusInfoBar.Message =
            $"{FormatTrackingSegment("配置状态", _lastConfigState, _lastConfigAt)}{Environment.NewLine}{FormatTrackingSegment("运行状态", _lastRunState, _lastRunAt)}｜{FormatTrackingSegment("数据状态", _lastDataState, _lastDataAt)}";
    }

    private static string FormatTrackingSegment(string label, string state, DateTime? timestamp)
    {
        var safeLabel = string.IsNullOrWhiteSpace(label) ? "状态" : label.Trim();
        var safeState = string.IsNullOrWhiteSpace(state) ? "未知" : state.Trim();
        var time = timestamp.HasValue ? timestamp.Value.ToString("HH:mm:ss", CultureInfo.InvariantCulture) : "--:--:--";
        return $"{safeLabel}：{safeState}（{time}）";
    }

    private static IBrush GetStatusBrush(DutySettingsSaveMessageLevel level)
    {
        return level switch
        {
            DutySettingsSaveMessageLevel.Warning => Brushes.Orange,
            DutySettingsSaveMessageLevel.Error => Brushes.Red,
            _ => Brushes.Gray
        };
    }

    private void SetStatus(string text, IBrush brush)
    {
        StatusInfoBar.Title = text;
        StatusInfoBar.Severity = GetStatusSeverity(brush);
    }

    private void SetDataView(bool showScheduleView)
    {
        if (DataViewTabControl is null)
        {
            return;
        }

        var targetIndex = showScheduleView ? 0 : 1;
        if (DataViewTabControl.SelectedIndex != targetIndex)
        {
            DataViewTabControl.SelectedIndex = targetIndex;
        }
    }

    private static InfoBarSeverity GetStatusSeverity(IBrush brush)
    {
        if (ReferenceEquals(brush, Brushes.Red))
        {
            return InfoBarSeverity.Error;
        }

        if (ReferenceEquals(brush, Brushes.Green))
        {
            return InfoBarSeverity.Success;
        }

        if (ReferenceEquals(brush, Brushes.Orange))
        {
            return InfoBarSeverity.Warning;
        }

        return InfoBarSeverity.Informational;
    }
}
