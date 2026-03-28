using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Helpers.UI;
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
    private IPythonIpcService PythonIpcService { get; } = IAppHost.GetService<IPythonIpcService>();
    private IDutySettingsRepository SettingsRepository { get; } = IAppHost.GetService<IDutySettingsRepository>();
    private DutyBackendSettingsSyncService BackendSettingsSyncService { get; } = IAppHost.GetService<DutyBackendSettingsSyncService>();
    private DutyPluginPaths PluginPaths { get; } = IAppHost.GetService<DutyPluginPaths>();
    private DutySettingsTraceService SettingsTrace { get; } = IAppHost.GetService<DutySettingsTraceService>();
    private readonly DutyMainSettingsHostModule _hostModule;
    private readonly DutyMainSettingsBackendModule _backendModule;
    private readonly DutyMainSettingsSaveCoordinator _saveCoordinator;
    private readonly DutyMainSettingsRosterModule _rosterModule;
    private readonly DutyMainSettingsScheduleModule _scheduleModule;
    private readonly DispatcherTimer _reasoningStreamFlushTimer;
    private readonly object _reasoningStreamLock = new();
    private readonly StringBuilder _pendingReasoningStreamText = new();
    private ComboBox? _scheduleRecordModeComboBox;
    private bool _isLoadingConfig;
    private bool _isApplyingConfig;
    private string _lastLocalConfigState = "未应用";
    private string _lastConfigState = "未应用";
    private DateTime? _lastConfigAt;
    private string _lastRunState = "待命";
    private DateTime? _lastRunAt;
    private string _lastDataState = "未刷新";
    private DateTime? _lastDataAt;
    private readonly DispatcherTimer _configApplyDebounceTimer;
    private bool _hasPendingConfigApply;
    private string _pendingScheduleSelectionDate = string.Empty;
    private string _pendingReasoningStreamStatus = string.Empty;
    private int _pendingReasoningStreamRevision;
    private bool _isPopulatingEditor;
    private DutyBackendConfigLoadState _backendConfigState = DutyBackendConfigLoadState.NotLoaded;
    private bool _isLoadingBackendConfig;
    private string? _backendConfigErrorMessage;
    private int _configLoadRevision;
    private int _dataLoadRevision;
    private int _runSessionRevision;
    private int _activeRunSessionRevision;
    private int _configEditRevision;
    private DutyBackendSyncStatusSnapshot _backendSyncStatus = new();
    private DutySettingsDocument? _lastLoadedSettingsDocument;
    private DutyHostSettingsValues _lastAppliedHostSettings = new();
    private DutyAccessSecurityValues _lastAppliedAccessSecurity = new();
    private DutyBackendConfig? _lastAppliedBackendConfig;
    private List<DutyPlanPreset> _planPresetDrafts = [];
    private string _currentPlanId = string.Empty;
    private bool _isRosterDropActive;
    private string _scheduleEditorSourceDate = string.Empty;
    private bool _isScheduleEditorDirty;
    private bool _settingsDiagnosticsExported;
    private bool _isApplyingAccessSecurity;
    private int _configEventSuspendDepth;
    private bool _backendSyncStatusSubscribed;
    private readonly string _pageInstanceId = Guid.NewGuid().ToString("N");

    public DutyMainSettingsPage()
    {
        InitializeComponent();
        ConfigureTimingEditors();
        _hostModule = new DutyMainSettingsHostModule();
        _backendModule = new DutyMainSettingsBackendModule();
        _saveCoordinator = new DutyMainSettingsSaveCoordinator(SettingsRepository, BackendSettingsSyncService, _hostModule, _backendModule, SettingsTrace);
        _rosterModule = new DutyMainSettingsRosterModule(Service);
        _scheduleModule = new DutyMainSettingsScheduleModule(Service);
        _configApplyDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _configApplyDebounceTimer.Tick += OnConfigApplyDebounceTick;
        _reasoningStreamFlushTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _reasoningStreamFlushTimer.Tick += OnReasoningStreamFlushTick;
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
        PluginDataPathText.Text = PluginPaths.DataDirectory;
        MultiAgentExecutionModeItem.Content = "Agents 执行顺序";
        MultiAgentExecutionModeItem.Description = "仅对 Agents 方案生效。";
        _backendSyncStatus = BackendSettingsSyncService.GetStatusSnapshot();
        ExecuteWithoutConfigEvents(() =>
        {
            InitializeAutoRunTimeOptions();
            InitializeAutoRunModeOptions();
            InitializeAccessTokenModeOptions();
            InitializeServerPortModeOptions();
            InitializeComponentRefreshTimeOptions();
            InitializeScheduleDayOptions();
            InitializeScheduleRecordModeOptions();
            UpdateExecutionModeVisibility();
            SetDataView(showScheduleView: true);
            SetSettingsControlsEnabled(false);
            ClearPendingConfigApply("constructor_setup");
        });
        UpdateConfigTracking("正在加载设置");
        SetStatus("正在加载本地设置...", Brushes.Gray);
        TraceSettings("page_ctor");
        TryLoadInitialSettings();
        UpdateRunTracking("待命");
        _ = LoadDataAsync("页面初始化");
    }

    private void ConfigureTimingEditors()
    {
        ConfigureRotationTimeEditor();
        ConfigureDutyReminderEditor();
    }

    private void ConfigureRotationTimeEditor()
    {
        if (FindSettingsExpanderItem(ComponentRefreshHourComboBox) is { } item)
        {
            item.Content = "值日轮换时间";
            item.Description = "到达该时间后，首页组件切换显示下一天的值日安排。";
        }
    }

    private void ConfigureDutyReminderEditor()
    {
        DutyReminderTimesBox.IsVisible = true;
        DutyReminderHourComboBox.IsVisible = false;
        DutyReminderMinuteComboBox.IsVisible = false;
        if (DutyReminderHourComboBox.Parent is Control hourField)
        {
            hourField.IsVisible = false;
        }

        if (DutyReminderMinuteComboBox.Parent is Control minuteField)
        {
            minuteField.IsVisible = false;
        }

        if (FindSettingsExpanderItem(DutyReminderTimesBox) is { } item)
        {
            item.Content = "提醒时间";
            item.Description = "支持输入多个提醒时间，使用逗号、分号或换行分隔。";
        }
    }

    private static SettingsExpanderItem? FindSettingsExpanderItem(Control control)
    {
        Control? current = control;
        while (current != null)
        {
            if (current is SettingsExpanderItem item)
            {
                return item;
            }

            current = current.Parent as Control;
        }

        return null;
    }

    private void OnPageLoaded(object? sender, RoutedEventArgs e)
    {
        ConfigureTimingEditors();
        EnsureBackendSyncSubscription();
        Service.ScheduleUpdated += OnScheduleUpdated;
        DutyDiagnosticsLogger.Info("SettingsPage", "Settings page loaded; beginning unified settings load.");
        TraceSettings("page_loaded");
        _ = WarnIfInitialSettingsStillUnavailableAsync();
        if (_backendConfigState != DutyBackendConfigLoadState.Loaded)
        {
            QueueSettingsLoad("page_loaded", forceReload: _backendConfigState == DutyBackendConfigLoadState.LoadFailed);
        }
    }

    private void QueueSettingsLoad(string reason, bool forceReload = false)
    {
        TraceSettings("queue_settings_load", new
        {
            reason,
            force_reload = forceReload
        });
        Dispatcher.UIThread.Post(
            () => { _ = EnsureSettingsLoadedAsync(reason, forceReload); },
            DispatcherPriority.Background);
    }

    private async Task EnsureSettingsLoadedAsync(string reason, bool forceReload = false)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            if (_backendConfigState == DutyBackendConfigLoadState.Loaded && !forceReload)
            {
                return;
            }

            if (_isLoadingBackendConfig)
            {
                DutyDiagnosticsLogger.Info("SettingsPage", "Deferred local settings load because another load is active.",
                    new
                    {
                        reason,
                        attempt,
                        state = _backendConfigState.ToString()
                    });
                await Task.Delay(150);
                continue;
            }

            var shouldForceReload = forceReload || attempt > 1;
            var loaded = await LoadSettingsAsync(shouldForceReload);
            if (loaded || _backendConfigState == DutyBackendConfigLoadState.Loaded)
            {
                return;
            }

            if (attempt < 3)
            {
                DutyDiagnosticsLogger.Warn("SettingsPage", "Retrying local settings load after unsuccessful attempt.",
                    new
                    {
                        reason,
                        attempt,
                        state = _backendConfigState.ToString()
                    });
                await Task.Delay(150);
            }
        }
    }

    private void TryLoadInitialSettings()
    {
        if (_backendConfigState == DutyBackendConfigLoadState.Loaded || _isLoadingBackendConfig)
        {
            TraceSettings("initial_settings_load_skipped", new
            {
                state = _backendConfigState.ToString(),
                is_loading_backend = _isLoadingBackendConfig
            });
            return;
        }

        var traceId = DutyDiagnosticsLogger.CreateTraceId("settings-load");
        var stopwatch = Stopwatch.StartNew();
        _isLoadingConfig = true;
        _isLoadingBackendConfig = true;
        DutyDiagnosticsLogger.Info("SettingsPage", "Starting initial local settings load.",
            new { traceId });
        TraceSettings("initial_settings_load_started", new { trace_id = traceId });

        try
        {
            var settingsDocument = SettingsRepository.LoadSettingsDocument();
            ApplyLoadedSettingsDocument(settingsDocument, showStatusMessage: true);
            stopwatch.Stop();
            DutyDiagnosticsLogger.Info("SettingsPage", "Initial local settings load succeeded.",
                new
                {
                    traceId,
                    durationMs = stopwatch.ElapsedMilliseconds,
                    hostVersion = settingsDocument.HostVersion,
                    backendVersion = settingsDocument.BackendVersion
                });
            TraceSettings("initial_settings_load_completed", new
            {
                trace_id = traceId,
                duration_ms = stopwatch.ElapsedMilliseconds,
                host_version = settingsDocument.HostVersion,
                backend_version = settingsDocument.BackendVersion,
                selected_plan_id = settingsDocument.Backend?.SelectedPlanId
            });
        }
        catch (Exception ex)
        {
            _backendConfigState = DutyBackendConfigLoadState.LoadFailed;
            _backendConfigErrorMessage = ex.Message;
            _lastLoadedSettingsDocument = null;
            _lastAppliedBackendConfig = null;
            UpdateConfigTracking("本地设置不可用");
            SetStatus($"本地设置不可用：{ex.Message}", Brushes.Orange);
            stopwatch.Stop();
            DutyDiagnosticsLogger.Error("SettingsPage", "Initial local settings load failed.", ex,
                new
                {
                    traceId,
                    durationMs = stopwatch.ElapsedMilliseconds
                });
            TraceSettings("initial_settings_load_failed", new
            {
                trace_id = traceId,
                duration_ms = stopwatch.ElapsedMilliseconds,
                error = ex.Message
            }, "ERROR");
            ExportSettingsDiagnosticsOnce("initial_settings_load_failed");
        }
        finally
        {
            _isLoadingBackendConfig = false;
            _isLoadingConfig = false;
            SetSettingsControlsEnabled(_backendConfigState == DutyBackendConfigLoadState.Loaded);
        }
    }

    private void OnPageUnloaded(object? sender, RoutedEventArgs e)
    {
        Service.ScheduleUpdated -= OnScheduleUpdated;
        ReleaseBackendSyncSubscription();
        Interlocked.Increment(ref _configLoadRevision);
        _configApplyDebounceTimer.Stop();
        StopReasoningStreamFlush();
        TraceSettings("page_unloaded");
        if (_isLoadingConfig)
        {
            SettingsTrace.Invariant("page_unloaded_while_loading", "Settings page unloaded while local settings were still loading.", BuildSettingsTraceSnapshot());
            return;
        }

        CaptureCurrentPlanDraft();
        QueueConfigApply(immediate: true);
    }

    private void OnScheduleUpdated(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _ = LoadDataAsync("后台自动排班更新");
        }, DispatcherPriority.Background);
    }

    private void EnsureBackendSyncSubscription()
    {
        if (_backendSyncStatusSubscribed)
        {
            return;
        }

        BackendSettingsSyncService.StatusChanged += OnBackendSyncStatusChanged;
        _backendSyncStatusSubscribed = true;
    }

    private void ReleaseBackendSyncSubscription()
    {
        if (!_backendSyncStatusSubscribed)
        {
            return;
        }

        BackendSettingsSyncService.StatusChanged -= OnBackendSyncStatusChanged;
        _backendSyncStatusSubscribed = false;
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

    private void InitializeAccessTokenModeOptions()
    {
        AccessTokenModeComboBox.SelectedIndex = 0;
        StaticAccessTokenBox.Text = string.Empty;
        AccessTokenStatusText.Text = "访问鉴权尚未加载。";
    }

    private void InitializeServerPortModeOptions()
    {
        ServerPortModeComboBox.SelectedIndex = 0;
        FixedServerPortBox.Text = string.Empty;
        FixedServerPortItem.IsVisible = false;
        McpEndpointStatusText.Text = "MCP 状态尚未加载。";
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
        var isMultiAgent = string.Equals(GetSelectedPlanModeId(), DutyBackendModeIds.Agents, StringComparison.Ordinal);
        MultiAgentExecutionModeItem.IsVisible = isMultiAgent;
    }

    private void InitializeComponentRefreshTimeOptions()
    {
        ComponentRefreshHourComboBox.ItemsSource = Enumerable.Range(0, 24).Select(x => x.ToString("D2")).ToList();
        ComponentRefreshMinuteComboBox.ItemsSource = Enumerable.Range(0, 60).Select(x => x.ToString("D2")).ToList();
        ComponentRefreshHourComboBox.SelectedItem = "08";
        ComponentRefreshMinuteComboBox.SelectedItem = "00";
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

    private void InitializeScheduleRecordModeOptions()
    {
        if (CreateScheduleEditBtn?.Parent is not Panel parent)
        {
            return;
        }

        if (_scheduleRecordModeComboBox != null)
        {
            return;
        }

        _scheduleRecordModeComboBox = new ComboBox
        {
            Width = 190,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
        };
        _scheduleRecordModeComboBox.ItemsSource = new List<ComboBoxItem>
        {
            new() { Content = "变更并进行记录", Tag = "record" },
            new() { Content = "变更但不进行记录", Tag = "skip" }
        };
        _scheduleRecordModeComboBox.SelectedIndex = 0;

        var insertIndex = Math.Max(0, parent.Children.IndexOf(CreateScheduleEditBtn));
        parent.Children.Insert(insertIndex, _scheduleRecordModeComboBox);
    }

    private async void OnRunAgentClick(object? sender, RoutedEventArgs e)
    {
        var runSessionRevision = Interlocked.Increment(ref _runSessionRevision);
        _activeRunSessionRevision = runSessionRevision;
        ResetReasoningStreamBuffer(runSessionRevision);
        var instruction = (InstructionBox.Text ?? string.Empty).Trim();
        var useDefaultInstruction = instruction.Length == 0;

        _configApplyDebounceTimer.Stop();
        await FlushPendingConfigApplyAsync();

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
                progress: progress =>
                {
                    var phase = (progress.Phase ?? string.Empty).Trim().ToLowerInvariant();
                    if (phase.Length == 0)
                    {
                        return;
                    }

                    if (phase == "stream_chunk")
                    {
                        QueueReasoningStreamChunk(runSessionRevision, progress.StreamChunk, "AI 正在流式返回中...");
                        return;
                    }

                    if (phase is "stream_fallback" or "parse_retry" or "agent_fail" or "agent_fallback")
                    {
                        var detail = (progress.Message ?? string.Empty).Trim();
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (_activeRunSessionRevision != runSessionRevision)
                            {
                                return;
                            }

                            if (detail.Length > 0)
                            {
                                if (!string.IsNullOrWhiteSpace(ReasoningBoardText.Text))
                                {
                                    ReasoningBoardText.Text += Environment.NewLine;
                                }
                                ReasoningBoardText.Text += $"[系统] {detail}";
                                ReasoningBoardScrollViewer.ScrollToEnd();
                            }

                            SetStatus(detail.Length > 0 ? detail : "执行状态更新。", Brushes.Orange);
                        });
                        return;
                    }

                    var message = (progress.Message ?? string.Empty).Trim();
                    if (message.Length == 0)
                    {
                        return;
                    }

                      Dispatcher.UIThread.Post(() =>
                      {
                          if (_activeRunSessionRevision != runSessionRevision)
                          {
                              return;
                          }
                          SetStatus(message, Brushes.Gray);
                      });
                  });
              FlushReasoningStreamBuffer(stopTimerIfIdle: true);
              _activeRunSessionRevision = 0;
              if (result.Success)
              {
                  if (string.IsNullOrWhiteSpace(ReasoningBoardText.Text) && !string.IsNullOrWhiteSpace(result.AiResponse))
                  {
                      ReasoningBoardText.Text = result.AiResponse;
                      ReasoningBoardScrollViewer.ScrollToEnd();
                  }
                  ReasoningBoardContainer.BorderBrush = Brushes.Green;
                  UpdateRunTracking("执行成功");
                  SetStatus(
                      useDefaultInstruction
                          ? "默认排班执行成功，正在刷新结果视图..."
                          : "排班执行成功，正在刷新结果视图...",
                      Brushes.Green);
                  Dispatcher.UIThread.Post(
                      () => { _ = LoadDataAsync("排班完成"); },
                      DispatcherPriority.Background);
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
              FlushReasoningStreamBuffer(stopTimerIfIdle: true);
              _activeRunSessionRevision = 0;
              ReasoningBoardContainer.BorderBrush = Brushes.Red;
              UpdateRunTracking("执行异常");
              SetStatus($"执行失败：{ex.Message}", Brushes.Red);
          }
          finally
          {
              StopReasoningStreamFlush();
              _activeRunSessionRevision = 0;
              RunAgentBtn.IsEnabled = true;
          }
      }

    private void OnConfigInputLostFocus(object? sender, RoutedEventArgs e)
    {
        if (ShouldIgnoreConfigEvent())
        {
            return;
        }

        CaptureCurrentPlanDraft();
        RefreshPlanSelectorsIfNeeded(sender);
        TraceSettings("control_changed", new { control = GetControlName(sender), reason = "lost_focus" });
        QueueConfigApply();
    }

    private void OnConfigInputTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (ShouldIgnoreConfigEvent())
        {
            return;
        }

        if (sender == FixedServerPortBox)
        {
            UpdateMcpEndpointControlsState(_backendConfigState == DutyBackendConfigLoadState.Loaded);
            UpdateMcpEndpointStatus();
        }

        CaptureCurrentPlanDraft();
        RefreshPlanSelectorsIfNeeded(sender);
        TraceSettings("control_changed", new { control = GetControlName(sender), reason = "text_changed" });
        QueueConfigApply();
    }

    private void OnConfigInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        if (ShouldIgnoreConfigEvent())
        {
            return;
        }

        e.Handled = true;
        CaptureCurrentPlanDraft();
        RefreshPlanSelectorsIfNeeded(sender);
        TraceSettings("control_changed", new { control = GetControlName(sender), reason = "enter_key" });
        QueueConfigApply(immediate: true);
    }

    private void OnConfigSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ShouldIgnoreConfigEvent())
        {
            return;
        }

        if (sender == AutoRunModeComboBox)
        {
            UpdateAutoRunSecondaryVisibility();
        }
        else if (sender == ServerPortModeComboBox)
        {
            UpdateMcpEndpointControlsState(_backendConfigState == DutyBackendConfigLoadState.Loaded);
            UpdateMcpEndpointStatus();
        }
        else if (sender == PlanModeComboBox)
        {
            UpdateExecutionModeVisibility();
            UpdatePlanModeHint();
        }

        CaptureCurrentPlanDraft();
        TraceSettings("control_changed", new { control = GetControlName(sender), reason = "selection_changed" });
        QueueConfigApply();
    }

    private void OnCurrentPlanSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ShouldIgnoreConfigEvent())
        {
            return;
        }

        CaptureCurrentPlanDraft();
        var previousPlanId = _currentPlanId;
        _currentPlanId = GetSelectedPlanOptionId();
        TraceSettings("plan_selection_changed", new
        {
            previous_plan_id = previousPlanId,
            next_plan_id = _currentPlanId
        });
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
        if (ShouldIgnoreConfigEvent())
        {
            return;
        }

        if (sender == EnableMcpSwitch)
        {
            UpdateMcpEndpointControlsState(_backendConfigState == DutyBackendConfigLoadState.Loaded);
            UpdateMcpEndpointStatus();
        }

        QueueConfigApply();
    }

    private void OnAccessTokenModeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateAccessSecurityControlsState(_backendConfigState == DutyBackendConfigLoadState.Loaded);
        UpdateAccessSecurityStatus();
        UpdateMcpEndpointControlsState(_backendConfigState == DutyBackendConfigLoadState.Loaded);
        UpdateMcpEndpointStatus();
    }

    private async void OnApplyAccessSecurityClick(object? sender, RoutedEventArgs e)
    {
        await ApplyAccessSecurityAsync(clearStaticToken: false);
    }

    private async void OnClearStaticAccessTokenClick(object? sender, RoutedEventArgs e)
    {
        await ApplyAccessSecurityAsync(clearStaticToken: true);
    }

    private async void OnCopyAccessTokenClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var token = PythonIpcService.GetCurrentAccessToken();
            if (string.IsNullOrWhiteSpace(token))
            {
                var runtimeStatus = PythonIpcService.GetAccessTokenStatus();
                var message = runtimeStatus.ConfiguredMode == DutyAccessTokenModes.Static
                    ? "静态 token 当前不可复制，请检查本地配置是否完整。"
                    : "动态 token 仅在后端已启动后可复制。";
                SetStatus(message, Brushes.Orange);
                UpdateAccessSecurityStatus();
                return;
            }

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.Clipboard == null)
            {
                throw new InvalidOperationException("Clipboard is unavailable.");
            }

            await topLevel.Clipboard.SetTextAsync(token);
            SetStatus("当前 token 已复制。", Brushes.Green);
            UpdateAccessSecurityStatus();
        }
        catch (Exception ex)
        {
            SetStatus($"复制 token 失败：{ex.Message}", Brushes.Red);
        }
    }

    private async void OnCopyMcpUrlClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (!TryGetCopyableMcpEndpoint(out var mcpUrl, out _, out var errorMessage))
            {
                SetStatus(errorMessage, Brushes.Orange);
                UpdateMcpEndpointStatus();
                return;
            }

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.Clipboard == null)
            {
                throw new InvalidOperationException("Clipboard is unavailable.");
            }

            await topLevel.Clipboard.SetTextAsync(mcpUrl);
            SetStatus("当前 MCP URL 已复制。", Brushes.Green);
            UpdateMcpEndpointStatus();
        }
        catch (Exception ex)
        {
            SetStatus($"复制 MCP URL 失败：{ex.Message}", Brushes.Red);
        }
    }

    private async void OnCopyMcpClientConfigClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (!TryGetCopyableMcpEndpoint(out var mcpUrl, out var token, out var errorMessage))
            {
                SetStatus(errorMessage, Brushes.Orange);
                UpdateMcpEndpointStatus();
                return;
            }

            var clientConfig = new
            {
                mcpServers = new Dictionary<string, object?>
                {
                    ["duty-agent"] = new
                    {
                        type = "streamableHttp",
                        url = mcpUrl,
                        headers = new Dictionary<string, string>
                        {
                            ["Authorization"] = $"Bearer {token}"
                        }
                    }
                }
            };

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.Clipboard == null)
            {
                throw new InvalidOperationException("Clipboard is unavailable.");
            }

            await topLevel.Clipboard.SetTextAsync(JsonSerializer.Serialize(clientConfig, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
            SetStatus("MCP 客户端配置已复制。", Brushes.Green);
            UpdateMcpEndpointStatus();
        }
        catch (Exception ex)
        {
            SetStatus($"复制 MCP 客户端配置失败：{ex.Message}", Brushes.Red);
        }
    }

    private async Task<bool> ApplyAccessSecurityAsync(bool clearStaticToken)
    {
        if (_isLoadingConfig || _isApplyingConfig || _isApplyingAccessSecurity)
        {
            return false;
        }

        try
        {
            _isApplyingAccessSecurity = true;
            UpdateAccessSecurityControlsState(_backendConfigState == DutyBackendConfigLoadState.Loaded);
            await FlushPendingConfigApplyAsync();

            var accessTokenMode = GetSelectedAccessTokenMode();
            var newStaticAccessToken = (StaticAccessTokenBox.Text ?? string.Empty).Trim();
            if (!clearStaticToken &&
                string.Equals(accessTokenMode, DutyAccessTokenModes.Dynamic, StringComparison.Ordinal) &&
                newStaticAccessToken.Length > 0)
            {
                SetStatus("动态模式下不会隐式保存静态 token。请切换到静态模式后再应用。", Brushes.Orange);
                return false;
            }

            TraceSettings("access_security_save_started", new
            {
                clear_static_token = clearStaticToken,
                access_token_mode = accessTokenMode,
                static_access_token_configured = _lastAppliedAccessSecurity.StaticAccessTokenConfigured
            });
            var result = SettingsRepository.SaveHostAccessSecurity(new DutyHostAccessSecuritySaveRequest
            {
                AccessTokenMode = accessTokenMode,
                NewStaticAccessTokenPlaintext = clearStaticToken || newStaticAccessToken.Length == 0 ? null : newStaticAccessToken,
                ClearStaticAccessToken = clearStaticToken,
                StaticAccessTokenConfigured = _lastAppliedAccessSecurity.StaticAccessTokenConfigured
            });

            ApplyLoadedSettingsDocument(result.Document, showStatusMessage: false);
            UpdateConfigTracking(
                result.NoChanges
                    ? "访问鉴权未变更"
                    : result.RestartRequired
                    ? "本地已保存（需重启）"
                    : "本地已保存");
            SetStatus(result.Message, Brushes.Gray);
            if (result.RestartRequired)
            {
                RequestRestart();
            }

            TraceSettings("access_security_save_completed", new
            {
                clear_static_token = clearStaticToken,
                access_token_mode = accessTokenMode,
                restart_required = result.RestartRequired,
                no_changes = result.NoChanges
            });
            return true;
        }
        catch (InvalidOperationException ex)
        {
            TraceSettings("access_security_save_failed", new { error = ex.Message }, "WARN");
            SetStatus(ex.Message, Brushes.Orange);
            return false;
        }
        catch (ArgumentException ex)
        {
            TraceSettings("access_security_save_failed", new { error = ex.Message }, "WARN");
            SetStatus(ex.Message, Brushes.Orange);
            return false;
        }
        catch (Exception ex)
        {
            TraceSettings("access_security_save_failed", new { error = ex.Message }, "ERROR");
            SetStatus($"访问鉴权保存失败：{ex.Message}", Brushes.Red);
            return false;
        }
        finally
        {
            _isApplyingAccessSecurity = false;
            UpdateAccessSecurityStatus();
            UpdateAccessSecurityControlsState(_backendConfigState == DutyBackendConfigLoadState.Loaded);
            UpdateMcpEndpointControlsState(_backendConfigState == DutyBackendConfigLoadState.Loaded);
            UpdateMcpEndpointStatus();
        }
    }

    private void QueueConfigApply(bool immediate = false)
    {
        if (_isLoadingConfig)
        {
            SettingsTrace.Invariant("save_requested_while_loading", "Ignored settings save request because local settings are still loading.", BuildSettingsTraceSnapshot(new { immediate }));
            return;
        }

        Interlocked.Increment(ref _configEditRevision);
        _hasPendingConfigApply = true;
        _configApplyDebounceTimer.Stop();
        TraceSettings("queue_save_requested", new
        {
            immediate,
            edit_revision = _configEditRevision
        });
        if (_isApplyingConfig)
        {
            return;
        }

        if (immediate)
        {
            TraceSettings("save_immediate_triggered", new { edit_revision = _configEditRevision });
            _ = FlushPendingConfigApplyAsync();
            return;
        }

        TraceSettings("debounce_started", new { edit_revision = _configEditRevision });
        _configApplyDebounceTimer.Start();
    }

    private void OnConfigApplyDebounceTick(object? sender, EventArgs e)
    {
        _configApplyDebounceTimer.Stop();
        TraceSettings("debounce_fired", new { edit_revision = _configEditRevision });
        _ = FlushPendingConfigApplyAsync();
    }

    private void OnReasoningStreamFlushTick(object? sender, EventArgs e)
    {
        FlushReasoningStreamBuffer(stopTimerIfIdle: true);
    }

    private void ResetReasoningStreamBuffer(int runSessionRevision)
    {
        StopReasoningStreamFlush();
        lock (_reasoningStreamLock)
        {
            _pendingReasoningStreamRevision = runSessionRevision;
        }
    }

    private void QueueReasoningStreamChunk(int runSessionRevision, string? streamChunk, string? statusMessage)
    {
        lock (_reasoningStreamLock)
        {
            if (_pendingReasoningStreamRevision != runSessionRevision)
            {
                _pendingReasoningStreamText.Clear();
                _pendingReasoningStreamStatus = string.Empty;
                _pendingReasoningStreamRevision = runSessionRevision;
            }

            if (!string.IsNullOrEmpty(streamChunk))
            {
                _pendingReasoningStreamText.Append(streamChunk);
            }

            if (!string.IsNullOrWhiteSpace(statusMessage))
            {
                _pendingReasoningStreamStatus = statusMessage.Trim();
            }
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (!_reasoningStreamFlushTimer.IsEnabled)
            {
                _reasoningStreamFlushTimer.Start();
            }
        }, DispatcherPriority.Background);
    }

    private void FlushReasoningStreamBuffer(bool stopTimerIfIdle)
    {
        string chunk;
        string status;
        int revision;
        lock (_reasoningStreamLock)
        {
            chunk = _pendingReasoningStreamText.ToString();
            _pendingReasoningStreamText.Clear();
            status = _pendingReasoningStreamStatus;
            _pendingReasoningStreamStatus = string.Empty;
            revision = _pendingReasoningStreamRevision;
        }

        if (revision != 0 && revision == _activeRunSessionRevision)
        {
            if (!string.IsNullOrWhiteSpace(status))
            {
                SetStatus(status, Brushes.Gray);
            }

            if (chunk.Length > 0)
            {
                ReasoningBoardText.Text += chunk;
                ReasoningBoardScrollViewer.ScrollToEnd();
            }
        }

        if (!stopTimerIfIdle)
        {
            return;
        }

        lock (_reasoningStreamLock)
        {
            if (_pendingReasoningStreamText.Length == 0 &&
                string.IsNullOrWhiteSpace(_pendingReasoningStreamStatus) &&
                _reasoningStreamFlushTimer.IsEnabled)
            {
                _reasoningStreamFlushTimer.Stop();
            }
        }
    }

    private void StopReasoningStreamFlush()
    {
        if (_reasoningStreamFlushTimer.IsEnabled)
        {
            _reasoningStreamFlushTimer.Stop();
        }

        lock (_reasoningStreamLock)
        {
            _pendingReasoningStreamText.Clear();
            _pendingReasoningStreamStatus = string.Empty;
            _pendingReasoningStreamRevision = 0;
        }
    }

    private async Task FlushPendingConfigApplyAsync()
    {
        if (!_hasPendingConfigApply || _isApplyingConfig || _isLoadingConfig)
        {
            TraceSettings("save_flush_skipped", new
            {
                has_pending = _hasPendingConfigApply,
                is_applying = _isApplyingConfig,
                is_loading = _isLoadingConfig
            });
            return;
        }

        _hasPendingConfigApply = false;
        TraceSettings("save_started", new { edit_revision = _configEditRevision });
        await ApplyConfigFromControlsAsync();
    }

    private async Task<bool> ApplyConfigFromControlsAsync()
    {
        if (_isLoadingConfig || _isApplyingConfig)
        {
            TraceSettings("save_apply_skipped", new
            {
                is_loading = _isLoadingConfig,
                is_applying = _isApplyingConfig
            });
            return false;
        }

        try
        {
            _isApplyingConfig = true;
            var applyRevision = Volatile.Read(ref _configEditRevision);
            var pendingValues = BuildSettingsPageValuesFromControls();
            TraceSettings("save_apply_invoked", new
            {
                apply_revision = applyRevision,
                selected_plan_id = pendingValues.Backend.SelectedPlanId,
                plan_count = pendingValues.Backend.PlanPresets?.Count ?? 0
            });
            var outcome = await _saveCoordinator.ApplyAsync(new DutySettingsSaveContext
            {
                Current = pendingValues,
                LastAppliedHost = _lastAppliedHostSettings,
                LastAppliedBackend = _lastAppliedBackendConfig
            });

            if (outcome.NoChanges)
            {
                TraceSettings("save_apply_completed", new
                {
                    apply_revision = applyRevision,
                    success = true,
                    no_changes = true
                });
                return true;
            }

            if (outcome.AppliedDocument != null)
            {
                _lastLoadedSettingsDocument = outcome.AppliedDocument;
            }

            if (outcome.AppliedBackend != null)
            {
                _lastAppliedBackendConfig = _backendModule.CloneConfig(outcome.AppliedBackend);
                _backendConfigState = DutyBackendConfigLoadState.Loaded;
                _backendConfigErrorMessage = null;
            }

            if (outcome.AppliedHost != null)
            {
                _lastAppliedHostSettings = outcome.AppliedHost;
            }

            var hasNewerLocalEdits = Volatile.Read(ref _configEditRevision) != applyRevision;

            if (!outcome.Success)
            {
                TraceSettings("save_apply_completed", new
                {
                    apply_revision = applyRevision,
                    success = false,
                    no_changes = false,
                    has_newer_local_edits = hasNewerLocalEdits,
                    message = outcome.Message
                }, "ERROR");
                if (outcome.RestartRequired)
                {
                    RequestRestart();
                }

                UpdateConfigTracking(hasNewerLocalEdits ? "本地草稿待同步" : "应用失败");
                SetStatus(
                    hasNewerLocalEdits
                        ? "较早的设置已返回，最新更改仍在等待同步。"
                        : outcome.Message,
                    hasNewerLocalEdits ? Brushes.Orange : GetStatusBrush(outcome.MessageLevel));
                return false;
            }

            UpdateConfigTracking(
                hasNewerLocalEdits
                    ? "本地已保存，等待提交最新更改"
                    : outcome.RestartRequired
                    ? "本地已保存（需重启）"
                    : "本地已保存");

            if (outcome.RestartRequired)
            {
                RequestRestart();
            }

            TraceSettings("save_apply_completed", new
            {
                apply_revision = applyRevision,
                success = true,
                no_changes = false,
                has_newer_local_edits = hasNewerLocalEdits,
                restart_required = outcome.RestartRequired,
                selected_plan_id = outcome.AppliedDocument?.Backend?.SelectedPlanId
            });
            SetStatus(
                hasNewerLocalEdits
                    ? "较早的本地保存已完成，正在继续提交最新更改..."
                    : outcome.Message,
                hasNewerLocalEdits ? Brushes.Orange : GetStatusBrush(outcome.MessageLevel));
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

            await ImportRosterFromPathAsync(localPath, "名单导入");
        }
        catch (Exception ex)
        {
            SetStatus($"导入失败：{ex.Message}", Brushes.Red);
        }
    }

    private void OnRosterDropZoneDragOver(object? sender, DragEventArgs e)
    {
        var localPath = TryGetFirstDroppedRosterPath(e.Data, out _);
        var canDrop = !string.IsNullOrWhiteSpace(localPath);
        e.DragEffects = canDrop ? DragDropEffects.Copy : DragDropEffects.None;
        SetRosterDropActive(canDrop);
        e.Handled = true;
    }

    private void OnRosterDropZoneDragLeave(object? sender, RoutedEventArgs e)
    {
        SetRosterDropActive(false);
    }

    private async void OnRosterDropZoneDrop(object? sender, DragEventArgs e)
    {
        SetRosterDropActive(false);
        try
        {
            var localPath = TryGetFirstDroppedRosterPath(e.Data, out var supportedCount);
            if (string.IsNullOrWhiteSpace(localPath))
            {
                SetStatus("请拖入本地 .txt 或 .xlsx 名单文件。", Brushes.Orange);
                return;
            }

            var extraMessage = supportedCount > 1 ? " 其余文件已忽略。" : string.Empty;
            await ImportRosterFromPathAsync(localPath, "拖拽导入", extraMessage);
            e.Handled = true;
        }
        catch (Exception ex)
        {
            SetStatus($"拖拽导入失败：{ex.Message}", Brushes.Red);
        }
    }

    private void OnRosterSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateStudentActionButtons();
    }

    private async Task ImportRosterFromPathAsync(string localPath, string reason, string successSuffix = "")
    {
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

        await LoadDataAsync(reason);
        SetStatus($"{result.Message}{successSuffix}", Brushes.Green);
    }

    private static string? TryGetFirstDroppedRosterPath(IDataObject data, out int supportedCount)
    {
        supportedCount = 0;
        var items = data.GetFiles();
        if (items == null)
        {
            return null;
        }

        string? firstSupportedPath = null;
        foreach (var item in items)
        {
            var localPath = item.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(localPath) || !IsSupportedRosterImportPath(localPath))
            {
                continue;
            }

            supportedCount++;
            firstSupportedPath ??= localPath;
        }

        return firstSupportedPath;
    }

    private static bool IsSupportedRosterImportPath(string path)
    {
        var extension = (Path.GetExtension(path) ?? string.Empty).Trim();
        return extension.Equals(".txt", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase);
    }

    private void SetRosterDropActive(bool isActive)
    {
        if (_isRosterDropActive == isActive)
        {
            return;
        }

        _isRosterDropActive = isActive;
        RosterDropOverlay.IsVisible = isActive;
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
        TraceSettings("manual_refresh_requested");
        try
        {
            await Task.WhenAll(
                LoadDataAsync("手动刷新"),
                LoadSettingsAsync(forceReload: true));
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

    private async Task<bool> LoadSettingsAsync(bool forceReload = false)
    {
        if (_isLoadingBackendConfig)
        {
            DutyDiagnosticsLogger.Info("SettingsPage", "Skipped local settings load because another load is active.",
                new
                {
                    forceReload,
                    state = _backendConfigState.ToString()
                });
            TraceSettings("local_settings_load_skipped_busy", new
            {
                force_reload = forceReload,
                state = _backendConfigState.ToString()
            });
            return _backendConfigState == DutyBackendConfigLoadState.Loaded;
        }

        if (_backendConfigState == DutyBackendConfigLoadState.Loaded && !forceReload)
        {
            DutyDiagnosticsLogger.Info("SettingsPage", "Skipped local settings load because cached settings are already loaded.");
            TraceSettings("local_settings_load_skipped_cached");
            return true;
        }

        var revision = Interlocked.Increment(ref _configLoadRevision);
        var traceId = DutyDiagnosticsLogger.CreateTraceId("settings-load");
        var stopwatch = Stopwatch.StartNew();
        _isLoadingConfig = true;
        _isLoadingBackendConfig = true;
        DutyDiagnosticsLogger.Info("SettingsPage", "Starting local settings load.",
            new
            {
                traceId,
                forceReload,
                revision
            });
        TraceSettings("local_settings_load_started", new
        {
            trace_id = traceId,
            force_reload = forceReload,
            revision
        });
        SetSettingsControlsEnabled(false);
        SetStatus(forceReload ? "正在刷新本地设置..." : "正在加载本地设置...", Brushes.Gray);

        try
        {
            var settingsDocument = await Task.Run(() => SettingsRepository.LoadSettingsDocument());
            if (revision != _configLoadRevision)
            {
                DutyDiagnosticsLogger.Warn("SettingsPage", "Discarded stale local settings load result.",
                    new { traceId, revision, latestRevision = _configLoadRevision });
                return _backendConfigState == DutyBackendConfigLoadState.Loaded;
            }

            ApplyLoadedSettingsDocument(settingsDocument, showStatusMessage: true);
            stopwatch.Stop();
            var backendConfig = _lastAppliedBackendConfig ?? CreateBackendConfig(settingsDocument);
            DutyDiagnosticsLogger.Info("SettingsPage", "Local settings load succeeded.",
                new
                {
                    traceId,
                    revision,
                    durationMs = stopwatch.ElapsedMilliseconds,
                    hostVersion = settingsDocument.HostVersion,
                    backendVersion = settingsDocument.BackendVersion,
                    baseUrl = backendConfig.BaseUrl,
                    model = backendConfig.Model,
                    modelProfile = backendConfig.ModelProfile,
                    orchestrationMode = backendConfig.OrchestrationMode,
                    multiAgentExecutionMode = backendConfig.MultiAgentExecutionMode,
                    apiKey = DutyDiagnosticsLogger.MaskSecret(backendConfig.ApiKey)
                });
            TraceSettings("local_settings_load_completed", new
            {
                trace_id = traceId,
                revision,
                duration_ms = stopwatch.ElapsedMilliseconds,
                host_version = settingsDocument.HostVersion,
                backend_version = settingsDocument.BackendVersion,
                selected_plan_id = settingsDocument.Backend?.SelectedPlanId
            });
            return true;
        }
        catch (Exception ex)
        {
            if (revision != _configLoadRevision)
            {
                DutyDiagnosticsLogger.Warn("SettingsPage", "Discarded stale local settings load failure.",
                    new { traceId, revision, latestRevision = _configLoadRevision });
                return _backendConfigState == DutyBackendConfigLoadState.Loaded;
            }

            _backendConfigState = DutyBackendConfigLoadState.LoadFailed;
            _backendConfigErrorMessage = ex.Message;
            _lastLoadedSettingsDocument = null;
            _lastAppliedBackendConfig = null;
            UpdateConfigTracking("本地设置不可用");
            SetStatus($"本地设置不可用：{ex.Message}", Brushes.Orange);
            stopwatch.Stop();
            DutyDiagnosticsLogger.Error("SettingsPage", "Local settings load failed.", ex,
                new
                {
                    traceId,
                    revision,
                    durationMs = stopwatch.ElapsedMilliseconds
                });
            TraceSettings("local_settings_load_failed", new
            {
                trace_id = traceId,
                revision,
                duration_ms = stopwatch.ElapsedMilliseconds,
                error = ex.Message
            }, "ERROR");
            ExportSettingsDiagnosticsOnce("local_settings_load_failed");
            return false;
        }
        finally
        {
            if (revision == _configLoadRevision)
            {
                _isLoadingBackendConfig = false;
                _isLoadingConfig = false;
                SetSettingsControlsEnabled(_backendConfigState == DutyBackendConfigLoadState.Loaded);
            }
        }
    }

    private void ApplyLoadedSettingsDocument(DutySettingsDocument settingsDocument, bool showStatusMessage)
    {
        var hostValues = CreateHostValues(settingsDocument.Host);
        var accessSecurityValues = CreateAccessSecurityValues(settingsDocument.Host);
        var backendConfig = CreateBackendConfig(settingsDocument);
        ExecuteWithoutConfigEvents(() =>
        {
            ApplyHostFormModel(hostValues);
            ApplyAccessSecurityFormModel(accessSecurityValues);
            ApplyBackendConfig(backendConfig);
        });
        ClearPendingConfigApply("settings_document_applied");
        _lastLoadedSettingsDocument = settingsDocument;
        _lastAppliedHostSettings = hostValues;
        _lastAppliedAccessSecurity = accessSecurityValues;
        _lastAppliedBackendConfig = _backendModule.CloneConfig(backendConfig);
        _backendConfigState = DutyBackendConfigLoadState.Loaded;
        _backendConfigErrorMessage = null;
        RefreshBackendSyncStatus(BackendSettingsSyncService.GetStatusSnapshot(), showStatusMessage: false);
        TraceSettings("settings_document_applied", new
        {
            host_version = settingsDocument.HostVersion,
            backend_version = settingsDocument.BackendVersion,
            selected_plan_id = settingsDocument.Backend?.SelectedPlanId,
            plan_count = settingsDocument.Backend?.PlanPresets?.Count ?? 0
        });
        if (_planPresetDrafts.Count == 0 || !_planPresetDrafts.Any(plan => string.Equals(plan.Id, _currentPlanId, StringComparison.Ordinal)))
        {
            SettingsTrace.Invariant("selected_plan_missing_after_apply", "Selected plan was not present after applying the loaded settings document.", BuildSettingsTraceSnapshot(new
            {
                selected_plan_id = _currentPlanId,
                plan_ids = _planPresetDrafts.Select(plan => plan.Id).ToArray()
            }));
        }
        UpdateConfigTracking("本地已加载");
        if (showStatusMessage)
        {
            SetStatus("本地设置已加载。", Brushes.Gray);
        }
    }

    private void ApplyHostFormModel(DutyHostSettingsValues hostSettings)
    {
        var wasLoadingConfig = _isLoadingConfig;
        _isLoadingConfig = true;
        ExecuteWithoutConfigEvents(() =>
        {
            try
            {
                SetAutoRunModeSelection(hostSettings.AutoRunMode);
                SetAutoRunParameterSelection(hostSettings.AutoRunMode, hostSettings.AutoRunParameter);
                SetAutoRunTimeSelection(hostSettings.AutoRunTime);
                AutoRunTriggerNotificationSwitch.IsChecked = hostSettings.AutoRunTriggerNotificationEnabled;
                DutyReminderEnabledSwitch.IsChecked = hostSettings.DutyReminderEnabled;
                DutyReminderTimesBox.Text = DutyMainSettingsHostModule.FormatDutyReminderTimes(hostSettings.DutyReminderTimes);
                SetServerPortModeSelection(hostSettings.ServerPortMode);
                FixedServerPortBox.Text = hostSettings.FixedServerPortText;
                EnableMcpSwitch.IsChecked = hostSettings.EnableMcp;
                EnableWebDebugLayerSwitch.IsChecked = hostSettings.EnableWebViewDebugLayer;
                SetComponentRefreshTimeSelection(hostSettings.ComponentRefreshTime);
                NotificationDurationSlider.Value = hostSettings.NotificationDurationSeconds;
                NotificationDurationLabel.Text = $"{hostSettings.NotificationDurationSeconds} 秒";
                UpdateMcpEndpointControlsState(_backendConfigState == DutyBackendConfigLoadState.Loaded);
                UpdateMcpEndpointStatus();
            }
            finally
            {
                _isLoadingConfig = wasLoadingConfig;
            }
        });
    }

    private void ApplyAccessSecurityFormModel(DutyAccessSecurityValues accessSecurityValues)
    {
        var wasLoadingConfig = _isLoadingConfig;
        _isLoadingConfig = true;
        ExecuteWithoutConfigEvents(() =>
        {
            try
            {
                SetAccessTokenModeSelection(accessSecurityValues.AccessTokenMode);
                StaticAccessTokenBox.Text = string.Empty;
                _lastAppliedAccessSecurity = accessSecurityValues;
                UpdateAccessSecurityStatus();
                UpdateAccessSecurityControlsState(_backendConfigState == DutyBackendConfigLoadState.Loaded);
                UpdateMcpEndpointControlsState(_backendConfigState == DutyBackendConfigLoadState.Loaded);
                UpdateMcpEndpointStatus();
            }
            finally
            {
                _isLoadingConfig = wasLoadingConfig;
            }
        });
    }

    private void ApplyBackendConfig(DutyBackendConfig backendConfig)
    {
        var wasLoadingConfig = _isLoadingConfig;
        _isLoadingConfig = true;
        ExecuteWithoutConfigEvents(() =>
        {
            try
            {
                _planPresetDrafts = _backendModule.NormalizePlanPresets(backendConfig.PlanPresets, backendConfig);
                _currentPlanId = _backendModule.NormalizeSelectedPlanId(backendConfig.SelectedPlanId, _planPresetDrafts, backendConfig);
                RefreshPlanSelectors();
                ApplyCurrentPlanToControls();
                UpdateExecutionModeVisibility();
                UpdatePlanModeHint();
                DutyRuleBox.Text = backendConfig.DutyRule;
            }
            finally
            {
                _isLoadingConfig = wasLoadingConfig;
            }
        });
    }

    private void SetSettingsControlsEnabled(bool enabled)
    {
        AutoRunModeComboBox.IsEnabled = enabled;
        AutoRunDayComboBox.IsEnabled = enabled;
        AutoRunMonthDayComboBox.IsEnabled = enabled;
        AutoRunIntervalBox.IsEnabled = enabled;
        AutoRunHourComboBox.IsEnabled = enabled;
        AutoRunMinuteComboBox.IsEnabled = enabled;
        AutoRunTriggerNotificationSwitch.IsEnabled = enabled;
        DutyReminderEnabledSwitch.IsEnabled = enabled;
        DutyReminderTimesBox.IsEnabled = enabled;
        UpdateAccessSecurityControlsState(enabled);
        UpdateMcpEndpointControlsState(enabled);
        EnableWebDebugLayerSwitch.IsEnabled = enabled;
        ComponentRefreshHourComboBox.IsEnabled = enabled;
        ComponentRefreshMinuteComboBox.IsEnabled = enabled;
        NotificationDurationSlider.IsEnabled = enabled;
        SetBackendConfigControlsEnabled(enabled);
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

    private static DutyAccessSecurityValues CreateAccessSecurityValues(DutyEditableHostSettingsDocument? host)
    {
        return new DutyAccessSecurityValues
        {
            AccessTokenMode = DutyAccessTokenModes.Normalize(host?.AccessTokenMode),
            StaticAccessTokenConfigured = host?.StaticAccessTokenConfigured == true
        };
    }

    private string GetSelectedAccessTokenMode()
    {
        return AccessTokenModeComboBox.SelectedItem is ComboBoxItem item
            ? DutyAccessTokenModes.Normalize(item.Tag as string)
            : DutyAccessTokenModes.Dynamic;
    }

    private void SetAccessTokenModeSelection(string? mode)
    {
        var normalized = DutyAccessTokenModes.Normalize(mode);
        foreach (var item in AccessTokenModeComboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(DutyAccessTokenModes.Normalize(item.Tag as string), normalized, StringComparison.Ordinal))
            {
                AccessTokenModeComboBox.SelectedItem = item;
                return;
            }
        }

        AccessTokenModeComboBox.SelectedIndex = 0;
    }

    private string GetSelectedServerPortMode()
    {
        return ServerPortModeComboBox.SelectedItem is ComboBoxItem item
            ? DutyServerPortModes.Normalize(item.Tag as string)
            : DutyServerPortModes.Random;
    }

    private void SetServerPortModeSelection(string? mode)
    {
        var normalized = DutyServerPortModes.Normalize(mode);
        foreach (var item in ServerPortModeComboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(DutyServerPortModes.Normalize(item.Tag as string), normalized, StringComparison.Ordinal))
            {
                ServerPortModeComboBox.SelectedItem = item;
                return;
            }
        }

        ServerPortModeComboBox.SelectedIndex = 0;
    }

    private void UpdateAccessSecurityControlsState(bool enabled)
    {
        var staticModeSelected = string.Equals(GetSelectedAccessTokenMode(), DutyAccessTokenModes.Static, StringComparison.Ordinal);
        AccessTokenModeComboBox.IsEnabled = enabled && !_isApplyingAccessSecurity;
        StaticAccessTokenBox.IsEnabled = enabled && !_isApplyingAccessSecurity && staticModeSelected;
        ApplyAccessSecurityBtn.IsEnabled = enabled && !_isApplyingAccessSecurity;
        CopyAccessTokenBtn.IsEnabled = enabled && !_isApplyingAccessSecurity;
        ClearStaticAccessTokenBtn.IsEnabled = enabled && !_isApplyingAccessSecurity && _lastAppliedAccessSecurity.StaticAccessTokenConfigured;
    }

    private void UpdateAccessSecurityStatus()
    {
        var runtimeStatus = PythonIpcService.GetAccessTokenStatus();
        var configuredModeText = runtimeStatus.ConfiguredMode == DutyAccessTokenModes.Static ? "静态" : "动态";
        var activeModeText = runtimeStatus.ActiveMode == DutyAccessTokenModes.Static ? "静态" : "动态";
        var configuredText = runtimeStatus.StaticTokenConfigured ? "已配置" : "未配置";
        var copyText = runtimeStatus.CanCopyCurrentToken ? "可复制" : "不可复制";
        var pendingSelection = GetSelectedAccessTokenMode();
        var pendingModeHint = string.Equals(pendingSelection, _lastAppliedAccessSecurity.AccessTokenMode, StringComparison.Ordinal)
            ? string.Empty
            : $"；已选择未保存模式：{(pendingSelection == DutyAccessTokenModes.Static ? "静态" : "动态")}";
        var restartHint = runtimeStatus.BackendReady && !string.Equals(runtimeStatus.ActiveMode, runtimeStatus.ConfiguredMode, StringComparison.Ordinal)
            ? $"；当前后端仍在使用{activeModeText} token，重启后切换"
            : string.Empty;
        AccessTokenStatusText.Text =
            $"已保存模式：{configuredModeText}；静态 token：{configuredText}；当前 token：{copyText}{pendingModeHint}{restartHint}";
    }

    private void UpdateMcpEndpointControlsState(bool enabled)
    {
        var fixedModeSelected = string.Equals(GetSelectedServerPortMode(), DutyServerPortModes.Fixed, StringComparison.Ordinal);
        var endpointStatus = PythonIpcService.GetServiceEndpointStatus();
        var hasCopyableRuntimeConfig = enabled &&
                                       endpointStatus.EnableMcpActive &&
                                       !string.IsNullOrWhiteSpace(endpointStatus.McpUrl) &&
                                       !string.IsNullOrWhiteSpace(PythonIpcService.GetCurrentAccessToken());

        EnableMcpSwitch.IsEnabled = enabled;
        ServerPortModeComboBox.IsEnabled = enabled;
        FixedServerPortItem.IsVisible = fixedModeSelected;
        FixedServerPortBox.IsEnabled = enabled && fixedModeSelected;
        CopyMcpUrlBtn.IsEnabled = hasCopyableRuntimeConfig;
        CopyMcpClientConfigBtn.IsEnabled = hasCopyableRuntimeConfig;
    }

    private void UpdateMcpEndpointStatus()
    {
        var endpointStatus = PythonIpcService.GetServiceEndpointStatus();
        var currentToken = PythonIpcService.GetCurrentAccessToken();
        var canCopyConfig = endpointStatus.EnableMcpActive &&
                            !string.IsNullOrWhiteSpace(endpointStatus.McpUrl) &&
                            !string.IsNullOrWhiteSpace(currentToken);
        var actualPortText = endpointStatus.ActualPort?.ToString() ?? "未就绪";
        string statusText;

        if (endpointStatus.PortConflictFallbackActive)
        {
            statusText = endpointStatus.StatusMessage;
        }
        else if (endpointStatus.EnableMcpActive && !string.IsNullOrWhiteSpace(endpointStatus.McpUrl))
        {
            statusText = $"MCP 已启用，当前地址：{endpointStatus.McpUrl}；当前实际端口：{actualPortText}。";
        }
        else if (endpointStatus.EnableMcpConfigured)
        {
            statusText = endpointStatus.ActualPort.HasValue
                ? $"MCP 已启用，但当前地址暂不可用；当前实际端口：{actualPortText}。"
                : "MCP 已启用，但后端尚未就绪。";
        }
        else
        {
            statusText = endpointStatus.ActualPort.HasValue
                ? $"MCP 未启用；当前服务端口：{actualPortText}。"
                : "MCP 未启用。";
        }

        var pendingMode = GetSelectedServerPortMode();
        var pendingFixedPortText = NormalizeFixedServerPortText(FixedServerPortBox.Text);
        var lastAppliedFixedPortText = NormalizeFixedServerPortText(_lastAppliedHostSettings.FixedServerPortText);
        var hasPendingEndpointChanges =
            EnableMcpSwitch.IsChecked == true != _lastAppliedHostSettings.EnableMcp ||
            !string.Equals(DutyServerPortModes.Normalize(_lastAppliedHostSettings.ServerPortMode), pendingMode, StringComparison.Ordinal) ||
            !string.Equals(lastAppliedFixedPortText, pendingFixedPortText, StringComparison.Ordinal);
        var pendingHint = hasPendingEndpointChanges ? "；已选择未保存的 MCP 端口设置，保存并重启后生效。" : string.Empty;
        var copyText = canCopyConfig ? "可复制" : "不可复制";

        McpEndpointStatusText.Text = $"{statusText} 当前客户端配置：{copyText}{pendingHint}";
    }

    private bool TryGetCopyableMcpEndpoint(out string mcpUrl, out string token, out string errorMessage)
    {
        var endpointStatus = PythonIpcService.GetServiceEndpointStatus();
        token = PythonIpcService.GetCurrentAccessToken() ?? string.Empty;
        mcpUrl = endpointStatus.McpUrl;
        if (!endpointStatus.EnableMcpActive || string.IsNullOrWhiteSpace(mcpUrl))
        {
            errorMessage = endpointStatus.PortConflictFallbackActive
                ? endpointStatus.StatusMessage
                : "当前 MCP 地址不可用，请确认 MCP 已启用且后端已就绪。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            errorMessage = "当前访问 token 不可用，请确认访问鉴权已生效且后端已就绪。";
            return false;
        }

        errorMessage = string.Empty;
        return true;
    }

    private static string NormalizeFixedServerPortText(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return int.TryParse(normalized, out var port) ? port.ToString() : normalized;
    }

    private DutyHostSettingsValues CreateHostValues(DutyEditableHostSettingsDocument host)
    {
        return new DutyHostSettingsValues
        {
            AutoRunMode = host.AutoRunMode,
            AutoRunParameter = host.AutoRunParameter,
            AutoRunTime = host.AutoRunTime,
            AutoRunTriggerNotificationEnabled = host.AutoRunTriggerNotificationEnabled,
            DutyReminderEnabled = host.DutyReminderEnabled,
            DutyReminderTimes = DutyMainSettingsHostModule.NormalizeDutyReminderTimes(host.DutyReminderTimes),
            ServerPortMode = DutyServerPortModes.Normalize(host.ServerPortMode),
            FixedServerPortText = host.FixedServerPort?.ToString() ?? string.Empty,
            EnableMcp = host.EnableMcp,
            EnableWebViewDebugLayer = host.EnableWebViewDebugLayer,
            ComponentRefreshTime = host.ComponentRefreshTime,
            NotificationDurationSeconds = host.NotificationDurationSeconds
        };
    }

    private void OnBackendSyncStatusChanged(object? sender, DutyBackendSyncStatusSnapshot snapshot)
    {
        Dispatcher.UIThread.Post(() => RefreshBackendSyncStatus(snapshot, showStatusMessage: true));
    }

    private void RefreshBackendSyncStatus(DutyBackendSyncStatusSnapshot snapshot, bool showStatusMessage)
    {
        var previousState = _backendSyncStatus.State;
        _backendSyncStatus = snapshot;
        UpdateConfigTracking(_lastLocalConfigState);
        if (previousState != snapshot.State || showStatusMessage)
        {
            TraceSettings("backend_sync_status_changed", new
            {
                state = snapshot.State.ToString(),
                settings_version = snapshot.SettingsVersion,
                last_error = snapshot.LastError,
                show_status_message = showStatusMessage
            });
        }

        if (!showStatusMessage)
        {
            return;
        }

        switch (snapshot.State)
        {
            case DutyBackendSyncState.Syncing:
                SetStatus("本地设置已保存，后端正在同步...", Brushes.Gray);
                break;
            case DutyBackendSyncState.Synced:
                SetStatus("本地设置已保存，后端已同步。", Brushes.Green);
                break;
            case DutyBackendSyncState.Failed:
                SetStatus(
                    string.IsNullOrWhiteSpace(snapshot.LastError)
                        ? "本地设置已保存，但后端同步失败，将自动重试。"
                        : $"本地设置已保存，但后端同步失败：{snapshot.LastError}",
                    Brushes.Orange);
                break;
        }
    }

    private DutyBackendConfig CreateBackendConfig(DutySettingsDocument document)
    {
        return CreateBackendConfig(document.Backend, document.BackendVersion);
    }

    private DutyBackendConfig CreateBackendConfig(DutyEditableBackendSettingsDocument? backend, int backendVersion)
    {
        backend ??= new DutyEditableBackendSettingsDocument();
        var planPresets = _backendModule.NormalizePlanPresets(backend.PlanPresets);
        var selectedPlanId = _backendModule.NormalizeSelectedPlanId(backend.SelectedPlanId, planPresets);
        var selectedPlan = _backendModule.GetSelectedPlan(planPresets, selectedPlanId) ?? planPresets.First();
        var selectedModeId = _backendModule.NormalizePlanModeId(selectedPlan.ModeId);

        return new DutyBackendConfig
        {
            Version = backendVersion,
            ApiKey = selectedPlan.ApiKey,
            BaseUrl = selectedPlan.BaseUrl,
            Model = selectedPlan.Model,
            ModelProfile = selectedPlan.ModelProfile,
            OrchestrationMode = string.Equals(selectedModeId, DutyBackendModeIds.Agents, StringComparison.Ordinal)
                ? "multi_agent"
                : "single_pass",
            MultiAgentExecutionMode = string.Equals(selectedModeId, DutyBackendModeIds.Agents, StringComparison.Ordinal)
                ? selectedPlan.MultiAgentExecutionMode
                : "auto",
            SinglePassStrategy = string.Equals(selectedModeId, DutyBackendModeIds.IncrementalSmall, StringComparison.Ordinal)
                ? "incremental_thinking"
                : "auto",
            ProviderHint = selectedPlan.ProviderHint,
            SelectedPlanId = selectedPlanId,
            PlanPresets = _backendModule.ClonePlanPresets(planPresets),
            DutyRule = backend.DutyRule ?? string.Empty
        };
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
        finally
        {
            if (revision == _dataLoadRevision)
            {
                UpdateAccessSecurityStatus();
                UpdateMcpEndpointControlsState(_backendConfigState == DutyBackendConfigLoadState.Loaded);
                UpdateMcpEndpointStatus();
            }
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
            ? string.IsNullOrWhiteSpace(_scheduleEditorSourceDate)
                ? (ScheduleListBox.SelectedItem as DutyScheduleRow)?.Date
                : _scheduleEditorSourceDate
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
        if (_isPopulatingEditor)
        {
            return;
        }

        if (_isScheduleEditorDirty && !string.IsNullOrWhiteSpace(_scheduleEditorSourceDate))
        {
            var selectedDate = (ScheduleListBox.SelectedItem as DutyScheduleRow)?.Date ?? string.Empty;
            if (!string.Equals(selectedDate, _scheduleEditorSourceDate, StringComparison.Ordinal))
            {
                try
                {
                    _isPopulatingEditor = true;
                    if (ScheduleListBox.ItemsSource is IEnumerable<DutyScheduleRow> rows)
                    {
                        ScheduleListBox.SelectedItem = rows.LastOrDefault(x =>
                            string.Equals(x.Date, _scheduleEditorSourceDate, StringComparison.Ordinal));
                    }
                }
                finally
                {
                    _isPopulatingEditor = false;
                }

                SetStatus("当前排班有未保存修改，请先保存或点击【放弃更改】。", Brushes.Orange);
                return;
            }
        }

        PopulateScheduleEditorFromSelection();
    }

    private void OnReloadScheduleEditClick(object? sender, RoutedEventArgs e)
    {
        PopulateScheduleEditorFromSelection();
    }

    private async void OnSaveScheduleEditClick(object? sender, RoutedEventArgs e)
    {
        var sourceDate = !string.IsNullOrWhiteSpace(_scheduleEditorSourceDate)
            ? _scheduleEditorSourceDate
            : (ScheduleListBox.SelectedItem as DutyScheduleRow)?.Date;
        if (string.IsNullOrWhiteSpace(sourceDate))
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
        var recordDebtCreditChanges = ShouldRecordScheduleEditorChanges();
        try
        {
            var response = await Service.SaveScheduleEntryAsync(
                sourceDate: sourceDate,
                targetDate: targetDate,
                day: targetDay,
                areaAssignments: areaAssignments,
                note: note,
                confirmOverwrite: true,
                recordDebtCreditChanges: recordDebtCreditChanges);

            _pendingScheduleSelectionDate = targetDate;
            _scheduleEditorSourceDate = targetDate;
            _isScheduleEditorDirty = false;
            ApplySnapshotPreview(response.Snapshot, "手动编辑排班");
            SetStatus(response.Message, Brushes.Green);
        }
        catch (Exception ex)
        {
            SetStatus($"保存失败：{ex.Message}", Brushes.Red);
        }
    }

    private async void OnCreateScheduleEditClick(object? sender, RoutedEventArgs e)
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
        var recordDebtCreditChanges = ShouldRecordScheduleEditorChanges();
        try
        {
            var response = await Service.SaveScheduleEntryAsync(
                sourceDate: null,
                targetDate: targetDate,
                day: targetDay,
                areaAssignments: areaAssignments,
                note: note,
                confirmOverwrite: true,
                recordDebtCreditChanges: recordDebtCreditChanges);

            _pendingScheduleSelectionDate = targetDate;
            _scheduleEditorSourceDate = targetDate;
            _isScheduleEditorDirty = false;
            ApplySnapshotPreview(response.Snapshot, "手动新建排班");
            SetStatus(response.Message, Brushes.Green);
        }
        catch (Exception ex)
        {
            SetStatus($"新建失败：{ex.Message}", Brushes.Red);
        }
    }

    private bool ShouldRecordScheduleEditorChanges()
    {
        return _scheduleRecordModeComboBox?.SelectedItem is ComboBoxItem { Tag: string tag }
               && string.Equals(tag, "record", StringComparison.OrdinalIgnoreCase);
    }

    private void ApplySnapshotPreview(DutyBackendSnapshot snapshot, string reason)
    {
        var rosterPreview = _rosterModule.BuildPreview(snapshot.Roster ?? [], snapshot.State ?? new DutyState(), DateTime.Today);
        var schedulePreview = _scheduleModule.BuildPreview(snapshot.State ?? new DutyState());
        ApplyRosterPreview(rosterPreview);
        ApplySchedulePreview(schedulePreview);
        UpdateStudentActionButtons();
        UpdateDataTracking(reason);
    }

    private void PopulateScheduleEditorFromSelection()
    {
        _isPopulatingEditor = true;
        try
        {
            if (ScheduleListBox.SelectedItem is not DutyScheduleRow selected)
            {
                _scheduleEditorSourceDate = string.Empty;
                _isScheduleEditorDirty = false;
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
                _scheduleEditorSourceDate = string.Empty;
                _isScheduleEditorDirty = false;
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
            _scheduleEditorSourceDate = selected.Date;
            _isScheduleEditorDirty = false;
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
        _isScheduleEditorDirty = true;
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
        if (ShouldIgnoreConfigEvent()) return;
        var val = (int)e.NewValue;
        NotificationDurationLabel.Text = $"{val} \u79d2";
        QueueConfigApply();
    }

    private void OnTestNotificationClicked(object? sender, RoutedEventArgs e)
    {
        Service.PublishDutyReminderNotificationNow();
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
        return _backendModule.NormalizeSelectedPlanId(_currentPlanId, _planPresetDrafts, _lastAppliedBackendConfig);
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
        ExecuteWithoutConfigEvents(() =>
        {
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
        });
    }

    private void RefreshPlanSelectorsIfNeeded(object? sender)
    {
        if (ReferenceEquals(sender, PlanNameBox))
        {
            RefreshPlanSelectors();
        }
    }

    private bool HasPlanSelectorChanges(IReadOnlyList<DutyPlanPreset> normalizedPlans, string normalizedSelectedPlanId)
    {
        if (!string.Equals(_currentPlanId, normalizedSelectedPlanId, StringComparison.Ordinal))
        {
            return true;
        }

        if (_planPresetDrafts.Count != normalizedPlans.Count)
        {
            return true;
        }

        for (var i = 0; i < normalizedPlans.Count; i++)
        {
            var current = _planPresetDrafts[i];
            var incoming = normalizedPlans[i];
            if (!string.Equals(current.Id, incoming.Id, StringComparison.Ordinal) ||
                !string.Equals(current.Name, incoming.Name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
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
        ExecuteWithoutConfigEvents(() =>
        {
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
        });

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
        plan.MultiAgentExecutionMode = string.Equals(plan.ModeId, DutyBackendModeIds.Agents, StringComparison.Ordinal)
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
            MultiAgentExecutionMode = string.Equals(modeId, DutyBackendModeIds.Agents, StringComparison.Ordinal)
                ? source?.MultiAgentExecutionMode ?? "auto"
                : "auto"
        };
    }

    private static string GetPlanModeDisplayName(string modeId)
    {
        return modeId switch
        {
            DutyBackendModeIds.Agents => "Agents",
            DutyBackendModeIds.IncrementalSmall => "增量小模型",
            _ => "标准"
        };
    }

    private static string GetPlanModeHint(string modeId)
    {
        return modeId switch
        {
            DutyBackendModeIds.Agents => "使用 Agents 执行链路，适合更稳定的结构化排班。",
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
            DutyReminderTimes = DutyMainSettingsHostModule.NormalizeDutyReminderTimes([DutyReminderTimesBox.Text ?? string.Empty]),
            ServerPortMode = GetSelectedServerPortMode(),
            FixedServerPortText = (FixedServerPortBox.Text ?? string.Empty).Trim(),
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
        _lastLocalConfigState = string.IsNullOrWhiteSpace(state) ? "未应用" : state.Trim();
        _lastConfigState = $"{_lastLocalConfigState}｜后端{DescribeBackendSyncState(_backendSyncStatus)}";
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

    private static string DescribeBackendSyncState(DutyBackendSyncStatusSnapshot snapshot)
    {
        return snapshot.State switch
        {
            DutyBackendSyncState.Syncing => $"同步中(v{snapshot.SettingsVersion})",
            DutyBackendSyncState.Synced => $"已同步(v{snapshot.SettingsVersion})",
            DutyBackendSyncState.Failed => string.IsNullOrWhiteSpace(snapshot.LastError)
                ? $"同步失败(v{snapshot.SettingsVersion})"
                : $"同步失败(v{snapshot.SettingsVersion})",
            _ => snapshot.SettingsVersion > 0 ? $"待同步(v{snapshot.SettingsVersion})" : "未同步"
        };
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

    private async void OnExportSettingsDiagnosticsClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            TraceSettings("diagnostics_export_requested");
            var zipPath = await Task.Run(() => SettingsTrace.ExportDiagnosticsBundle(BuildSettingsTraceSnapshot()));
            SetStatus($"诊断包已导出：{zipPath}", Brushes.Green);
            TraceSettings("diagnostics_export_completed", new { zip_path = zipPath });
        }
        catch (Exception ex)
        {
            SetStatus($"导出诊断包失败：{ex.Message}", Brushes.Red);
            TraceSettings("diagnostics_export_failed", new { error = ex.Message }, "ERROR");
        }
    }

    private async Task WarnIfInitialSettingsStillUnavailableAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(2));
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_backendConfigState == DutyBackendConfigLoadState.Loaded)
            {
                return;
            }

            SettingsTrace.Invariant("initial_settings_load_timeout", "Settings page is still not in loaded state two seconds after opening.", BuildSettingsTraceSnapshot());
            ExportSettingsDiagnosticsOnce("initial_settings_load_timeout");
        });
    }

    private bool ShouldIgnoreConfigEvent()
    {
        return _isLoadingConfig || _configEventSuspendDepth > 0;
    }

    private void ExecuteWithoutConfigEvents(Action action)
    {
        _configEventSuspendDepth++;
        try
        {
            action();
        }
        finally
        {
            _configEventSuspendDepth--;
        }
    }

    private void ClearPendingConfigApply(string reason)
    {
        _configApplyDebounceTimer.Stop();
        _hasPendingConfigApply = false;
        TraceSettings("pending_save_cleared", new { reason });
    }

    private void TraceSettings(string eventType, object? extra = null, string level = "INFO")
    {
        var payload = BuildSettingsTraceSnapshot(extra);
        switch (level)
        {
            case "WARN":
                SettingsTrace.Warn(eventType, payload);
                break;
            case "ERROR":
                SettingsTrace.Error(eventType, payload);
                break;
            default:
                SettingsTrace.Info(eventType, payload);
                break;
        }
    }

    private object BuildSettingsTraceSnapshot(object? extra = null)
    {
        return new
        {
            page_instance_id = _pageInstanceId,
            is_loading_config = _isLoadingConfig,
            is_loading_backend_config = _isLoadingBackendConfig,
            is_applying_config = _isApplyingConfig,
            has_pending_config_apply = _hasPendingConfigApply,
            config_load_revision = _configLoadRevision,
            config_edit_revision = _configEditRevision,
            backend_config_state = _backendConfigState.ToString(),
            backend_sync_state = _backendSyncStatus.State.ToString(),
            backend_sync_version = _backendSyncStatus.SettingsVersion,
            current_plan_id = _currentPlanId,
            selected_plan_option_id = CurrentPlanComboBox?.SelectedItem is PlanSelectOption option ? option.Id : null,
            last_local_state = _lastLocalConfigState,
            last_combined_state = _lastConfigState,
            last_loaded_host_version = _lastLoadedSettingsDocument?.HostVersion,
            last_loaded_backend_version = _lastLoadedSettingsDocument?.BackendVersion,
            last_loaded_selected_plan_id = _lastLoadedSettingsDocument?.Backend?.SelectedPlanId,
            plan_ids = _planPresetDrafts.Select(plan => plan.Id).ToArray(),
            settings_path = PluginPaths.SettingsPath,
            extra
        };
    }

    private static string GetControlName(object? sender)
    {
        return sender switch
        {
            Control control when !string.IsNullOrWhiteSpace(control.Name) => control.Name,
            _ => sender?.GetType().Name ?? "unknown"
        };
    }

    private void ExportSettingsDiagnosticsOnce(string reason)
    {
        if (_settingsDiagnosticsExported)
        {
            return;
        }

        _settingsDiagnosticsExported = true;
        try
        {
            var zipPath = SettingsTrace.ExportDiagnosticsBundle(BuildSettingsTraceSnapshot(new { reason }));
            DutyDiagnosticsLogger.Warn("SettingsPage", "Auto-exported settings diagnostics bundle.",
                new { reason, zipPath });
            TraceSettings("diagnostics_export_completed", new
            {
                reason,
                zip_path = zipPath,
                automatic = true
            });
        }
        catch (Exception ex)
        {
            DutyDiagnosticsLogger.Error("SettingsPage", "Failed to auto-export settings diagnostics bundle.", ex,
                new { reason });
            TraceSettings("diagnostics_export_failed", new
            {
                reason,
                automatic = true,
                error = ex.Message
            }, "ERROR");
        }
    }

    private async void OnResetPluginDataClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        var confirmed = await ContentDialogHelper.ShowConfirmationDialog(
            "重置 Duty-Agent 插件数据",
            "此操作会清空 Duty-Agent 的插件设置、后端配置、名单、排班状态和兼容旧版遗留数据。仅删除插件或重新安装插件并不会自动执行这个重置操作。\n\n如果你确认要继续，请输入：我确认重置 Duty-Agent 数据",
            "我确认重置 Duty-Agent 数据",
            topLevel,
            positiveText: "重置",
            negativeText: "取消");
        if (!confirmed)
        {
            return;
        }

        try
        {
            ResetPluginDataBtn.IsEnabled = false;
            RunAgentBtn.IsEnabled = false;
            TraceSettings("plugin_data_reset_requested");
            SetStatus("正在重置插件数据...", Brushes.Orange);

            await PythonIpcService.StopAsync();
            var document = SettingsRepository.ResetPersistedData();
            BackendSettingsSyncService.RequestSync("plugin_data_reset");
            await PythonIpcService.RestartEngineAsync();

            ExecuteWithoutConfigEvents(() =>
            {
                InstructionBox.Text = string.Empty;
                ReasoningBoardText.Text = string.Empty;
                ReasoningBoardContainer.IsVisible = false;
            });

            ApplyLoadedSettingsDocument(document, showStatusMessage: false);
            await LoadDataAsync("插件数据重置");

            TraceSettings("plugin_data_reset_completed", new
            {
                data_directory = PluginPaths.DataDirectory
            });
            UpdateConfigTracking("插件数据已重置");
            UpdateDataTracking("插件数据已重置");
            SetStatus("插件数据已重置，当前已恢复到默认状态。", Brushes.Green);
        }
        catch (Exception ex)
        {
            TraceSettings("plugin_data_reset_failed", new { error = ex.Message }, "ERROR");
            SetStatus($"重置插件数据失败：{ex.Message}", Brushes.Red);
        }
        finally
        {
            ResetPluginDataBtn.IsEnabled = true;
            RunAgentBtn.IsEnabled = true;
        }
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
