using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DutyAgent.Client;

internal sealed class MainForm : Form
{
    private enum CloseChoice
    {
        Tray,
        Exit,
        Cancel,
    }

    private readonly WebView2 _webView = new()
    {
        Dock = DockStyle.Fill,
    };
    private readonly Label _statusLabel = new()
    {
        Dock = DockStyle.Fill,
        Text = "正在启动 Duty-Agent...",
        TextAlign = ContentAlignment.MiddleCenter,
    };
    private readonly BackendProcessManager _backend = new();
    private readonly MenuStrip _menu = new() { Dock = DockStyle.Top };
    private readonly NotifyIcon _trayIcon;
    private readonly System.Windows.Forms.Timer _lifecycleDebounce = new() { Interval = 400 };
    private readonly RegisteredWaitHandle? _wakeRegistration;
    private DesktopNotificationService? _notificationService;
    private NotificationStreamClient? _notificationStream;
    private FileSystemWatcher? _hostConfigWatcher;
    private bool _shutdownStarted;
    private bool _initializeQueued;
    private bool _webViewReady;
    private bool _webViewInitializing;
    private bool _allowVisible;
    private bool _exitRequested;
    private bool _trayBalloonShown;
    private string _closeAction = "ask";

    public MainForm(bool startSilent, EventWaitHandle wakeEvent)
    {
        _allowVisible = !startSilent;

        Text = "Duty-Agent";
        Width = 1280;
        Height = 800;
        MinimumSize = new Size(900, 600);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = LoadIconOrDefault();
        var baseFont = SystemFonts.MessageBoxFont ?? Font;
        _statusLabel.Font = new Font(baseFont.FontFamily, 12f);

        BuildMenu();
        Controls.Add(_statusLabel);
        Controls.Add(_menu);
        _trayIcon = CreateTrayIcon();
        _lifecycleDebounce.Tick += (_, _) =>
        {
            _lifecycleDebounce.Stop();
            ApplyLifecycleSettings();
        };
        FormClosing += OnFormClosing;

        // 单实例唤醒：第二个实例启动时只发信号，这里把驻留托盘的窗口拉回前台。
        _wakeRegistration = ThreadPool.RegisterWaitForSingleObject(
            wakeEvent,
            OnWakeSignal,
            state: null,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    /// <summary>
    /// --silent 启动时保持窗口隐藏（仅托盘），但强制创建句柄，
    /// 使消息循环与初始化（OnHandleCreated）照常进行。
    /// </summary>
    protected override void SetVisibleCore(bool value)
    {
        if (!_allowVisible)
        {
            value = false;
            if (!IsHandleCreated)
            {
                CreateHandle();
            }
        }

        base.SetVisibleCore(value);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (_initializeQueued)
        {
            return;
        }

        _initializeQueued = true;
        BeginInvoke(new Action(() => _ = InitializeAsync()));
    }

    private void BuildMenu()
    {
        var toolsMenu = new ToolStripMenuItem("工具(&T)");
        var installBridge = new ToolStripMenuItem("安装 ClassIsland 插件");
        installBridge.Click += OnInstallBridgeClicked;
        toolsMenu.DropDownItems.Add(installBridge);
        _menu.Items.Add(toolsMenu);
    }

    private NotifyIcon CreateTrayIcon()
    {
        var menu = new ContextMenuStrip();
        var openItem = new ToolStripMenuItem("打开主界面");
        openItem.Click += (_, _) => RestoreWindow();
        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += async (_, _) =>
        {
            _exitRequested = true;
            await ShutdownAndCloseAsync().ConfigureAwait(true);
        };
        menu.Items.Add(openItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        var icon = new NotifyIcon
        {
            Text = "Duty-Agent",
            Icon = Icon ?? SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = false,
        };
        icon.DoubleClick += (_, _) => RestoreWindow();
        return icon;
    }

    private void OnInstallBridgeClicked(object? sender, EventArgs e)
    {
        var result = BridgeInstaller.Install();
        var icon = result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning;
        MessageBox.Show(this, result.Message, "安装 ClassIsland 插件", MessageBoxButtons.OK, icon);
    }

    private async Task InitializeAsync()
    {
        try
        {
            await _backend.StartAsync(CancellationToken.None).ConfigureAwait(true);
            _notificationService = new DesktopNotificationService(this);
            _notificationService.Initialize();
            _notificationService.NotificationActivated += OnNotificationActivated;
            _notificationStream = new NotificationStreamClient(_backend, _notificationService);
            _notificationStream.Start();

            ApplyLifecycleSettings();
            StartHostConfigWatcher();
            _trayIcon.Visible = true;

            if (_allowVisible)
            {
                await EnsureWebViewLoadedAsync().ConfigureAwait(true);
            }
            else
            {
                ShowTrayBalloonOnce(
                    "Duty-Agent 正在后台运行",
                    "自动排班与提醒已就绪，双击托盘图标打开主界面。");
            }
        }
        catch (Exception ex)
        {
            await ShowFatalErrorAsync("启动失败", ex).ConfigureAwait(true);
        }
    }

    /// <summary>WebView2 延迟到窗口首次可见时才初始化：静默自启不付这笔成本。</summary>
    private async Task EnsureWebViewLoadedAsync()
    {
        if (_webViewReady || _webViewInitializing)
        {
            return;
        }

        _webViewInitializing = true;
        try
        {
            await InitializeWebViewAsync().ConfigureAwait(true);
            Controls.Remove(_statusLabel);
            Controls.Add(_webView);
            _webView.BringToFront();
            _webView.Source = new Uri(_backend.WebAppUrl);
            _webViewReady = true;
        }
        finally
        {
            _webViewInitializing = false;
        }
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            var userDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DutyAgent",
                "WebView2");
            Directory.CreateDirectory(userDataPath);

            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataPath).ConfigureAwait(true);
            await _webView.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            _webView.CoreWebView2.Settings.AreDevToolsEnabled = true;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("WebView2 初始化失败。请确认系统已安装 Microsoft Edge WebView2 Runtime。", ex);
        }
    }

