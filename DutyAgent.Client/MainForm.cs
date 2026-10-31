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
    /// <summary>
    /// 没配置过时的关窗行为：隐藏到托盘。
    ///
    /// 以前默认是 "ask"——每次点 X 都弹一个模态框问"驻留还是退出"，打断感强，
    /// 而且第一次见面就问一个用户还没法判断的问题。驻留后台才是这个程序的常态
    /// （自动排班、提醒都靠后台进程），真退出走托盘菜单"退出"或设置页。
    /// </summary>
    private const string DefaultCloseAction = "tray";

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
    private readonly SplashView _splash = new();
    private readonly BackendProcessManager _backend = new();
    private readonly NotifyIcon _trayIcon;
    private readonly System.Windows.Forms.Timer _lifecycleDebounce = new() { Interval = 400 };
    private readonly List<RegisteredWaitHandle> _wakeRegistrations = new();

    // 启动流程的取消源：关闭窗口时先取消它，让 InitializeAsync 就地停下，
    // 而不是把它正在使用的后端进程从背后拆掉（那会让引导失败被当成"启动失败"）。
    private readonly CancellationTokenSource _startupCts = new();
    private DesktopNotificationService? _notificationService;
    private NotificationStreamClient? _notificationStream;
    private FileSystemWatcher? _hostConfigWatcher;
    private bool _shutdownStarted;
    private bool _initializeQueued;
    private bool _webViewReady;
    private bool _webViewPrepared;
    // 缺 WebView2 运行时：启动页停在错误态，别再重试初始化（RestoreWindow 会重复触发）。
    private bool _webViewUnavailable;
    private bool _webViewInitializing;
    private bool _allowVisible;
    private bool _exitRequested;
    private bool _trayBalloonShown;
    private string _closeAction = DefaultCloseAction;
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
    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_MOVE = 0xF010;
    private const int SC_SIZE = 0xF000;
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
    private static extern IntPtr SetCapture(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

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

    public MainForm(bool startSilent, EventWaitHandle wakeEvent, EventWaitHandle dataWakeEvent)
    {
        _allowVisible = !startSilent;

        Text = "Duty-Agent";
        Width = 1280;
        Height = 800;
        MinimumSize = new Size(900, 600);
        StartPosition = FormStartPosition.CenterScreen;
        // 上次的窗口大小/位置（含最大化）优先于默认的 1280×800 居中。
        WindowPlacement.Apply(this);
        Icon = LoadIconOrDefault();

        // 应用名与全部操作都在 web 内;原生层只留托盘,标题栏由 web 绘制(可经配置回退原生)
        _customChrome = ReadCustomChromeEnabled();

        // 启动页：自绘 44px 标题条 + 品牌块。三键是真的能用的（见 SplashView）。
        // client_custom_chrome=false 时窗口有原生标题栏，启动页就别再画一条。
        _splash.ShowTitleBar = _customChrome;
        _splash.Version = Application.ProductVersion;
        _splash.MinimizeRequested += (_, _) => WindowState = FormWindowState.Minimized;
        _splash.MaximizeRequested += (_, _) => ToggleMaximize();
        _splash.CloseRequested += (_, _) => Close();
        Controls.Add(_splash);

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
        // 两个事件：同形态唤醒（带 PFN 作用域）与跨形态数据目录唤醒（不带作用域，
        // zip 版 ↔ 商店版互相唤醒后由 owner 继续运行）。
        foreach (var wakeHandle in new[] { wakeEvent, dataWakeEvent })
        {
            _wakeRegistrations.Add(ThreadPool.RegisterWaitForSingleObject(
                wakeHandle,
                OnWakeSignal,
                state: null,
                Timeout.Infinite,
                executeOnlyOnce: false));
        }
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

    /// <summary>释放鼠标捕获后以指定 HT 值进入原生缩放模态循环(边缘拖拽缩放由系统处理)。</summary>
    private void BeginNativeResize(int hitTest)
    {
        if (!_customChrome || !IsHandleCreated)
        {
            return;
        }

        ReleaseCapture();
        SetCapture(Handle);
        SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)hitTest, IntPtr.Zero);
    }

    /// <summary>
    /// 拖拽移动兜底(对齐 tao 的 drag_window):坐标取 GetCursorPos 的 OS 实时光标,
    /// 不做 web 坐标换算;ReleaseCapture 后 PostMessage 异步进入移动循环,
    /// 避免在 WebMessageReceived 回调内 SendMessage 重入消息泵。原生 app-region
    /// 路径生效时页面收不到 pointerdown,本兜底不会被触发。
    /// </summary>
    private void BeginNativeMove()
    {
        if (!_customChrome || !IsHandleCreated)
        {
            return;
        }

        ReleaseCapture();
        var p = Cursor.Position;
        PostMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION,
            (IntPtr)(((p.Y & 0xFFFF) << 16) | (p.X & 0xFFFF)));
    }

    /// <summary>触摸屏专用：通过 WM_SYSCOMMAND + SC_SIZE 进入原生缩放循环。</summary>
    private void BeginNativeResizeTouch(int hitTest)
    {
        if (!_customChrome || !IsHandleCreated)
        {
            return;
        }

        ReleaseCapture();
        SetCapture(Handle);
        int sc = hitTest == HTCAPTION ? SC_MOVE : SC_SIZE;
        SendMessage(Handle, WM_SYSCOMMAND, (IntPtr)(sc | hitTest), IntPtr.Zero);
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
        var cancellationToken = _startupCts.Token;
        ClientLog.Info("初始化流程开始。");

        // WebView2 的初始化（建用户数据目录 + 环境 + CoreWebView2）与后端启动互不依赖。
        // 以前是串行：后端 2-6s 等完，才开始 WebView 的 1-2s，两段等待直接相加。
        // 这里先把它挂起来跑，等后端就绪、拿到端口和 token 之后再挂控件 + 导航。
        Task? webViewTask = null;
        if (_allowVisible)
        {
            webViewTask = PrepareWebViewAsync();
            // 后端先失败时可能没人 await 它：挂个观察者，别让异常在 GC 时炸成
            // UnobservedTaskException。
            _ = webViewTask.ContinueWith(
                t => ClientLog.Warn($"WebView 预初始化失败：{t.Exception?.GetBaseException().Message}"),
                TaskContinuationOptions.OnlyOnFaulted);
        }

        try
        {
            // 首次启动（还没有名单）在 Windows 上要额外付一次杀毒扫描 + 冷缓存的钱，
            // 实测 5 秒 vs 3 秒。这行小字专门解释那次"怎么这么久"。
            var firstRun = !File.Exists(Path.Combine(_backend.DataDirectory, "roster.csv"));
            _splash.SetStage("正在启动排班引擎…", firstRun ? "首次启动需要多等几秒" : null);

            await _backend.StartAsync(cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();

            _backend.BackendRestarted += OnBackendRestarted;
            _notificationService = new DesktopNotificationService(this);
            _notificationService.Initialize();
            _notificationService.NotificationActivated += OnNotificationActivated;
            _notificationStream = new NotificationStreamClient(_backend, _notificationService);
            _notificationStream.Start();

            ApplyLifecycleSettings();
            StartHostConfigWatcher();
            cancellationToken.ThrowIfCancellationRequested();
            _trayIcon.Visible = true;

            if (_allowVisible)
            {
                // 到这一步 WebView 通常已经预热完了；没完就等它，然后才导航。
                if (webViewTask is not null)
                {
                    await webViewTask.ConfigureAwait(true);
                }

                cancellationToken.ThrowIfCancellationRequested();
                _splash.SetStage("正在加载界面…");
                AttachWebView();
            }
            else
            {
                ShowTrayBalloonOnce(
                    "Duty-Agent 正在后台运行",
                    "自动排班与提醒已就绪，双击托盘图标打开主界面。");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 用户在启动过程中关掉了窗口：这是正常退出路径，不是故障，
            // 既不该弹"启动失败"，也不该在日志里留一条 error。
            ClientLog.Info("启动过程中收到退出请求，初始化已中止。");
        }
        catch (WebViewRuntimeMissingException ex)
        {
            // 缺运行时不该整程序退出：后端、托盘、自动排班、提醒、ClassIsland 联动
            // 都不依赖主界面，把它们一起关掉只会让用户以为"装了个坏软件"。
            ClientLog.Warn("主界面未加载：缺少 WebView2 Runtime。后台继续运行。");
            ShowWebViewRuntimeMissing(ex);
        }
        catch (Exception ex)
        {
            if (_shutdownStarted)
            {
                // 关停把后端进程收走了，启动流程必然收到异常；此时窗口正在关闭，
                // 再弹一个"启动失败"只会让用户以为自己点错了按钮。
                ClientLog.Warn($"启动流程在退出过程中失败，已忽略：{ex.Message}");
                return;
            }

            // 启动页先切到错误态，别让它停在"正在启动排班引擎…"上——
            // 弹窗后面那块地方会一直亮着，看起来像还在等。
            _splash.IsError = true;
            _splash.SetStage("排班引擎启动失败");
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

        if (_shutdownStarted)
        {
            // 关停途中 watchdog 恰好拉起了一个新后端：不要再导航/重订阅，
            // 收尾的 StopAsync 会把它收掉。
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

    /// <summary>
    /// 预热 WebView2（环境 + CoreWebView2），但不挂到窗口、不导航。
    /// 与后端启动并行执行；后端就绪后才由 AttachWebView 接管。
    /// </summary>
    private async Task PrepareWebViewAsync()
    {
        if (_webViewPrepared || _webViewInitializing || _webViewUnavailable || _shutdownStarted)
        {
            return;
        }

        _webViewInitializing = true;
        try
        {
            await InitializeWebViewAsync().ConfigureAwait(true);
            _webViewPrepared = true;
        }
        finally
        {
            _webViewInitializing = false;
        }
    }

    /// <summary>把预热好的 WebView 挂进窗口并导航到后端地址。</summary>
    private void AttachWebView()
    {
        if (_webViewReady || !_webViewPrepared || _shutdownStarted)
        {
            return;
        }

        // WebView2 是独立子窗口，挂上来就会盖住 windowless 的启动页，所以顺序是
        // 「先挂 WebView → 再隐藏启动页」，避免出现一帧两边都空着的窗口。
        Controls.Add(_webView);
        _webView.BringToFront();
        _splash.Visible = false;
        _webView.Source = new Uri(_backend.WebAppUrl);
        _webViewReady = true;
    }

    /// <summary>WebView2 延迟到窗口首次可见时才初始化：静默自启不付这笔成本。</summary>
    private async Task EnsureWebViewLoadedAsync()
    {
        if (_webViewReady || _shutdownStarted)
        {
            return;
        }

        await PrepareWebViewAsync().ConfigureAwait(true);
        AttachWebView();
    }

    private async Task InitializeWebViewAsync()
    {
        // 运行时缺失必须单独判定：它决定上层弹"去装运行时"还是"启动失败"。
        // 不在这里探测的话，EnsureCoreWebView2Async 抛的是无上下文的 COM 异常。
        if (!WebViewRuntime.TryGetVersion(out var runtimeVersion))
        {
            ClientLog.Warn("未检测到 WebView2 Runtime，主界面无法初始化。");
            throw new WebViewRuntimeMissingException();
        }

        ClientLog.Info($"WebView2 Runtime 版本: {runtimeVersion}");

        try
        {
            string userDataPath;
            if (PackageIdentityHelper.IsPackaged)
            {
                // 包身份下用平台托管的 LocalFolder：卸载时随包一起清理，
                // 不会在 %LOCALAPPDATA% 留下孤儿缓存目录。
                userDataPath = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
            }
            else
            {
                userDataPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DutyAgent",
                    "WebView2");
            }
            Directory.CreateDirectory(userDataPath);

            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataPath).ConfigureAwait(true);
            await _webView.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            // 启用 app-region CSS 支持：让 WebView2 内部处理窗口拖拽/缩放，
            // 避免 Web 层 postMessage → C# SendMessage 路径下的鼠标捕获竞争。
            // 该属性在下次导航后生效；页面需配合 CSS app-region: drag/no-drag 使用。
            _webView.CoreWebView2.Settings.IsNonClientRegionSupportEnabled = true;
            // 关闭触摸滑动导航手势：桌面应用不需要浏览器前进/后退手势，
            // 且该手势会吞掉侧栏区域的 pointermove，干扰自绘铬下的窗口拖拽。
            _webView.CoreWebView2.Settings.IsSwipeNavigationEnabled = false;
            // DevTools 默认关闭：仅调试器附加或显式设置 DUTY_WEBVIEW_DEVTOOLS=1 时开放，
            // 避免最终用户（学生/值日生）误触右键检查元素。
            _webView.CoreWebView2.Settings.AreDevToolsEnabled =
                Debugger.IsAttached
                || string.Equals(
                    Environment.GetEnvironmentVariable("DUTY_WEBVIEW_DEVTOOLS"),
                    "1",
                    StringComparison.OrdinalIgnoreCase);
            _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            // 只允许留在本地后端页面上：界面是同源网页，任何外部导航都意味着
            // "别的站点拿到了宿主消息通道"，必须拦掉。外链统一走 open-external。
            _webView.CoreWebView2.NavigationStarting += (_, args) =>
            {
                if (!IsLocalAppNavigation(args.Uri))
                {
                    args.Cancel = true;
                    ClientLog.Warn($"拦截非本地导航: {args.Uri}");
                }
            };
            // 网页刷新(F5)会丢掉 web 侧状态:每次导航完成后重推铬配置与窗口状态
            _webView.CoreWebView2.NavigationCompleted += (_, _) => PushHostInfo();
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
                case "open-external":
                    HandleOpenExternal(root);
                    break;
                case "drag-window":
                    BeginNativeMove();
                    break;
                case "resize-window":
                {
                    if (WindowState != FormWindowState.Maximized
                        && root.TryGetProperty("hit", out var hitEl)
                        && hitEl.ValueKind == JsonValueKind.Number)
                    {
                        BeginNativeResize(hitEl.GetInt32());
                    }

                    break;
                }
                case "resize-window-touch":
                {
                    if (WindowState != FormWindowState.Maximized
                        && root.TryGetProperty("hit", out var hitEl2)
                        && hitEl2.ValueKind == JsonValueKind.Number)
                    {
                        BeginNativeResizeTouch(hitEl2.GetInt32());
                    }

                    break;
                }
            }
        }
        catch (JsonException)
        {
            // 非 JSON 消息(如纯字符串)直接忽略
        }
    }

    /// <summary>
    /// 是否允许 WebView 导航到该地址。只放行回环地址（本地后端）与 about: 空白页，
    /// 其余一律取消——界面不需要访问任何外部站点。
    /// </summary>
    internal static bool IsLocalAppNavigation(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)
            || !Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        if (parsed.Scheme == "about")
        {
            return true;
        }

        return parsed.Scheme == Uri.UriSchemeHttp && parsed.IsLoopback;
    }

    /// <summary>
    /// 打开外部链接（隐私政策、支持邮箱、反馈入口）。
    /// 只放行 http/https/mailto：web 侧内容是本地打包的，但这条通道等价于"让页面调起
    /// 系统程序"，白名单协议是必须的，否则任何脚本注入都能变成任意 URI 执行。
    /// </summary>
    private void HandleOpenExternal(JsonElement root)
    {
        if (!root.TryGetProperty("url", out var urlEl)
            || urlEl.ValueKind != JsonValueKind.String
            || urlEl.GetString() is not string url
            || string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return;
        }

        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeMailto)
        {
            ClientLog.Warn($"拒绝打开非 http(s)/mailto 链接: {uri.Scheme}");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            ClientLog.Info($"已打开外部链接: {uri.Host}");
        }
        catch (Exception ex)
        {
            ClientLog.Warn($"打开外部链接失败: {ex.Message}");
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
        BackColor = bg;
        // 启动页自己按主题画（令牌色），不能靠 Label 的 ForeColor 继承。
        _splash.Dark = dark;

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
        // 隐藏前记下当前大小/位置：用户可能刚把窗口摆好就点了关闭。
        WindowPlacement.Save(this);
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

        // 关机 / 注销 / 任务管理器"结束任务"：系统在等我们退出，取消关窗会拖慢
        // 关机（严重的会弹"此应用阻止你关机"），而且这时候跑完整关停流程也没意义。
        // 直接放行：进程退出后 Python 侧的父进程看门狗和 Job Object 会收掉后端。
        if (e.CloseReason is CloseReason.WindowsShutDown
            or CloseReason.TaskManagerClosing
            or CloseReason.ApplicationExitCall)
        {
            _shutdownStarted = true;
            CancelStartup();
            _trayIcon.Visible = false;
            WindowPlacement.Save(this);
            ClientLog.Info($"收到系统关闭请求（{e.CloseReason}），直接退出。");
            return;
        }

        e.Cancel = true;

        if (!_exitRequested)
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

        // 先掐掉启动流程：否则它会继续初始化 WebView/托盘，并在后端被收走之后
        // 把"引导失败"当成启动错误弹窗（用户明明只是点了退出）。
        CancelStartup();

        WindowPlacement.Save(this);
        _trayIcon.Visible = false;
        Enabled = false;

        // 从托盘退出时窗口是隐藏的：不要把它弹出来。用户刚说"退出"，
        // 屏幕上再冒一个窗口会让人以为程序又在抢焦点；托盘图标消失就是信号。
        if (Visible)
        {
            ShowShutdownStatus();
        }

        if (_notificationStream is not null)
        {
            await _notificationStream.StopAsync().ConfigureAwait(true);
        }

        await _backend.StopAsync().ConfigureAwait(true);
        Close();
    }

    /// <summary>取消启动流程（重复调用与已释放都安全）。</summary>
    private void CancelStartup()
    {
        try
        {
            _startupCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已经在 Dispose 之后：启动流程早已结束，无需再取消。
        }
    }

    /// <summary>
    /// 关停期间的可见反馈。WebView2 是独立子窗口，会整个盖住同级的 windowless 控件
    /// （airspace 限制），所以必须先隐藏它——否则用户点完退出看到的是一张冻住的
    /// 页面，最长 5 秒，和"卡死"没有区别。
    /// </summary>
    private void ShowShutdownStatus()
    {
        if (_webViewReady)
        {
            _webView.Visible = false;
        }

        _splash.IsError = false;
        _splash.ActionText = null;
        _splash.SetStage("正在关闭后端服务…");
        _splash.Visible = true;
        if (!Controls.Contains(_splash))
        {
            Controls.Add(_splash);
        }

        _splash.BringToFront();
    }

    // ======== 宿主配置（client_auto_start / client_close_action）观察与执行 ========

    private void ApplyLifecycleSettings()
    {
        var (autoStart, closeAction) = ReadLifecycleSettings();
        _closeAction = closeAction;
        if (!AutoStartManager.Apply(autoStart) && autoStart)
        {
            // 系统拒绝了自启（组策略禁用 StartupTask）：提示但不阻塞主流程。
            _trayIcon?.ShowBalloonTip(
                4000,
                "自启未生效",
                "系统禁用了本应用的开机自启，可到系统「设置 → 应用 → 启动」中查看。",
                System.Windows.Forms.ToolTipIcon.Warning);
        }
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

                // 缺省 False：自启只由用户明确开启（首次向导 / 设置页），
                // 不能因为装了软件就默认写登录项。
                var autoStart = root.TryGetProperty("client_auto_start", out var autoStartElement)
                    && autoStartElement.ValueKind == JsonValueKind.True;

                var action = root.TryGetProperty("client_close_action", out var actionElement)
                    && actionElement.ValueKind == JsonValueKind.String
                    ? (actionElement.GetString() ?? DefaultCloseAction).Trim().ToLowerInvariant()
                    : DefaultCloseAction;
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

        // 配置缺失/损坏时按"用户没开自启"处理：宁可少一条登录项，也不要静默写入。
        return (false, DefaultCloseAction);
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
            foreach (var registration in _wakeRegistrations)
            {
                registration.Unregister(null);
            }

            _wakeRegistrations.Clear();
            _lifecycleDebounce.Dispose();
            _startupCts.Dispose();
            _splash.Dispose();
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

    /// <summary>
    /// WebView2 运行时缺失：把启动页变成错误态并给出"下载运行时"入口，后台继续运行。
    /// 不做成"致命错误 + 退出"，是因为后台能力（自动排班/提醒/桥接供数）与主界面无关。
    /// </summary>
    private void ShowWebViewRuntimeMissing(WebViewRuntimeMissingException exception)
    {
        _webViewUnavailable = true;
        ClientLog.Warn($"界面不可用：{exception.Message}");

        // 静默自启时窗口是隐藏的：这种错误必须让用户看见，否则等于装了个没界面的软件。
        if (!Visible)
        {
            RestoreWindow();
        }

        _splash.IsError = true;
        _splash.SetStage("界面组件缺失：需要 Microsoft Edge WebView2 Runtime", "后台仍在运行，自动排班与提醒不受影响");
        _splash.ActionText = "下载运行时";
        _splash.ActionRequested -= OnSplashActionRequested;
        _splash.ActionRequested += OnSplashActionRequested;
    }

    private void OnSplashActionRequested(object? sender, EventArgs e)
    {
        try
        {
            WebViewRuntime.OpenDownloadPage();
        }
        catch (Exception ex)
        {
            ClientLog.Warn($"打开 WebView2 下载页失败：{ex.Message}");
        }
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
