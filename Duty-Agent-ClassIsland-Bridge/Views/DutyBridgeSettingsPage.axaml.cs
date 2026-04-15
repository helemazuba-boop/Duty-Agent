using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Shared;
using DutyAgentBridge.Models;
using DutyAgentBridge.Services;

namespace DutyAgentBridge.Views;

/// <summary>
/// 桥接插件设置页（简化版：仅显示联动状态和入口链接）
/// </summary>
[FullWidthPage]
[HidePageTitle]
[SettingsPageInfo("duty-agent-bridge.settings", "Duty-Agent 桥接", "\uE31E", "\uE31E")]
public partial class DutyBridgeSettingsPage : SettingsPageBase
{
    private readonly IIpcBridgeService _bridge = IAppHost.GetService<IIpcBridgeService>();
    private readonly IHealthMonitorService _healthMonitor = IAppHost.GetService<IHealthMonitorService>();

    public DutyBridgeSettingsPage()
    {
        InitializeComponent();

        _bridge.StateChanged += OnBridgeStateChanged;
        UpdateConnectionStatus();
    }

    private void OnBridgeStateChanged(object? sender, IpcBridgeState state)
    {
        Dispatcher.UIThread.Post(UpdateConnectionStatus);
    }

    private void UpdateConnectionStatus()
    {
        var state = _bridge.State;
        var meta = _bridge.CurrentMeta;

        // 更新状态标签
        StatusIndicator.Text = state switch
        {
            IpcBridgeState.Initial => "\u25CB \u672A\u8FDE\u63A5",
            IpcBridgeState.Checking => "\u25CB \u68C0\u6D4B\u4E2D...",
            IpcBridgeState.Connecting => "\u25CB \u8FDE\u63A5\u4E2D...",
            IpcBridgeState.Connected => "\u25CF \u5DF2\u8FDE\u63A5",
            IpcBridgeState.Disconnected => "\u25CB \u5DF2\u65AD\u5F00",
            IpcBridgeState.NotInstalled => "\u25CB \u672A\u68C0\u6D4B\u5230\u72EC\u7ACB\u8F6F\u4EF6",
            IpcBridgeState.Error => $"\u26A0 \u9519\u8BEF: {_bridge.LastError ?? ""}",
            _ => "\u25CB \u672A\u77E5"
        };

        StatusIndicator.Foreground = state switch
        {
            IpcBridgeState.Connected => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#52c41a")),
            IpcBridgeState.Error => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#ff4d4f")),
            _ => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#8c8c8c"))
        };

        // 更新详情
        if (meta != null)
        {
            ConnectionDetails.Text = $"Port: {meta.Port} | PID: {meta.ProcessId} | Version: {meta.Version}";
            ConnectionDetails.IsVisible = true;
        }
        else
        {
            ConnectionDetails.IsVisible = false;
        }

        // 更新按钮状态
        ReconnectButton.IsEnabled = state != IpcBridgeState.Connecting && state != IpcBridgeState.Checking;
    }

    private async void OnReconnectClick(object? sender, RoutedEventArgs e)
    {
        _bridge.Reconnect();
    }

    private void OnOpenStandaloneClick(object? sender, RoutedEventArgs e)
    {
        // 提示用户手动启动独立软件
        // 可以尝试打开独立软件的安装目录或配置文件
        try
        {
            var meta = _bridge.CurrentMeta;
            if (meta != null && !string.IsNullOrWhiteSpace(meta.DataDir))
            {
                // 打开数据目录
                System.Diagnostics.Process.Start("explorer.exe", meta.DataDir);
            }
        }
        catch
        {
            // 静默忽略
        }
    }

    private async void OnTestScheduleClick(object? sender, RoutedEventArgs e)
    {
        if (_bridge.State != IpcBridgeState.Connected)
        {
            return;
        }

        TestResult.Text = "\u6D4B\u8BD5\u4E2D...";
        TestResult.IsVisible = true;

        try
        {
            var result = await _bridge.RunScheduleAsync("请基于当前名册生成值日安排。");
            TestResult.Text = result.Success
                ? $"\u6210\u529F: {result.Message}"
                : $"\u5931\u8D25: {result.Message}";
            TestResult.Foreground = result.Success
                ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#52c41a"))
                : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#ff4d4f"));
        }
        catch (Exception ex)
        {
            TestResult.Text = $"\u9519\u8BEF: {ex.Message}";
            TestResult.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#ff4d4f"));
        }
    }
}
