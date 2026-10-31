using System.Text.Json;

namespace DutyAgent.Client;

/// <summary>
/// 记住主窗口的大小/位置/最大化状态。
///
/// 以前每次启动都是 1280×800 居中：用户把窗口拖到副屏、拉大看排班表，
/// 关掉再开又回到原位，等于每次都要重摆一次。状态存在数据目录下的
/// client-window.json（客户端私有，不碰 host-config——那份是 C# 写、Python 读的桥）。
///
/// 恢复时做两件事：尺寸不许小于 MinimumSize，矩形必须还落在某块屏幕上。
/// 后者是为拔掉副屏/改分辨率准备的——否则窗口会恢复到不存在的坐标上，
/// 用户看到的是"程序启动了但没有窗口"。
/// </summary>
internal static class WindowPlacement
{
    private sealed class State
    {
        public int Left { get; set; }
        public int Top { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public bool Maximized { get; set; }
    }

    private static string StoragePath => Path.Combine(ClientLog.DataDirectory, "client-window.json");

    public static void Apply(Form form)
    {
        var state = Read();
        if (state is null)
        {
            return;
        }

        var width = Math.Max(form.MinimumSize.Width, state.Width);
        var height = Math.Max(form.MinimumSize.Height, state.Height);
        var bounds = new Rectangle(state.Left, state.Top, width, height);
        if (!IsOnScreen(bounds))
        {
            // 保存的坐标已经不在任何屏幕上（换了显示器/分辨率）：回到居中，
            // 而不是让窗口跑到看不见的地方。
            return;
        }

        form.StartPosition = FormStartPosition.Manual;
        form.Bounds = bounds;
        if (state.Maximized)
        {
            form.WindowState = FormWindowState.Maximized;
        }
    }

    public static void Save(Form form)
    {
        try
        {
            // 最大化时 Bounds 是整屏，真正该记住的是还原后的尺寸。
            var bounds = form.WindowState == FormWindowState.Normal ? form.Bounds : form.RestoreBounds;
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                return;
            }

            var state = new State
            {
                Left = bounds.Left,
                Top = bounds.Top,
                Width = bounds.Width,
                Height = bounds.Height,
                Maximized = form.WindowState == FormWindowState.Maximized,
            };

            Directory.CreateDirectory(ClientLog.DataDirectory);
            File.WriteAllText(
                StoragePath,
                JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }),
                new System.Text.UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            // 记不住窗口位置不是故障，别让它影响关窗流程。
            ClientLog.Warn($"窗口位置保存失败：{ex.Message}");
        }
    }

    private static State? Read()
    {
        try
        {
            if (!File.Exists(StoragePath))
            {
                return null;
            }

            var state = JsonSerializer.Deserialize<State>(File.ReadAllText(StoragePath));
            if (state is null || state.Width <= 0 || state.Height <= 0)
            {
                return null;
            }

            return state;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsOnScreen(Rectangle bounds)
    {
        // 要求至少 120px 的可见重叠：只要窗口还能被鼠标抓住就算有效。
        const int minimumVisible = 120;
        foreach (var screen in Screen.AllScreens)
        {
            var overlap = Rectangle.Intersect(screen.WorkingArea, bounds);
            if (overlap.Width >= minimumVisible && overlap.Height >= minimumVisible)
            {
                return true;
            }
        }

        return false;
    }
}
