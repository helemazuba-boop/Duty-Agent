using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace DutyAgent.Client;

/// <summary>
/// 启动页：一条 44px 的自绘标题条 + 居中品牌块 + 不确定进度条 + 阶段文案。
///
/// 为什么是自绘而不是用原生标题栏或 web 页：
///   * 原生标题栏 32px、web 标题条 44px，切换时会重排一次，用户能看见内容整体上移；
///     这里从第一帧就画成 web 那条的几何，页面出现时零跳动。
///   * WebView2 冷启动要 1-1.5s，比后端先就绪但不可能第一帧就有内容，
///     所以第一屏必须是原生画的。
///   * WebView2 是独立子窗口，会盖住同级的 windowless 控件（airspace），
///     所以启动页不能"浮"在 WebView 上——它是 WebView 挂上来之前的唯一内容。
///
/// 尺寸全部来自 web 的 AppLayout.vue / tokens.generated.css：标题条 44、三键 46×44、
/// 图标 10px、悬停色 --dt-hover、关闭悬停 #E81123。改那边记得同步这里。
/// </summary>
internal sealed class SplashView : Control
{
    // ---- 设计令牌（tokens.generated.css）----
    private static readonly Color LightBg = Color.FromArgb(0xF6, 0xF7, 0xF9);
    private static readonly Color LightText = Color.FromArgb(0x11, 0x18, 0x27);
    private static readonly Color LightText2 = Color.FromArgb(0x6B, 0x72, 0x80);
    private static readonly Color LightText3 = Color.FromArgb(0x8B, 0x93, 0xA1);
    private static readonly Color LightHover = Color.FromArgb(0xF3, 0xF4, 0xF6);
    private static readonly Color LightTrack = Color.FromArgb(0xE5, 0xE7, 0xEB);
    private static readonly Color LightBorder = Color.FromArgb(0xD1, 0xD5, 0xDB);
    private static readonly Color LightProgressTrack = Color.FromArgb(0xE2, 0xE5, 0xEA);
    private static readonly Color LightPrimary = Color.FromArgb(0x5B, 0x5B, 0xD6);

    private static readonly Color DarkBg = Color.FromArgb(0x0F, 0x11, 0x15);
    private static readonly Color DarkText = Color.FromArgb(0xE6, 0xE8, 0xEB);
    private static readonly Color DarkText2 = Color.FromArgb(0x9A, 0xA0, 0xA6);
    private static readonly Color DarkText3 = Color.FromArgb(0x7B, 0x82, 0x90);
    private static readonly Color DarkHover = Color.FromArgb(0x1A, 0x1C, 0x22);
    private static readonly Color DarkTrack = Color.FromArgb(0x23, 0x26, 0x2D);
    private static readonly Color DarkBorder = Color.FromArgb(0x33, 0x37, 0x40);
    private static readonly Color DarkProgressTrack = Color.FromArgb(0x2C, 0x30, 0x39);
    private static readonly Color DarkPrimary = Color.FromArgb(0x8F, 0x8D, 0xF0);

    private static readonly Color CloseHover = Color.FromArgb(0xE8, 0x11, 0x23);

    // ---- 几何（与 web 标题条一致）----
    private const int TitleBarHeight = 44;
    private const int ButtonWidth = 46;
    private const int IconSize = 10;
    private const int LogoSize = 64;
    private const int ProgressWidth = 220;
    private const int ProgressHeight = 2;
    private const int ProgressThumb = 72;
    private const int ProgressCycleMs = 1400;

    private readonly System.Windows.Forms.Timer _animation = new() { Interval = 33 };
    private readonly Image? _logo;
    private int _hoveredButton = -1;
    private long _animationStarted = Environment.TickCount64;

    private string _stage = "正在启动…";
    private string? _hint;
    private string _version = string.Empty;
    private bool _isError;
    private string? _actionText;
    private bool _actionHovered;
    private Rectangle _actionRect = Rectangle.Empty;

