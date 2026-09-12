using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Shared;
using DutyAgentBridge.Models;
using DutyAgentBridge.Services;

namespace DutyAgentBridge.Views;

[SettingsPageInfo("duty-agent-bridge.settings", "Duty-Agent \u6865\u63A5", "\uE31E", "\uE31E")]
public partial class DutyBridgeSettingsPage : SettingsPageBase
{
    private readonly IBridgePaths _paths;
    private readonly IIpcBridgeService _bridge;
    private readonly DispatcherTimer? _confirmResetTimer;
    private bool _testArmed;

    public DutyBridgeSettingsPage()
    {
        // Avalonia XAML 编译器要求无参构造；依赖经宿主服务定位器解析
        // （Settings 为 Plugin.Initialize 注册的单例实例）。
        Settings = IAppHost.GetService<BridgeSettings>();
        _paths = IAppHost.GetService<IBridgePaths>();
        _bridge = IAppHost.GetService<IIpcBridgeService>();

        InitializeComponent();
        DataContext = this;

        VersionText.Text = $"Duty-Agent Bridge v{GetType().Assembly.GetName().Version?.ToString(3) ?? "?"}";
        UpdatePathDiagnostics();
        UpdateConnectionStatus();

        // 页面是按导航重建的 transient 实例：Loaded/Unloaded 管理订阅，避免事件泄漏。
        Loaded += (_, _) => _bridge.StateChanged += OnBridgeStateChanged;
        Unloaded += (_, _) => _bridge.StateChanged -= OnBridgeStateChanged;

        _confirmResetTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _confirmResetTimer.Tick += (_, _) =>
        {
            _confirmResetTimer.Stop();
            _testArmed = false;
            TestScheduleButton.Content = "测试排班";
        };
    }

    private BridgeSettings Settings { get; }

    private void OnBridgeStateChanged(object? sender, IpcBridgeState state)
    {
        Dispatcher.UIThread.Post(UpdateConnectionStatus);
    }

    private void UpdateConnectionStatus()
    {
        var state = _bridge.State;
        var meta = _bridge.CurrentMeta;

        StatusIndicator.Text = state switch
        {
            IpcBridgeState.Initial => "\u25CB 未连接",
            IpcBridgeState.Checking => "\u25CB 检测中...",
            IpcBridgeState.Connecting => "\u25CB 连接中...",
            IpcBridgeState.Connected => "\u25CF 已连接",
            IpcBridgeState.Disconnected => "\u25CB 已断开",
            IpcBridgeState.NotInstalled => "\u25CB 未检测到独立客户端",
            IpcBridgeState.Error => $"\u26A0 错误: {_bridge.LastError ?? ""}",
            _ => "\u25CB 未知"
        };

        StatusIndicator.Foreground = state switch
        {
            IpcBridgeState.Connected => new SolidColorBrush(Color.Parse("#52c41a")),
            IpcBridgeState.Error => new SolidColorBrush(Color.Parse("#ff4d4f")),
            _ => new SolidColorBrush(Color.Parse("#8c8c8c"))
        };

        if (meta is not null)
        {
            ConnectionDetails.Text = $"Port: {meta.Port} | PID: {meta.ProcessId} | Version: {meta.Version}";
            ConnectionDetails.IsVisible = true;
        }
        else
        {
            ConnectionDetails.IsVisible = false;
        }

        ReconnectButton.IsEnabled = state != IpcBridgeState.Connecting && state != IpcBridgeState.Checking;
    }

    private void UpdatePathDiagnostics()
    {
        MetaSourceText.Text = $"Meta 来源: {_paths.MetaSource}";
        MetaFileText.Text = $"Meta 文件: {_paths.MetaFilePath}";
        LogsDirectoryText.Text = $"日志目录: {_paths.LogsDirectory}";
    }

    private void OnReconnectClick(object? sender, RoutedEventArgs e)
    {
        _bridge.Reconnect();
    }

    private void OnOpenStandaloneClick(object? sender, RoutedEventArgs e)
    {
        var meta = _bridge.CurrentMeta;
        if (meta is not null && !string.IsNullOrWhiteSpace(meta.DataDir))
        {
            OpenFolder(meta.DataDir);
        }
        else
        {
            OpenFolder(_paths.PluginConfigFolder);
        }
    }

    private void OnOpenConfigFolderClick(object? sender, RoutedEventArgs e)
    {
        OpenFolder(_paths.PluginConfigFolder);
    }

    private void OnOpenLogsFolderClick(object? sender, RoutedEventArgs e)
    {
        OpenFolder(_paths.LogsDirectory);
    }

    private static void OpenFolder(string folder)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(folder))
            {
                Directory.CreateDirectory(folder);
                Process.Start(new ProcessStartInfo("explorer.exe", folder) { UseShellExecute = true });
            }
        }
        catch
        {
        }
    }

    private async void OnTestScheduleClick(object? sender, RoutedEventArgs e)
    {
        // 两段式确认：第一次点击仅武装（3 秒后自动复位），避免误触发真实排班。
        if (!_testArmed)
        {
            _testArmed = true;
            TestScheduleButton.Content = "确认执行？";
            _confirmResetTimer?.Start();
            return;
        }

        _confirmResetTimer?.Stop();
        _testArmed = false;
        TestScheduleButton.Content = "测试排班";

        if (_bridge.State != IpcBridgeState.Connected)
        {
            TestResult.Text = "桥接未连接独立软件。";
            TestResult.IsVisible = true;
            return;
        }

        TestScheduleButton.IsEnabled = false;
        TestResult.Text = "排班执行中…（由独立软件配置的模型完成，可能需要数十秒）";
        TestResult.IsVisible = true;
        try
        {
            var result = await _bridge.RunScheduleAsync("请基于当前名册生成明天的值日安排。");
            TestResult.Text = result.Success
                ? $"排班完成：{(result.AiResponse ?? "").Trim()}"
                : $"排班失败：{result.Message}";
        }
        catch (Exception ex)
        {
            TestResult.Text = $"排班失败：{ex.Message}";
        }
        finally
        {
            TestScheduleButton.IsEnabled = true;
        }
    }
}
