using OpenVideoSmartShare.Infrastructure;
using OpenVideoSmartShare.Localization;

namespace OpenVideoSmartShare.Ui;

/// <summary>Notification area icon with just "Open" and "Quit", owning the main window.</summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly AppHost _host;
    private readonly NotifyIcon _tray = new();
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _openItem = new();
    private readonly ToolStripMenuItem _exitItem = new();
    private readonly MainForm _mainForm;
    private readonly RegisteredWaitHandle _showWait;
    private readonly RegisteredWaitHandle _exitWait;
    private bool _exiting;

    public TrayApplicationContext(AppHost host, EventWaitHandle showRequested, EventWaitHandle exitRequested, bool startHidden)
    {
        _host = host;
        _mainForm = new MainForm(host);
        _mainForm.HiddenToTray += (_, _) =>
            _tray.ShowBalloonTip(3000, AppPaths.AppName, L.T("tray.stillRunning"), ToolTipIcon.Info);

        _openItem.Font = new Font(_menu.Font, FontStyle.Bold);
        _openItem.Click += (_, _) => _mainForm.ShowAndActivate();
        _exitItem.Click += async (_, _) => await ExitAsync();
        _menu.Items.AddRange([_openItem, new ToolStripSeparator(), _exitItem]);

        _tray.Icon = AppIcon.Create(SystemInformation.SmallIconSize.Width);
        _tray.Text = AppPaths.AppName;
        _tray.ContextMenuStrip = _menu;
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                _mainForm.ShowAndActivate();
            }
        };
        _tray.Visible = true;

        // A second launch of the exe signals this event instead of starting another server.
        _showWait = ThreadPool.RegisterWaitForSingleObject(showRequested,
            (_, _) => _mainForm.BeginInvoke(_mainForm.ShowAndActivate), null, Timeout.Infinite, executeOnlyOnce: false);
        // "OpenVideoSmartShare.exe --exit" (used by the install script) closes this instance cleanly.
        _exitWait = ThreadPool.RegisterWaitForSingleObject(exitRequested,
            (_, _) => _mainForm.BeginInvoke(async () => await ExitAsync()), null, Timeout.Infinite, executeOnlyOnce: true);

        L.LanguageChanged += OnLanguageChanged;
        ApplyTexts();

        if (!startHidden)
        {
            _mainForm.Show();
        }

        _ = StartHostAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            L.LanguageChanged -= OnLanguageChanged;
            _showWait.Unregister(null);
            _exitWait.Unregister(null);
            _tray.Dispose();
            _menu.Dispose();
            _mainForm.Dispose();
        }

        base.Dispose(disposing);
    }

    private async Task StartHostAsync()
    {
        try
        {
            // Before listening: otherwise Windows shows its own firewall alert, whose "Cancel" adds a block rule.
            await EnsureFirewallAsync();
            await _host.StartAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Startup failed", ex);
        }
    }

    private async Task EnsureFirewallAsync()
    {
        var state = await Task.Run(() => FirewallHelper.Check(AppPaths.ExecutablePath));
        Log.Info($"Firewall state: {state}");
        if (state is not (FirewallState.Missing or FirewallState.Blocked) || _host.Settings.Read(s => s.FirewallPromptDeclined))
        {
            return;
        }

        var owner = _mainForm.Visible ? _mainForm : null;
        var answer = MessageBox.Show(owner, L.T("firewall.prompt"), AppPaths.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes)
        {
            _host.Settings.Update(s => s.FirewallPromptDeclined = true, notify: false);
            return;
        }

        var added = await Task.Run(() => FirewallHelper.AllowInbound(AppPaths.ExecutablePath));
        if (!added)
        {
            MessageBox.Show(owner, L.T("firewall.failed"), AppPaths.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => ApplyTexts();

    private void ApplyTexts()
    {
        _openItem.Text = L.T("tray.open");
        _exitItem.Text = L.T("tray.exit");
    }

    private async Task ExitAsync()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _tray.Visible = false;
        _mainForm.Hide();
        try
        {
            // Give the server a moment to send ssdp:byebye and close connections.
            await Task.WhenAny(_host.DisposeAsync().AsTask(), Task.Delay(4000));
        }
        catch (Exception ex)
        {
            Log.Warn("Error while shutting down", ex);
        }

        Log.Info("Exit");
        ExitThread();
    }
}
