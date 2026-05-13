using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DutyAgent.Client;

internal sealed class MainForm : Form
{
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
    private bool _shutdownStarted;

    public MainForm()
    {
        Text = "Duty-Agent";
        Width = 1280;
        Height = 800;
        MinimumSize = new Size(900, 600);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = LoadIconOrDefault();
        var baseFont = SystemFonts.MessageBoxFont ?? Font;
        _statusLabel.Font = new Font(baseFont.FontFamily, 12f);

        Controls.Add(_statusLabel);
        Load += OnLoad;
        FormClosing += OnFormClosing;
    }

    private async void OnLoad(object? sender, EventArgs e)
    {
        try
        {
            await _backend.StartAsync(CancellationToken.None).ConfigureAwait(true);
            await InitializeWebViewAsync().ConfigureAwait(true);
            Controls.Remove(_statusLabel);
            Controls.Add(_webView);
            _webView.Source = new Uri(_backend.WebAppUrl);
        }
        catch (Exception ex)
        {
            await ShowFatalErrorAsync("启动失败", ex).ConfigureAwait(true);
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

    private async void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_shutdownStarted)
        {
            return;
        }

        _shutdownStarted = true;
        e.Cancel = true;
        Enabled = false;
        _statusLabel.Text = "正在关闭后端服务...";
        if (!Controls.Contains(_statusLabel))
        {
            Controls.Add(_statusLabel);
            _statusLabel.BringToFront();
        }

        await _backend.StopAsync().ConfigureAwait(true);
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _webView.Dispose();
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

    private async Task ShowFatalErrorAsync(string title, Exception exception)
    {
        var message = $"{exception.Message}\n\n{exception.GetBaseException().Message}";
        MessageBox.Show(this, message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        _shutdownStarted = true;
        await _backend.StopAsync().ConfigureAwait(true);
        Close();
    }
}
