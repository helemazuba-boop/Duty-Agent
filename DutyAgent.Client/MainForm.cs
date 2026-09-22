using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
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
        // 透明默认底色:页面加载前后由窗体 BackColor 兜底,杜绝导航间隙的白闪
        DefaultBackgroundColor = Color.Transparent,
    };
    private readonly Label _statusLabel = new()
    {
        Dock = DockStyle.Fill,
        Text = "正在启动 Duty-Agent...",
        TextAlign = ContentAlignment.MiddleCenter,
    };
    private readonly BackendProcessManager _backend = new();
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
    private bool _chromeDark;

    // ======== 自定义窗口铬:web 画标题条与三键,宿主管窗口消息 ========
    private bool _customChrome;
    private int _titlebarCssHeight = 44;   // web 标题条高度(CSS px)
    private int _controlsCssWidth = 138;   // 窗口三键总宽 3×46(CSS px)
    private bool _lastMaximized;

    private const int WM_NCCALCSIZE = 0x0083;
    private const int WM_NCHITTEST = 0x0084;
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int WM_NCLBUTTONUP = 0x00A2;
    private const int HTCLIENT = 1;
    private const int HTCAPTION = 2;
    private const int HTMAXBUTTON = 9;
    private const int HTLEFT = 10;
    private const int HTRIGHT = 11;
    private const int HTTOP = 12;
    private const int HTTOPLEFT = 13;
    private const int HTTOPRIGHT = 14;
    private const int HTBOTTOM = 15;
    private const int HTBOTTOMLEFT = 16;
    private const int HTBOTTOMRIGHT = 17;
    private const int SM_CXSIZEFRAME = 32;
    private const int SM_CXPADDEDBORDER = 92;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    // ======== 窗口镀铬跟随 web 主题(DWM) ========
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY = 19; // Win10 旧版属性号
    private const int DWMWA_BORDER_COLOR = 34;                   // Win11+
    private const int DWMWA_CAPTION_COLOR = 35;                  // Win11+
    private static readonly Color BgLight = Color.FromArgb(0xF6, 0xF7, 0xF9);   // tokens.json light.bg
    private static readonly Color BgDark = Color.FromArgb(0x0F, 0x11, 0x15);    // tokens.json dark.bg
    private static readonly Color TextLight = Color.FromArgb(0x11, 0x18, 0x27);
    private static readonly Color TextDark = Color.FromArgb(0xE6, 0xE8, 0xEB);
    private const uint CaptionColorLight = 0x00F9F7F6u; // COLORREF = 0x00BBGGRR
    private const uint CaptionColorDark = 0x0015110Fu;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref uint value, int size);

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

        // 应用名与全部操作都在 web 内;原生层只留托盘,标题栏由 web 绘制(可经配置回退原生)
        _customChrome = ReadCustomChromeEnabled();
        Controls.Add(_statusLabel);

        // 首帧前先按系统深浅排好窗口底色,web 起来后由 theme-sync 消息纠正
        _chromeDark = SystemPrefersDark();
        ApplyWindowChrome(_chromeDark, _chromeDark ? CaptionColorDark : CaptionColorLight);

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
        // DWM 属性要在窗口可见前设好,否则深色用户会先看到一帧亮色标题栏
        ApplyWindowChrome(_chromeDark, _chromeDark ? CaptionColorDark : CaptionColorLight);
        if (_initializeQueued)
        {
            return;
        }

        _initializeQueued = true;
        BeginInvoke(new Action(() => _ = InitializeAsync()));
    }

    // ======== 自定义窗口铬:去原生标题栏,保留边框/阴影/缩放/贴靠 ========

    /// <summary>
    /// WM_NCCALCSIZE 吃掉默认处理:客户区 = 整个窗口矩形,原生标题栏不再存在。
    /// 保留 WS_THICKFRAME/WS_CAPTION 样式,因此 DWM 阴影、缩放、Win+方向键贴靠、
    /// Alt+F4、Win11 圆角全部继续由系统提供;最大化时按边框厚度内缩,防止内容压出屏幕。
    /// </summary>
    private bool HandleNcCalcSize(ref Message m)
    {
        if (!_customChrome || (int)m.WParam != 1)
        {
            return false;
        }

        if (WindowState == FormWindowState.Maximized)
        {
            var frame = GetResizeFrameThickness();
            var rect = System.Runtime.InteropServices.Marshal.PtrToStructure<RECT>(m.LParam);
            rect.Left += frame;
            rect.Top += frame;
            rect.Right -= frame;
            rect.Bottom -= frame;
            System.Runtime.InteropServices.Marshal.StructureToPtr(rect, m.LParam, false);
        }

        m.Result = IntPtr.Zero;
        return true;
    }

    /// <summary>
    /// 自绘铬下的非客户命中测试:边缘缩放热区、标题条拖拽区(HTCAPTION)、
    /// 最大化按钮的 Win11 贴靠布局悬浮菜单(HTMAXBUTTON);三键区域返回 HTCLIENT 交给 web。
    /// </summary>
    private int? HitTestNonClient(IntPtr lParam)
    {
        if (!_customChrome)
        {
            return null;
        }

        // lParam = 屏幕坐标(负数按高位有符号数处理)
        int x = (short)((long)lParam & 0xFFFF);
        int y = (short)(((long)lParam >> 16) & 0xFFFF);
        int winX = x - Location.X;
        int winY = y - Location.Y;

        double scale = DeviceDpi / 96.0;

        // 缩放热区(最大化时系统不提供缩放)
        if (WindowState != FormWindowState.Maximized)
        {
            int frame = GetResizeFrameThickness();
            bool left = winX < frame;
            bool right = winX >= Width - frame;
            bool top = winY < frame;
            bool bottom = winY >= Height - frame;
            if (top && left) return HTTOPLEFT;
            if (top && right) return HTTOPRIGHT;
            if (bottom && left) return HTBOTTOMLEFT;
            if (bottom && right) return HTBOTTOMRIGHT;
            if (left) return HTLEFT;
            if (right) return HTRIGHT;
            if (top) return HTTOP;
            if (bottom) return HTBOTTOM;
        }

        int titlebarHeight = (int)Math.Round(_titlebarCssHeight * scale);
        int controlsWidth = (int)Math.Round(_controlsCssWidth * scale);
        if (winY >= titlebarHeight)
        {
            return null;
        }

        if (winX >= Width - controlsWidth)
        {
            // Win11:悬停最大化按钮(三键中间那颗)时交给系统弹贴靠布局菜单
            if (Environment.OSVersion.Version.Build >= 22000)
            {
                int buttonWidth = controlsWidth / 3;
                int maxLeft = Width - controlsWidth + buttonWidth;
                if (winX >= maxLeft && winX < maxLeft + buttonWidth)
                {
                    return HTMAXBUTTON;
                }
            }

            return HTCLIENT;
        }

        return HTCAPTION;
    }

    private int GetResizeFrameThickness()
    {
        // SystemAware DPI 下 GetSystemMetrics 已按系统 DPI 缩放,与窗口 DPI 一致
        return GetSystemMetrics(SM_CXSIZEFRAME) + GetSystemMetrics(SM_CXPADDEDBORDER);
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == FormWindowState.Maximized
            ? FormWindowState.Normal
            : FormWindowState.Maximized;
    }

    /// <summary>释放鼠标捕获后以指定 HT 值进入原生移动/缩放模态循环(拖拽、贴靠、双击全归系统)。
    /// lParam 必须携带光标屏幕物理坐标:Win10 的拖拽循环严格按该点计算锚点,传 0 会直接失效。
    /// web 传来的 screenX/Y 是 CSS 坐标,按 DPI 换算;缺省时退回 Cursor.Position。</summary>
    private void BeginNativeMove(JsonElement root, int hitTest)
    {
        if (!_customChrome || !IsHandleCreated)
        {
            return;
        }

        int x;
        int y;
        if (root.TryGetProperty("x", out var xe) && xe.ValueKind == JsonValueKind.Number
            && root.TryGetProperty("y", out var ye) && ye.ValueKind == JsonValueKind.Number)
        {
            double scale = DeviceDpi / 96.0;
            x = (int)Math.Round(xe.GetDouble() * scale);
            y = (int)Math.Round(ye.GetDouble() * scale);
        }
        else
        {
            var pt = Cursor.Position;
            x = pt.X;
            y = pt.Y;
        }

        var lParam = (IntPtr)((y << 16) | (x & 0xFFFF));
        ReleaseCapture();
        SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)hitTest, lParam);
    }

    /// <summary>窗口命令由 web 标题条三键发起;close 走既有 FormClosing 策略(询问/驻留/退出)。</summary>
    private void HandleWindowCommand(JsonElement root)
    {
        var command = root.TryGetProperty("command", out var cmdEl) && cmdEl.ValueKind == JsonValueKind.String
            ? cmdEl.GetString()
            : null;
        switch (command)
        {
            case "minimize":
                WindowState = FormWindowState.Minimized;
                break;
            case "maximize-toggle":
                ToggleMaximize();
                break;
            case "close":
                Close();
                break;
        }
    }

    private void HandleChromeGeometry(JsonElement root)
    {
        if (root.TryGetProperty("titlebarHeight", out var tbEl) && tbEl.ValueKind == JsonValueKind.Number)
        {
            _titlebarCssHeight = Math.Max(24, tbEl.GetInt32());
        }

        if (root.TryGetProperty("controlsWidth", out var cwEl) && cwEl.ValueKind == JsonValueKind.Number)
        {
            _controlsCssWidth = Math.Max(92, cwEl.GetInt32());
        }
    }

    private void PostToWeb(object payload)
    {
        try
        {
            _webView.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(payload));
        }
        catch
        {
            // WebView 未就绪或已关闭:无处投递,忽略
        }
    }

    private void PushHostInfo()
    {
        PostToWeb(new { type = "host-info", customChrome = _customChrome });
        PushWindowState();
    }

    private void PushWindowState()
    {
        var maximized = WindowState == FormWindowState.Maximized;
        if (maximized == _lastMaximized)
        {
            return;
        }

        _lastMaximized = maximized;
        PostToWeb(new { type = "window-state", maximized });
    }

    /// <summary>host-config 的 client_custom_chrome(默认开):自定义铬异常时一行配置回退原生标题栏。</summary>
    private bool ReadCustomChromeEnabled()
    {
        try
        {
            var path = Path.Combine(_backend.DataDirectory, "host-config.json");
            if (File.Exists(path))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
                var root = document.RootElement;
                return !(root.TryGetProperty("client_custom_chrome", out var el)
                    && el.ValueKind == JsonValueKind.False);
            }
        }
        catch
        {
        }

        return true;
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_NCCALCSIZE:
                if (HandleNcCalcSize(ref m))
                {
                    return;
                }

                break;
            case WM_NCHITTEST:
            {
                var hit = HitTestNonClient(m.LParam);
                if (hit.HasValue)
                {
                    m.Result = (IntPtr)hit.Value;
                    return;
                }

                break;
            }
            case WM_NCLBUTTONDOWN:
                // 贴靠布局按钮的点击由我们接管(HTMAXBUTTON 区域不进 web)
                if (_customChrome && (int)m.WParam == HTMAXBUTTON)
                {
                    return;
                }

                break;
            case WM_NCLBUTTONUP:
                if (_customChrome && (int)m.WParam == HTMAXBUTTON)
                {
                    ToggleMaximize();
                    return;
                }

                break;
        }

        base.WndProc(ref m);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (IsHandleCreated && _webViewReady)
        {
            PushWindowState();
        }
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

    private async Task InitializeAsync()
    {
        try
        {
            await _backend.StartAsync(CancellationToken.None).ConfigureAwait(true);
            _backend.BackendRestarted += OnBackendRestarted;
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

    /// <summary>
    /// 后端 watchdog 自动重启成功后的恢复：旧 token/端口已失效，
    /// 用新的 WebAppUrl（携带新 token）重新导航 WebView，并重启通知流订阅。
    /// </summary>
    private async void OnBackendRestarted(object? sender, EventArgs e)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => OnBackendRestarted(sender, e));
            return;
        }

        if (_webViewReady)
        {
            try
            {
                _webView.Source = new Uri(_backend.WebAppUrl);
            }
            catch
            {
                // WebView 处于异常状态时忽略导航，下次 RestoreWindow 会重新加载。
            }
        }

        if (_notificationStream is not null)
        {
            try
            {
                await _notificationStream.StopAsync().ConfigureAwait(true);
            }
            catch
            {
            }

            _notificationStream.Start();
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
            // DevTools 默认关闭：仅调试器附加或显式设置 DUTY_WEBVIEW_DEVTOOLS=1 时开放，
            // 避免最终用户（学生/值日生）误触右键检查元素。
            _webView.CoreWebView2.Settings.AreDevToolsEnabled =
                Debugger.IsAttached
                || string.Equals(
                    Environment.GetEnvironmentVariable("DUTY_WEBVIEW_DEVTOOLS"),
                    "1",
                    StringComparison.OrdinalIgnoreCase);
            _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            // 网页刷新(F5)会丢掉 web 侧状态:每次导航完成后重推铬配置与窗口状态
            _webView.CoreWebView2.NavigationCompleted += (_, _) => PushHostInfo();
            // 触摸屏兼容:启用非客户区支持后,-webkit-app-region:drag 由 WebView2 原生处理
            // (触摸/鼠标/双击最大化/系统菜单);旧运行时无此能力时静默降级到消息桥回退
            try
            {
                _webView.CoreWebView2.Settings.IsNonClientRegionSupportEnabled = true;
            }
            catch
            {
                // 旧 WebView2 Runtime 不具备该能力:鼠标走消息桥回退,触摸拖拽不可用
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("WebView2 初始化失败。请确认系统已安装 Microsoft Edge WebView2 Runtime。", ex);
        }
    }

    // ======== 窗口镀铬跟随 web 主题 ========

    /// <summary>web 侧上报的消息:主题同步 / 插件安装 / 铬几何 / 窗口命令。</summary>
    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var typeEl)
                || typeEl.GetString() is not string messageType)
            {
                return;
            }

            switch (messageType)
            {
                case "theme-sync":
                    HandleThemeSync(root);
                    break;
                case "install-bridge":
                    HandleInstallBridge();
                    break;
                case "chrome-geometry":
                    HandleChromeGeometry(root);
                    break;
                case "window-command":
                    HandleWindowCommand(root);
                    break;
                case "get-host-info":
                    PushHostInfo();
                    break;
                case "drag-window":
                    // 鼠标回退通道(触摸走 WebView2 的 app-region 原生处理)
                    BeginNativeMove(root, HTCAPTION);
                    break;
                case "resize-window":
                    // 同上:边缘热区按下后拉起原生缩放循环;最大化时忽略
                    if (WindowState != FormWindowState.Maximized
                        && root.TryGetProperty("hit", out var hitEl)
                        && hitEl.ValueKind == JsonValueKind.Number)
                    {
                        BeginNativeMove(root, hitEl.GetInt32());
                    }

                    break;
            }
        }
        catch (JsonException)
        {
            // 非 JSON 消息(如纯字符串)直接忽略
        }
    }

    private void HandleThemeSync(JsonElement root)
    {
        var dark = root.TryGetProperty("dark", out var darkEl) && darkEl.ValueKind == JsonValueKind.True;
        if (!root.TryGetProperty("bg", out var bgEl)
            || bgEl.ValueKind != JsonValueKind.String
            || !TryParseColorRef(bgEl.GetString(), out var captionColor))
        {
            captionColor = dark ? CaptionColorDark : CaptionColorLight;
        }

        _chromeDark = dark;
        ApplyWindowChrome(dark, captionColor);
    }

    /// <summary>安装耗时且涉及文件 IO,放线程池;结果经 PostWebMessageAsJson 回传给设置页 toast。</summary>
    private void HandleInstallBridge()
    {
        _ = Task.Run(() =>
        {
            var result = BridgeInstaller.Install();
            BeginInvoke(() =>
            {
                try
                {
                    _webView.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new
                    {
                        type = "install-bridge-result",
                        ok = result.Success,
                        message = result.Message,
                    }));
                }
                catch
                {
                    // WebView 已关闭或未就绪:结果无处投递,忽略
                }
            });
        });
    }

    /// <summary>
    /// 把深浅主题落到窗口:标题栏/描边用 DWM 属性上色(旧系统忽略失败),
    /// 窗体与菜单条配色同步,避免深色模式下残留一条浅色铬带。
    /// </summary>
    private void ApplyWindowChrome(bool dark, uint captionColor)
    {
        if (IsDisposed)
        {
            return;
        }

        var bg = dark ? BgDark : BgLight;
        var text = dark ? TextDark : TextLight;
        BackColor = bg;
        _statusLabel.BackColor = bg;
        _statusLabel.ForeColor = text;

        if (!IsHandleCreated)
        {
            return;
        }

        var useDark = dark ? 1 : 0;
        if (DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY, ref useDark, sizeof(int));
        }

        DwmSetWindowAttribute(Handle, DWMWA_CAPTION_COLOR, ref captionColor, sizeof(uint));
        var borderColor = captionColor; // 外圈描边与标题栏同色,去掉深色模式下的亮色边框
        DwmSetWindowAttribute(Handle, DWMWA_BORDER_COLOR, ref borderColor, sizeof(uint));
    }

    /// <summary>#RRGGBB → COLORREF(0x00BBGGRR)。</summary>
    private static bool TryParseColorRef(string? hex, out uint colorref)
    {
        colorref = 0;
        if (hex is null)
        {
            return false;
        }

        var span = hex.Trim();
        if (span.Length == 7 && span[0] == '#'
            && byte.TryParse(span.AsSpan(1, 2), System.Globalization.NumberStyles.HexNumber, null, out var r)
            && byte.TryParse(span.AsSpan(3, 2), System.Globalization.NumberStyles.HexNumber, null, out var g)
            && byte.TryParse(span.AsSpan(5, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
        {
            colorref = (uint)((b << 16) | (g << 8) | r);
            return true;
        }

        return false;
    }

    /// <summary>web 首帧消息到达前,用系统深浅设置兜底(与 useTheme 的 auto 默认一致)。</summary>
    private static bool SystemPrefersDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch
        {
            return false;
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