    public SplashView()
    {
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.Selectable,
            true);

        Dock = DockStyle.Fill;
        _logo = LoadLogo();
        _animation.Tick += (_, _) => AnimateProgress();
    }

    /// <summary>点最小化/最大化/关闭时触发；按钮由本控件绘制，命中测试也在这里做。</summary>
    public event EventHandler? MinimizeRequested;
    public event EventHandler? MaximizeRequested;
    public event EventHandler? CloseRequested;

    /// <summary>错误态下的补救按钮（例如"下载运行时"）被点击。</summary>
    public event EventHandler? ActionRequested;

    /// <summary>深色主题。与窗口铬共用同一个判断，避免启动页和标题栏色差。</summary>
    public bool Dark { get; set; }

    /// <summary>
    /// 是否自己画标题条。正常情况是 true（窗口是无边框的，web 之后会接管这条）；
    /// host-config 里 client_custom_chrome=false 时窗口有原生标题栏，这里必须关掉，
    /// 否则会看到两条标题栏。
    /// </summary>
    public bool ShowTitleBar { get; set; } = true;

    /// <summary>错误态：文案换成危险色，用于缺 WebView2 / 后端启动失败。</summary>
    public bool IsError
    {
        get => _isError;
        set
        {
            if (_isError == value)
            {
                return;
            }

            _isError = value;
            Invalidate();
        }
    }

    public string Version
    {
        get => _version;
        set
        {
            _version = value;
            Invalidate();
        }
    }

    /// <summary>错误态下的补救按钮文案（为空则不画）。</summary>
    public string? ActionText
    {
        get => _actionText;
        set
        {
            _actionText = value;
            Invalidate();
        }
    }

    /// <summary>更新阶段文案（只有真实事件才改它，不伪造百分比）。</summary>
    public void SetStage(string stage, string? hint = null)
    {
        if (_stage == stage && _hint == hint)
        {
            return;
        }

        _stage = stage;
        _hint = hint;
        Invalidate(StageRect());
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible)
        {
            // 从 1/4 周期开始：第一帧高亮段就已经在轨道里，不会看起来像一条空线。
            _animationStarted = Environment.TickCount64 - ProgressCycleMs / 4;
            _animation.Start();
        }
        else
        {
            _animation.Stop();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var scale = DeviceDpi / 96f;

        g.Clear(Dark ? DarkBg : LightBg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        DrawTitleBar(g, scale);
        DrawBrandBlock(g, scale);
        DrawVersion(g, scale);
    }

    // ---------------- 标题条 ----------------

    private void DrawTitleBar(Graphics g, float scale)
    {
        if (!ShowTitleBar)
        {
            return;
        }

        var barHeight = (int)Math.Round(TitleBarHeight * scale);
        // 与 web 一致：标题条上不写应用名（名字在侧栏），只有右侧三键。
        for (var i = 0; i < 3; i++)
        {
            DrawCaptionButton(g, i, scale, barHeight);
        }
    }

    private int TitleBarArea(float scale) =>
        ShowTitleBar ? (int)Math.Round(TitleBarHeight * scale) : 0;

    private void DrawCaptionButton(Graphics g, int index, float scale, int barHeight)
    {
        var rect = ButtonRect(index, scale, barHeight);
        var hovered = _hoveredButton == index;
        var isClose = index == 2;

        if (hovered)
        {
            using var brush = new SolidBrush(isClose ? CloseHover : Dark ? DarkHover : LightHover);
            g.FillRectangle(brush, rect);
        }

        var iconColor = hovered && isClose
            ? Color.White
            : Dark ? DarkText2 : LightText2;

        using var pen = new Pen(iconColor, 1f);
        var cx = rect.Left + rect.Width / 2f;
        var cy = rect.Top + rect.Height / 2f;
        var half = IconSize / 2f;

        switch (index)
        {
            case 0: // 最小化
                g.DrawLine(pen, cx - half, cy + 0.5f, cx + half, cy + 0.5f);
                break;
            case 1: // 最大化
                g.DrawRectangle(pen, cx - half, cy - half, IconSize, IconSize);
                break;
            default: // 关闭
                g.DrawLine(pen, cx - half, cy - half, cx + half, cy + half);
                g.DrawLine(pen, cx + half, cy - half, cx - half, cy + half);
                break;
        }
    }

    private Rectangle ButtonRect(int index, float scale, int barHeight)
    {
        var width = (int)Math.Round(ButtonWidth * scale);
        // index 0=最小化 1=最大化 2=关闭；最右那颗必须贴住右边缘，所以是 (3 - index)。
        var right = Width - (3 - index) * width;
        return new Rectangle(right, 0, width, barHeight);
    }

    // ---------------- 品牌块 ----------------

    private void DrawBrandBlock(Graphics g, float scale)
    {
        var contentTop = TitleBarArea(scale);
        var blockHeight = BlockHeight(scale);
        var y = contentTop + Math.Max(0, (Height - contentTop - blockHeight) / 2);

        if (_logo is not null)
        {
            var size = (int)Math.Round(LogoSize * scale);
            g.DrawImage(_logo, new Rectangle((Width - size) / 2, y, size, size));
            y += size;
        }

        y += (int)Math.Round(20 * scale);
        using (var font = CreateFont("Segoe UI Variable Display", 22f, FontStyle.Regular))
        {
            DrawCentered(g, "Duty-Agent", font, Dark ? DarkText : LightText, y);
            y += LineHeight(font);
        }

        y += (int)Math.Round(6 * scale);
        using (var font = CreateFont(null, 13f, FontStyle.Regular))
        {
            DrawCentered(g, "值日排班 · 本地运行", font, Dark ? DarkText2 : LightText2, y);
            y += LineHeight(font);
        }

        y += (int)Math.Round(28 * scale);
        if (_isError)
        {
            // 错误态没有"正在加载"，进度条会骗人：换成一点留白。
            y -= (int)Math.Round(14 * scale);
        }
        else
        {
            DrawProgress(g, scale, y);
            y += (int)Math.Round(ProgressHeight * scale);
            y += (int)Math.Round(14 * scale);
        }
        using (var font = CreateFont(null, 12f, FontStyle.Regular))
        {
            var color = _isError
                ? (Dark ? Color.FromArgb(0xF0, 0x95, 0x95) : Color.FromArgb(0xDC, 0x26, 0x26))
                : (Dark ? DarkText3 : LightText3);
            DrawCentered(g, _stage, font, color, y);
            y += LineHeight(font);
        }

        if (!string.IsNullOrEmpty(_hint))
        {
            y += (int)Math.Round(6 * scale);
            using var font = CreateFont(null, 11f, FontStyle.Regular);
            DrawCentered(g, _hint, font, Dark ? DarkText3 : LightText3, y);
            y += LineHeight(font);
        }

        if (!string.IsNullOrEmpty(_actionText))
        {
            y += (int)Math.Round(16 * scale);
            DrawAction(g, scale, y);
        }
        else
        {
            _actionRect = Rectangle.Empty;
        }
    }

    /// <summary>
    /// 居中绘制单行文本：给一个整宽矩形 + HorizontalCenter，让 GDI 自己居中。
    /// 不要手工算 `x = 中心 - MeasureText/2` —— MeasureText 与 DrawText 对 CJK 的
    /// 宽度口径不一致，实测 12px 中文会整体左偏约 60px（第一版就踩了这个）。
    /// </summary>
    private void DrawCentered(Graphics g, string text, Font font, Color color, int y)
    {
        var rect = new Rectangle(0, y, Width, LineHeight(font));
        TextRenderer.DrawText(g, text, font, rect, color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
            | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
    }

    private static int LineHeight(Font font) => font.Height + 2;

    /// <summary>错误态补救按钮：一个浅底小胶囊，比裸文字更像"能点"。</summary>
    private void DrawAction(Graphics g, float scale, int top)
    {
        using var font = CreateFont(null, 12f, FontStyle.Regular);
        var textSize = TextRenderer.MeasureText(g, _actionText!, font, Size.Empty, TextFormatFlags.NoPadding);
        var padX = (int)Math.Round(14 * scale);
        var padY = (int)Math.Round(7 * scale);
        var width = Math.Min(textSize.Width, Width - padX * 4) + padX * 2;
        var height = LineHeight(font) + padY * 2;
        var rect = new Rectangle((Width - width) / 2, top, width, height);
        _actionRect = rect;

        using var path = RoundedRect(rect, (int)Math.Round(6 * scale));
        using (var fill = new SolidBrush(_actionHovered
                   ? (Dark ? DarkHover : LightHover)
                   : (Dark ? DarkTrack : LightTrack)))
        {
            g.FillPath(fill, path);
        }

        using (var border = new Pen(Dark ? DarkBorder : LightBorder, 1f))
        {
            g.DrawPath(border, path);
        }

        TextRenderer.DrawText(g, _actionText!, font, rect, Dark ? DarkPrimary : LightPrimary,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
            | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
    }

    private static GraphicsPath RoundedRect(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        var d = Math.Max(1, radius * 2);
        path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private void DrawProgress(Graphics g, float scale, int top)
    {
        var width = (int)Math.Round(ProgressWidth * scale);
        var height = Math.Max(1, (int)Math.Round(ProgressHeight * scale));
        var left = (Width - width) / 2;

        using (var track = new SolidBrush(Dark ? DarkProgressTrack : LightProgressTrack))
        {
            g.FillRectangle(track, left, top, width, height);
        }

        // 不确定态：一个固定宽度的高亮段匀速滑过轨道（循环，不表示真实进度）。
        // 必须裁到轨道内：不裁的话高亮段进出时会露在轨道外面，看着像一条多余的白线。
        var thumb = (int)Math.Round(ProgressThumb * scale);
        var elapsed = (Environment.TickCount64 - _animationStarted) % ProgressCycleMs;
        var travel = width + thumb;
        var x = left + (int)(elapsed / (double)ProgressCycleMs * travel) - thumb;

        var clip = g.Save();
        g.SetClip(new Rectangle(left, top, width, height));
        using (var brush = new SolidBrush(Dark ? DarkText : LightText))
        {
            g.FillRectangle(brush, x, top, thumb, height);
        }

        g.Restore(clip);
    }

    private void DrawVersion(Graphics g, float scale)
    {
        if (string.IsNullOrEmpty(_version))
        {
            return;
        }

        using var font = CreateFont(null, 11f, FontStyle.Regular);
        var color = Dark ? DarkText3 : LightText3;
        var size = TextRenderer.MeasureText(g, _version, font, Size.Empty, TextFormatFlags.NoPadding);
        var margin = (int)Math.Round(16 * scale);
        TextRenderer.DrawText(g, _version, font,
            new Point(Width - size.Width - margin, Height - size.Height - margin),
            color, TextFormatFlags.NoPadding);
    }

    private int BlockHeight(float scale)
    {
        // 高度必须与 DrawBrandBlock 里推进 y 的算法逐项对应，否则垂直居中会偏。
        var height = 0;
        if (_logo is not null)
        {
            height += (int)Math.Round(LogoSize * scale);
        }

        height += (int)Math.Round(20 * scale);
        using (var font = CreateFont("Segoe UI Variable Display", 22f, FontStyle.Regular))
        {
            height += LineHeight(font);
        }

        height += (int)Math.Round(6 * scale);
        using (var font = CreateFont(null, 13f, FontStyle.Regular))
        {
            height += LineHeight(font);
        }

        height += (int)Math.Round(28 * scale);
        if (_isError)
        {
            height -= (int)Math.Round(14 * scale);
        }
        else
        {
            height += (int)Math.Round(ProgressHeight * scale);
            height += (int)Math.Round(14 * scale);
        }
        using (var font = CreateFont(null, 12f, FontStyle.Regular))
        {
            height += LineHeight(font);
        }

        if (!string.IsNullOrEmpty(_hint))
        {
            height += (int)Math.Round(6 * scale);
            using var font = CreateFont(null, 11f, FontStyle.Regular);
            height += LineHeight(font);
        }

        if (!string.IsNullOrEmpty(_actionText))
        {
            height += (int)Math.Round(16 * scale);
            using var font = CreateFont(null, 12f, FontStyle.Regular);
            height += LineHeight(font) + (int)Math.Round(7 * scale) * 2;
        }

        return height;
    }

    /// <summary>进度条 + 阶段文案所在的区域：动画只重绘这一块。</summary>
    private Rectangle StageRect()
    {
        var scale = DeviceDpi / 96f;
        var contentTop = TitleBarArea(scale);
        var height = Height - contentTop;
        return new Rectangle(0, contentTop + Math.Max(0, height / 2), Width, Math.Max(1, height / 2));
    }

    private void AnimateProgress()
    {
        if (!Visible)
        {
            return;
        }

        Invalidate(StageRect());
    }

    // ---------------- 交互 ----------------

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        var actionHovered = _actionRect.Contains(e.Location);
        if (actionHovered != _actionHovered)
        {
            _actionHovered = actionHovered;
            Invalidate();
        }

        var index = actionHovered ? -1 : HitTestButton(e.Location);
        if (index != _hoveredButton)
        {
            _hoveredButton = index;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoveredButton != -1 || _actionHovered)
        {
            _hoveredButton = -1;
            _actionHovered = false;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        if (_actionRect.Contains(e.Location))
        {
            ActionRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        switch (HitTestButton(e.Location))
        {
            case 0:
                MinimizeRequested?.Invoke(this, EventArgs.Empty);
                break;
            case 1:
                MaximizeRequested?.Invoke(this, EventArgs.Empty);
                break;
            case 2:
                CloseRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    private int HitTestButton(Point point)
    {
        if (!ShowTitleBar)
        {
            return -1;
        }

        if (point.Y >= TitleBarHeight * DeviceDpi / 96f)
        {
            return -1;
        }

        var scale = DeviceDpi / 96f;
        var barHeight = (int)Math.Round(TitleBarHeight * scale);
        for (var i = 0; i < 3; i++)
        {
            if (ButtonRect(i, scale, barHeight).Contains(point))
            {
                return i;
            }
        }

        return -1;
    }

    // ---------------- 资源 ----------------

    private static Image? LoadLogo()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "icon.png");
            if (!File.Exists(path))
            {
                return null;
            }

            // 先读进内存再解码：文件被占用时不会锁住安装目录（MSIX 下是只读的）。
            using var stream = new MemoryStream(File.ReadAllBytes(path));
            using var source = Image.FromStream(stream);
            return new Bitmap(source, new Size(256, 256));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 按**逻辑像素**建字体。Font 的构造函数吃的是磅值，而且 GDI+ 渲染时
    /// 已经按屏幕 DPI 缩放过一次；这里再乘 DPI 就会二次放大（150% 屏上
    /// 文字变成设计值的两倍，内容块直接溢出窗口——踩过）。
    /// 换算成磅后 DPI 由 GDI+ 自己处理，各 DPI 下视觉尺寸一致。
    /// </summary>
    private Font CreateFont(string? family, float pixels, FontStyle style)
    {
        var size = pixels * 72f / 96f;
        if (!string.IsNullOrEmpty(family))
        {
            try
            {
                var candidate = new Font(family, size, style);
                // GDI+ 找不到字族时会静默回落到默认字体，这里靠 Family.Name 判断是否命中。
                if (string.Equals(candidate.FontFamily.Name, family, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }

                candidate.Dispose();
            }
            catch
            {
            }
        }

        return new Font(Font.FontFamily, size, style);
    }
}