    private void RestoreWindow()
    {
        if (InvokeRequired)
        {
            BeginInvoke(RestoreWindow);
            return;
        }

        _allowVisible = true;
        Show();
        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }

        Activate();
        _ = EnsureWebViewLoadedAsync();
    }

    private void OnWakeSignal(object? state, bool timedOut)
    {
        try
        {
            BeginInvoke(RestoreWindow);
        }
        catch
        {
            // 句柄尚未创建或窗口已销毁：忽略唤醒信号。
        }
    }

    private void HideToTray()
    {
        Hide();
        ShowTrayBalloonOnce(
            "Duty-Agent 仍在后台运行",
            "自动排班、值日提醒与离线兜底继续生效，双击托盘图标可重新打开。");
    }

    private void ShowTrayBalloonOnce(string title, string text)
    {
        if (_trayBalloonShown)
        {
            return;
        }

        _trayBalloonShown = true;
        try
        {
            _trayIcon.ShowBalloonTip(4000, title, text, ToolTipIcon.Info);
        }
        catch
        {
        }
    }

    private async void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_shutdownStarted)
        {
            // 第二次 Close()（关停完成后）直接放行。
            return;
        }

        e.Cancel = true;

        // Let the OS/session tear us down cleanly; only intercept ordinary window
        // closes (X button, Alt+F4, external WM_CLOSE) to apply the tray policy.
        var forcedClose = e.CloseReason is CloseReason.WindowsShutDown
            or CloseReason.TaskManagerClosing
            or CloseReason.ApplicationExitCall;

        if (!_exitRequested && !forcedClose)
        {
            if (_closeAction == "tray")
            {
                HideToTray();
                return;
            }

            if (_closeAction == "ask")
            {
                var (choice, remember) = ShowCloseChoiceDialog();
                if (choice == CloseChoice.Cancel)
                {
                    return;
                }

                if (remember)
                {
                    _closeAction = choice == CloseChoice.Tray ? "tray" : "exit";
                    _ = PersistCloseActionAsync(_closeAction);
                }

                if (choice == CloseChoice.Tray)
                {
                    HideToTray();
                    return;
                }

                _exitRequested = true;
            }
        }

        await ShutdownAndCloseAsync().ConfigureAwait(true);
    }

    private (CloseChoice Choice, bool Remember) ShowCloseChoiceDialog()
    {
        var verification = new TaskDialogVerificationCheckBox("记住我的选择（可在设置页“系统与自启”修改）");
        var trayButton = new TaskDialogButton("驻留后台");
        var exitButton = new TaskDialogButton("退出程序");
        var page = new TaskDialogPage
        {
            Caption = "Duty-Agent",
            Heading = "关闭主窗口？",
            Text = "驻留后台：窗口隐藏到托盘，自动排班、值日提醒与离线兜底继续运行。\n退出程序：后端一并停止，定时任务不再执行。",
            Icon = TaskDialogIcon.Information,
            Verification = verification,
            AllowCancel = true,
        };
        page.Buttons.Add(trayButton);
        page.Buttons.Add(exitButton);

        var result = TaskDialog.ShowDialog(this, page);
        if (result == trayButton)
        {
            return (CloseChoice.Tray, verification.Checked);
        }

        if (result == exitButton)
        {
            return (CloseChoice.Exit, verification.Checked);
        }

        return (CloseChoice.Cancel, false);
    }

    private async Task ShutdownAndCloseAsync()
    {
        if (_shutdownStarted)
        {
            return;
        }

        _shutdownStarted = true;
        _trayIcon.Visible = false;
        RestoreWindowForShutdownStatus();
        Enabled = false;
        _statusLabel.Text = "正在关闭后端服务...";
        if (!Controls.Contains(_statusLabel))
        {
            Controls.Add(_statusLabel);
            _statusLabel.BringToFront();
        }

        if (_notificationStream is not null)
        {
            await _notificationStream.StopAsync().ConfigureAwait(true);
        }

        await _backend.StopAsync().ConfigureAwait(true);
        Close();
    }

    private void RestoreWindowForShutdownStatus()
    {
        // 从托盘退出时窗口是隐藏的：短暂显示状态页避免"程序无响应"的观感。
        if (!Visible)
        {
            _allowVisible = true;
            Show();
        }
    }

    // ======== 宿主配置（client_auto_start / client_close_action）观察与执行 ========

    private void ApplyLifecycleSettings()
    {
        var (autoStart, closeAction) = ReadLifecycleSettings();
        _closeAction = closeAction;
        AutoStartManager.Apply(autoStart);
    }

    private (bool AutoStart, string CloseAction) ReadLifecycleSettings()
    {
        try
        {
            var path = Path.Combine(_backend.DataDirectory, "host-config.json");
            if (File.Exists(path))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
                var root = document.RootElement;

                var autoStart = !(root.TryGetProperty("client_auto_start", out var autoStartElement)
                    && autoStartElement.ValueKind == JsonValueKind.False);

                var action = root.TryGetProperty("client_close_action", out var actionElement)
                    && actionElement.ValueKind == JsonValueKind.String
                    ? (actionElement.GetString() ?? "ask").Trim().ToLowerInvariant()
                    : "ask";
                if (action != "tray" && action != "exit")
                {
                    action = "ask";
                }

                return (autoStart, action);
            }
        }
        catch
        {
        }

        return (true, "ask");
    }

    private void StartHostConfigWatcher()
    {
        try
        {
            Directory.CreateDirectory(_backend.DataDirectory);
            _hostConfigWatcher = new FileSystemWatcher(_backend.DataDirectory, "host-config.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            };
            _hostConfigWatcher.Changed += (_, _) => ScheduleLifecycleRefresh();
            _hostConfigWatcher.Created += (_, _) => ScheduleLifecycleRefresh();
            _hostConfigWatcher.Renamed += (_, _) => ScheduleLifecycleRefresh();
            _hostConfigWatcher.EnableRaisingEvents = true;
        }
        catch
        {
            // 观察失败只影响"改动即时生效"，下次启动仍会重读配置。
        }
    }

    private void ScheduleLifecycleRefresh()
    {
        try
        {
            BeginInvoke(() =>
            {
                _lifecycleDebounce.Stop();
                _lifecycleDebounce.Start();
            });
        }
        catch
        {
        }
    }

    private async Task PersistCloseActionAsync(string action)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            if (!string.IsNullOrWhiteSpace(_backend.Token))
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _backend.Token);
            }

            using var payload = new StringContent(
                JsonSerializer.Serialize(new Dictionary<string, string> { ["client_close_action"] = action }),
                Encoding.UTF8,
                "application/json");
            await client.PatchAsync($"{_backend.BaseUrl}/api/v1/notifications/settings", payload).ConfigureAwait(false);
        }
        catch
        {
            // 后端不可达时仅本次会话生效；下次由宿主配置纠正。
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _wakeRegistration?.Unregister(null);
            _lifecycleDebounce.Dispose();
            _hostConfigWatcher?.Dispose();
            _trayIcon.Dispose();
            _webView.Dispose();
            _notificationStream?.Dispose();
            _notificationService?.Dispose();
            _backend.Dispose();
        }

        base.Dispose(disposing);
    }

    private static Icon? LoadIconOrDefault()
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "icon.png");
            if (File.Exists(iconPath))
            {
                using var bitmap = new Bitmap(iconPath);
                return Icon.FromHandle(bitmap.GetHicon());
            }
        }
        catch
        {
        }

        return null;
    }

    private async void OnNotificationActivated(object? sender, string route)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => OnNotificationActivated(sender, route));
            return;
        }

        RestoreWindow();
        await EnsureWebViewLoadedAsync().ConfigureAwait(true);

        var targetUrl = _backend.BuildWebAppUrl(route);
        _webView.CoreWebView2?.Navigate(targetUrl);
    }

    private async Task ShowFatalErrorAsync(string title, Exception exception)
    {
        var message = $"{exception.Message}\n\n{exception.GetBaseException().Message}";
        MessageBox.Show(this, message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        _shutdownStarted = true;
        _exitRequested = true;
        _trayIcon.Visible = false;
        await _backend.StopAsync().ConfigureAwait(true);
        Close();
    }
}
