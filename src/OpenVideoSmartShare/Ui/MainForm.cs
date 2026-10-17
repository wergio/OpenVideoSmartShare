using System.Diagnostics;
using System.Globalization;
using OpenVideoSmartShare.Infrastructure;
using OpenVideoSmartShare.Library;
using OpenVideoSmartShare.Localization;
using OpenVideoSmartShare.Upnp;

namespace OpenVideoSmartShare.Ui;

/// <summary>Video list with TV selection: double click plays on the default TV.</summary>
internal sealed class MainForm : Form
{
    private readonly AppHost _host;
    private readonly ToolStrip _toolbar = new() { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(6, 4, 6, 4) };
    private readonly ToolStripButton _refreshButton = new();
    private readonly ToolStripLabel _playToLabel = new();
    private readonly ToolStripComboBox _rendererCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, AutoSize = false };
    private readonly ToolStripButton _playButton = new();
    private readonly ToolStripButton _stopButton = new() { AutoToolTip = false, Enabled = false };
    private readonly ToolStripButton _settingsButton = new() { Alignment = ToolStripItemAlignment.Right };
    private readonly ToolStripTextBox _searchBox = new() { AutoSize = false };
    private readonly ToolStripLabel _searchLabel = new();
    private readonly VideoListView _list = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None };
    private readonly Label _emptyLabel = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        BackColor = SystemColors.Window,
        ForeColor = SystemColors.GrayText,
        Visible = false,
    };
    private readonly StatusStrip _status = new() { SizingGrip = true };
    private readonly ToolStripStatusLabel _serverLabel = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel _activityLabel = new();
    private readonly ToolStripStatusLabel _countLabel = new();
    private readonly ContextMenuStrip _listMenu = new();
    private readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 5000 };
    // Streaming starts and ends on the TV's side: the Stop button follows it by polling.
    private readonly System.Windows.Forms.Timer _streamTimer = new() { Interval = 1000 };
    private bool _updatingRenderers;
    private bool _stopping;
    private string? _lastActivity;
    private bool _balloonShown;

    public MainForm(AppHost host)
    {
        _host = host;
        SuspendLayout();
        // Sizes below are at 96 DPI and get scaled to the monitor (the TV runs at ~225%).
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = AppPaths.AppName;
        Icon = AppIcon.Create();
        MinimumSize = new Size(640, 360);
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1180, 640);
        KeyPreview = true;

        _toolbar.Items.AddRange(
        [
            _refreshButton,
            new ToolStripSeparator(),
            _searchLabel,
            _searchBox,
            new ToolStripSeparator(),
            _playToLabel,
            _rendererCombo,
            _playButton,
            _stopButton,
            _settingsButton,
        ]);

        var listPanel = new Panel { Dock = DockStyle.Fill };
        listPanel.Controls.Add(_list);
        listPanel.Controls.Add(_emptyLabel);
        _status.Items.AddRange([_serverLabel, _activityLabel, _countLabel]);
        Controls.Add(listPanel);
        Controls.Add(_toolbar);
        Controls.Add(_status);
        ResumeLayout(false);
        PerformLayout();
        ApplyDpiSizes();
        DpiChanged += (_, _) => ApplyDpiSizes();

        _refreshButton.Click += async (_, _) => await RefreshAsync();
        _playButton.Click += async (_, _) => await PlaySelectedAsync(useDefault: false);
        _stopButton.Click += async (_, _) => await StopAsync();
        _settingsButton.Click += (_, _) => OpenSettings();
        _rendererCombo.SelectedIndexChanged += (_, _) => OnRendererSelectionChanged();
        _searchBox.TextChanged += (_, _) => ReloadList();
        _list.DoubleClick += async (_, _) => await PlaySelectedAsync(useDefault: true);
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        _list.ContextMenuStrip = _listMenu;
        _listMenu.Opening += (_, e) => e.Cancel = !BuildListMenu();
        _statusTimer.Tick += (_, _) => UpdateStatus();
        _streamTimer.Tick += (_, _) => UpdateStopButton();

        _host.Library.SnapshotChanged += (_, _) => PostToUi(ReloadList);
        _host.Library.DetailsChanged += (_, _) => PostToUi(_list.RefreshDetails);
        _host.Library.ScanStateChanged += (_, _) => PostToUi(UpdateStatus);
        _host.Renderers.Changed += (_, _) => PostToUi(ReloadRenderers);
        L.LanguageChanged += OnLanguageChanged;

        // Created now so that events can be marshalled while the window is still hidden in the tray.
        _ = Handle;
        ApplyTexts();
        RestoreLayout();
        ReloadRenderers();
        ReloadList();
        UpdateStatus();
        _statusTimer.Start();
        _streamTimer.Start();
    }

    /// <summary>Shows the window (restoring it if minimized) and brings it to the front.</summary>
    public void ShowAndActivate()
    {
        if (!Visible)
        {
            Show();
        }

        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }

        Activate();
        BringToFront();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SaveLayout();
        if (e.CloseReason == CloseReason.UserClosing)
        {
            // The window only hides: the server keeps running in the tray.
            e.Cancel = true;
            Hide();
            if (!_balloonShown && Owner == null)
            {
                _balloonShown = true;
                HiddenToTray?.Invoke(this, EventArgs.Empty);
            }

            return;
        }

        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            L.LanguageChanged -= OnLanguageChanged;
            _statusTimer.Dispose();
            _streamTimer.Dispose();
            _listMenu.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.F5:
                _ = RefreshAsync();
                return true;
            case Keys.Control | Keys.F:
                _searchBox.Focus();
                _searchBox.SelectAll();
                return true;
            case Keys.Enter when _list.Focused:
                _ = PlaySelectedAsync(useDefault: true);
                return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>Raised the first time the window is closed to the tray.</summary>
    public event EventHandler? HiddenToTray;

    /// <summary>Tool strip hosted controls are not auto-scaled, so their widths follow the DPI here.</summary>
    private void ApplyDpiSizes()
    {
        _searchBox.Width = LogicalToDeviceUnits(220);
        FitRendererCombo();
    }

    /// <summary>Sizes the TV list to its longest entry (name, ★, "not found").</summary>
    private void FitRendererCombo()
    {
        var font = _rendererCombo.Font;
        var widest = _rendererCombo.Items.Cast<object>()
            .Select(item => TextRenderer.MeasureText(item.ToString(), font).Width)
            .DefaultIfEmpty(0)
            .Max();
        var dropDownButton = SystemInformation.GetVerticalScrollBarWidthForDpi(DeviceDpi);
        _rendererCombo.Width = Math.Max(LogicalToDeviceUnits(120), widest + dropDownButton + LogicalToDeviceUnits(12));
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => PostToUi(() =>
    {
        ApplyTexts();
        ReloadRenderers();
        ReloadList();
        UpdateStatus();
    });

    private void ApplyTexts()
    {
        _refreshButton.Text = "⟳  " + L.T("main.refresh");
        _playToLabel.Text = L.T("main.playTo") + ":";
        _playButton.Text = "▶  " + L.T("main.play");
        _stopButton.Text = "■  " + L.T("main.stop");
        _settingsButton.Text = L.T("main.settings");
        _searchLabel.Text = L.T("main.search") + ":";
        _searchBox.TextBox.PlaceholderText = L.T("main.searchHint");
        _list.SetHeaders(c => L.T(c switch
        {
            VideoColumn.Title => "col.title",
            VideoColumn.Duration => "col.duration",
            VideoColumn.Subtitles => "col.subtitles",
            VideoColumn.Size => "col.size",
            VideoColumn.Resolution => "col.resolution",
            VideoColumn.Date => "col.date",
            _ => "col.folder",
        }));
    }

    private void ReloadList()
    {
        var snapshot = _host.Library.Snapshot;
        var filter = _searchBox.Text.Trim();
        var items = filter.Length == 0
            ? snapshot.Items
            : snapshot.Items.Where(i => i.FileName.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                                        || i.FolderPath.Contains(filter, StringComparison.CurrentCultureIgnoreCase)).ToList();
        _list.SetItems(items);

        var hasFolders = _host.Settings.Read(s => s.SharedFolders.Count > 0);
        _emptyLabel.Text = !hasFolders ? L.T("msg.noFolders")
            : filter.Length > 0 ? L.T("list.noMatch")
            : L.T("list.empty");
        _emptyLabel.Visible = items.Count == 0 && !(snapshot.ScannedAt == null && _host.Library.IsScanning && hasFolders);
        if (_emptyLabel.Visible)
        {
            _emptyLabel.BringToFront();
        }

        UpdateButtons();
        UpdateStatus();
    }

    private void ReloadRenderers()
    {
        var settings = _host.Settings.Snapshot();
        var choices = _host.Renderers.Renderers
            .Select(r => new RendererChoice(r.Udn, r.FriendlyName, r, r.Udn == settings.DefaultRendererUdn))
            .ToList();
        if (settings.DefaultRendererUdn != null && choices.All(c => c.Udn != settings.DefaultRendererUdn))
        {
            choices.Insert(0, new RendererChoice(settings.DefaultRendererUdn, settings.DefaultRendererName ?? settings.DefaultRendererUdn, null, true));
        }

        var selectedUdn = (_rendererCombo.SelectedItem as RendererChoice)?.Udn
                          ?? settings.SelectedRendererUdn ?? settings.DefaultRendererUdn;
        _updatingRenderers = true;
        try
        {
            _rendererCombo.Items.Clear();
            _rendererCombo.Items.AddRange([.. choices]);
            var index = choices.FindIndex(c => c.Udn == selectedUdn);
            if (index < 0)
            {
                index = choices.FindIndex(c => c.Renderer != null);
            }

            _rendererCombo.SelectedIndex = index >= 0 ? index : choices.Count > 0 ? 0 : -1;
        }
        finally
        {
            _updatingRenderers = false;
        }

        FitRendererCombo();
        UpdateButtons();
    }

    private void OnRendererSelectionChanged()
    {
        if (_updatingRenderers)
        {
            return;
        }

        if (_rendererCombo.SelectedItem is RendererChoice choice)
        {
            _host.Settings.Update(s => s.SelectedRendererUdn = choice.Udn, notify: false);
        }

        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var choice = _rendererCombo.SelectedItem as RendererChoice;
        _playButton.Enabled = choice?.Renderer != null && _list.SelectedVideo != null;
        UpdateStopButton();
    }

    /// <summary>Stop is enabled while a TV is streaming a video; the tooltip names the TVs it stops.</summary>
    private void UpdateStopButton()
    {
        IReadOnlyList<RendererInfo> targets = _stopping ? [] : StopTargets();
        _stopButton.Enabled = targets.Count > 0;
        _stopButton.ToolTipText = string.Join(", ", targets.Select(r => r.FriendlyName));
    }

    /// <summary>The TV chosen in the toolbar when it is streaming, otherwise every TV that is.</summary>
    private IReadOnlyList<RendererInfo> StopTargets()
    {
        var streaming = _host.StreamingRenderers();
        var selected = (_rendererCombo.SelectedItem as RendererChoice)?.Renderer;
        return selected != null && streaming.Any(r => r.Udn == selected.Udn) ? [selected] : streaming;
    }

    private bool BuildListMenu()
    {
        var video = _list.SelectedVideo;
        if (video == null)
        {
            return false;
        }

        _listMenu.Items.Clear();
        var defaultUdn = _host.Settings.Read(s => s.DefaultRendererUdn);
        if (defaultUdn != null)
        {
            var playDefault = new ToolStripMenuItem(L.T("ctx.playOnDefault"), null, async (_, _) => await PlaySelectedAsync(useDefault: true))
            {
                Font = new Font(_listMenu.Font, FontStyle.Bold),
            };
            _listMenu.Items.Add(playDefault);
        }

        var playTo = new ToolStripMenuItem(L.T("main.playTo"));
        foreach (var renderer in _host.Renderers.Renderers)
        {
            var target = renderer;
            playTo.DropDownItems.Add(new ToolStripMenuItem(renderer.FriendlyName, null, async (_, _) => await PlayAsync(video, target)));
        }

        playTo.Enabled = playTo.DropDownItems.Count > 0;
        _listMenu.Items.Add(playTo);
        _listMenu.Items.Add(new ToolStripSeparator());
        _listMenu.Items.Add(new ToolStripMenuItem(L.T("ctx.openFolder"), null, (_, _) => OpenContainingFolder(video)));
        return true;
    }

    private async Task PlaySelectedAsync(bool useDefault)
    {
        var video = _list.SelectedVideo;
        if (video == null)
        {
            return;
        }

        var target = ResolveTarget(useDefault, out var problem);
        if (target == null)
        {
            MessageBox.Show(this, problem, AppPaths.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        await PlayAsync(video, target);
    }

    /// <summary>Default TV for double click, otherwise the TV chosen in the toolbar.</summary>
    private RendererInfo? ResolveTarget(bool useDefault, out string problem)
    {
        problem = L.T("msg.noTv");
        var settings = _host.Settings.Snapshot();
        if (useDefault && settings.DefaultRendererUdn != null)
        {
            var renderer = _host.Renderers.Find(settings.DefaultRendererUdn);
            if (renderer == null)
            {
                problem = L.F("msg.defaultTvMissing", settings.DefaultRendererName ?? settings.DefaultRendererUdn);
            }

            return renderer;
        }

        if ((_rendererCombo.SelectedItem as RendererChoice)?.Renderer is { } selected)
        {
            return selected;
        }

        var all = _host.Renderers.Renderers;
        return all.Count == 1 ? all[0] : null;
    }

    private async Task PlayAsync(VideoItem video, RendererInfo renderer)
    {
        SetActivity(L.F("status.sending", renderer.FriendlyName));
        UseWaitCursor = true;
        try
        {
            await _host.PlayAsync(renderer, video);
            SetActivity(L.F("status.sent", renderer.FriendlyName, video.Title));
        }
        catch (UpnpException ex)
        {
            SetActivity(null);
            Log.Warn($"Play failed on {renderer.FriendlyName}: {ex.Code} {ex.Message}");
            MessageBox.Show(this, $"{L.T("msg.playError")}\n\n{renderer.FriendlyName}: {ex.Message}" + (ex.Code > 0 ? $" ({ex.Code})" : ""),
                AppPaths.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private async Task StopAsync()
    {
        var targets = StopTargets();
        if (targets.Count == 0)
        {
            return;
        }

        _stopping = true;
        UpdateStopButton();
        UseWaitCursor = true;
        try
        {
            foreach (var renderer in targets)
            {
                try
                {
                    await _host.StopAsync(renderer);
                }
                catch (UpnpException ex)
                {
                    Log.Warn($"Stop failed on {renderer.FriendlyName}: {ex.Code} {ex.Message}");
                    MessageBox.Show(this, $"{L.T("msg.stopError")}\n\n{renderer.FriendlyName}: {ex.Message}" + (ex.Code > 0 ? $" ({ex.Code})" : ""),
                        AppPaths.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }

            // "Playing on ..." no longer holds.
            SetActivity(null);
        }
        finally
        {
            _stopping = false;
            UseWaitCursor = false;
            UpdateStopButton();
        }
    }

    private async Task RefreshAsync()
    {
        _refreshButton.Enabled = false;
        try
        {
            await _host.RefreshAsync();
        }
        finally
        {
            _refreshButton.Enabled = true;
            UpdateStatus();
        }
    }

    private void OpenSettings()
    {
        using var form = new SettingsForm(_host);
        form.ShowDialog(this);
        // The default TV is chosen in Settings: refresh the star in the TV list.
        ReloadRenderers();
        ReloadList();
    }

    private static void OpenContainingFolder(VideoItem video)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{video.FullPath}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warn($"Cannot open folder of {video.FullPath}", ex);
        }
    }

    private void SetActivity(string? text)
    {
        _lastActivity = text;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var server = _host.Server;
        _serverLabel.Text = server.IsRunning
            ? L.F("status.server", _host.Settings.Read(s => s.ServerName), $"{NetworkHelper.GetAddressFor(null)}:{server.Port}")
            : L.F("status.serverError", server.LastError ?? "-");

        var snapshot = _host.Library.Snapshot;
        _countLabel.Text = L.F("status.videos", snapshot.Items.Count);
        if (_host.Library.IsScanning)
        {
            _activityLabel.Text = L.T("status.scanning");
        }
        else if (snapshot.UnreachableFolders.Count > 0)
        {
            _activityLabel.Text = L.F("status.unreachable", string.Join(", ", snapshot.UnreachableFolders));
        }
        else if (_lastActivity != null)
        {
            _activityLabel.Text = _lastActivity;
        }
        else
        {
            _activityLabel.Text = snapshot.ScannedAt is { } at ? L.F("status.updatedAt", at.ToString("t", CultureInfo.CurrentCulture)) : "";
        }
    }

    private void RestoreLayout()
    {
        var layout = _host.Settings.Read(s => s.Window);
        if (layout.Width > 0 && layout.Height > 0)
        {
            var bounds = new Rectangle(layout.Left, layout.Top, layout.Width, layout.Height);
            if (Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(bounds)))
            {
                StartPosition = FormStartPosition.Manual;
                Bounds = bounds;
            }

            if (layout.Maximized)
            {
                WindowState = FormWindowState.Maximized;
            }
        }

        var widths = layout.ColumnWidths;
        var defaults = new[] { 460, 75, 80, 90, 95, 90, 260 };
        for (var i = 0; i < _list.Columns.Count; i++)
        {
            _list.Columns[i].Width = widths != null && i < widths.Length && widths[i] > 20 ? widths[i] : LogicalToDeviceUnits(defaults[i]);
        }

        if (Enum.IsDefined((VideoColumn)layout.SortColumn))
        {
            _list.SortBy((VideoColumn)layout.SortColumn, toggle: false, descending: layout.SortDescending);
        }

        _list.FillLastColumn();
    }

    private void SaveLayout()
    {
        var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        var widths = _list.Columns.Cast<ColumnHeader>().Select(c => c.Width).ToArray();
        _host.Settings.Update(s => s.Window = new Settings.WindowLayout
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            Maximized = WindowState == FormWindowState.Maximized,
            ColumnWidths = widths,
            SortColumn = (int)_list.SortColumn,
            SortDescending = _list.SortDescending,
        }, notify: false);
    }

    private void PostToUi(Action action)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        try
        {
            BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // Handle destroyed while exiting.
        }
    }

    private sealed record RendererChoice(string Udn, string Name, RendererInfo? Renderer, bool IsDefault)
    {
        public override string ToString() =>
            Name + (IsDefault ? "  ★" : "") + (Renderer == null ? "  " + L.T("main.notFound") : "");
    }
}
