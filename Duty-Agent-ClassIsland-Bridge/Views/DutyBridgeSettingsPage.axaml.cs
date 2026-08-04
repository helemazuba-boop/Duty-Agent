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

[FullWidthPage]
[HidePageTitle]
[SettingsPageInfo("duty-agent-bridge.settings", "Duty-Agent \u6865\u63A5", "\uE31E", "\uE31E")]
public partial class DutyBridgeSettingsPage : SettingsPageBase
{
    private readonly IIpcBridgeService _bridge = IAppHost.GetService<IIpcBridgeService>();
    private readonly IBridgePaths _paths = IAppHost.GetService<IBridgePaths>();

    public DutyBridgeSettingsPage()
    {
        InitializeComponent();

        _bridge.StateChanged += OnBridgeStateChanged;
        UpdateConnectionStatus();
        UpdatePathDiagnostics();
    }

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
            IpcBridgeState.Initial => "\u25CB \u672A\u8FDE\u63A5",
            IpcBridgeState.Checking => "\u25CB \u68C0\u6D4B\u4E2D...",
            IpcBridgeState.Connecting => "\u25CB \u8FDE\u63A5\u4E2D...",
            IpcBridgeState.Connected => "\u25CF \u5DF2\u8FDE\u63A5",
            IpcBridgeState.Disconnected => "\u25CB \u5DF2\u65AD\u5F00",
            IpcBridgeState.NotInstalled => "\u25CB \u672A\u68C0\u6D4B\u5230\u72EC\u7ACB\u5BA2\u6237\u7AEF",
            IpcBridgeState.Error => $"\u26A0 \u9519\u8BEF: {_bridge.LastError ?? ""}",
            _ => "\u25CB \u672A\u77E5"
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
        ConfigSourceText.Text = $"\u53D1\u73B0\u6765\u6E90: {_paths.ConfigSource}";
        ConfigFolderText.Text = $"ClassIsland \u914D\u7F6E\u76EE\u5F55: {_paths.ConfigFolder}";
        MetaFileText.Text = $"Meta \u6587\u4EF6: {_paths.MetaFilePath}";
        LogsDirectoryText.Text = $"\u65E5\u5FD7\u76EE\u5F55: {_paths.LogsDirectory}";
        ConfigCandidatesText.Text = string.Join(
            Environment.NewLine,
            _paths.ConfigCandidates.Select(candidate =>
                $"- [{FormatCandidateState(candidate)}] {candidate.Source}: {candidate.ConfigFolder}"));
    }

    private static string FormatCandidateState(BridgeConfigCandidate candidate)
    {
        if (candidate.HasLiveMeta)
        {
            return "\u5B58\u5728, live meta";
        }

        return candidate.Exists ? "\u5B58\u5728" : "\u4E0D\u5B58\u5728";
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
            OpenFolder(_paths.SharedConfigFolder);
        }
    }

    private void OnOpenConfigFolderClick(object? sender, RoutedEventArgs e)
    {
        OpenFolder(_paths.SharedConfigFolder);
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
                System.Diagnostics.Process.Start("explorer.exe", folder);
            }
        }
        catch
        {
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
            var result = await _bridge.RunScheduleAsync("\u8BF7\u57FA\u4E8E\u5F53\u524D\u540D\u518C\u751F\u6210\u503C\u65E5\u5B89\u6392\u3002");
            TestResult.Text = result.Success
                ? $"\u6210\u529F: {result.Message}"
                : $"\u5931\u8D25: {result.Message}";
            TestResult.Foreground = result.Success
                ? new SolidColorBrush(Color.Parse("#52c41a"))
                : new SolidColorBrush(Color.Parse("#ff4d4f"));
        }
        catch (Exception ex)
        {
            TestResult.Text = $"\u9519\u8BEF: {ex.Message}";
            TestResult.Foreground = new SolidColorBrush(Color.Parse("#ff4d4f"));
        }
    }
}
