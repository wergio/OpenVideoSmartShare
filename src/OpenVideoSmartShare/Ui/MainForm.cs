using System.Diagnostics;
using OpenVideoSmartShare.Infrastructure;
using OpenVideoSmartShare.Library;
using OpenVideoSmartShare.Localization;
using OpenVideoSmartShare.Upnp;

namespace OpenVideoSmartShare.Ui;

/// <summary>Video list with TV selection: double click plays on the default TV.</summary>
internal sealed class MainForm : Form
{
    private readonly AppHost _host;
    // Flow layout: on a narrow window the items wrap onto more rows instead of being hidden.
    // Settings sits on a strip of its own laid over the top right corner (the flow layout cannot
    // right-align); the toolbar keeps that corner free with its right padding.
    private readonly Panel _toolbarPanel = new() { Dock = DockStyle.Top, BackColor = FlatToolStripRenderer.Background };
    private readonly ToolStrip _toolbar = new()
    {
        GripStyle = ToolStripGripStyle.Hidden,
        Padding = new Padding(6, 4, 6, 4),
        LayoutStyle = ToolStripLayoutStyle.Flow,
        Dock = DockStyle.Top,
        Renderer = new FlatToolStripRenderer(),
    };
    private readonly ToolStrip _settingsStrip = new()
    {
        GripStyle = ToolStripGripStyle.Hidden,
        Padding = Padding.Empty,
        Dock = DockStyle.None,
        Anchor = AnchorStyles.Top | AnchorStyles.Right,
        LayoutStyle = ToolStripLayoutStyle.HorizontalStackWithOverflow,
        CanOverflow = false,
        Renderer = new FlatToolStripRenderer { DrawBorder = false },
    };
    private readonly ToolStripButton _refreshButton = new();
    private readonly Label _playToLabel = ToolbarLabel();
    private readonly ComboBox _rendererCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ToolStripButton _playButton = new();
    // Buttons that cannot act right now are hidden rather than disabled.
    private readonly ToolStripButton _pauseButton = new() { AutoToolTip = false, Visible = false };
    private readonly ToolStripButton _stopButton = new() { AutoToolTip = false, Visible = false };
    // Segoe MDL2 Assets glyphs; emoji and most symbols do not render in tool strip buttons.
    private const string RefreshGlyph = "\uE72C";
    private const string PlayGlyph = "\uE768";
    private const string PauseGlyph = "\uE769";
    private const string StopGlyph = "\uE71A";
    private const string SettingsGlyph = "\uE713";
    private const string SpeakerGlyph = "\uE767";
    private const string MutedGlyph = "\uE74F";
    private readonly ToolStripButton _muteButton = new() { AutoToolTip = false, Visible = false };
    private readonly ToolStripControlHost _volumeGroup;
    private readonly Button _volumeDown = VolumeButton("\uE993");
    private readonly Label _volumeLabel = ToolbarLabel();
    private readonly Button _volumeUp = VolumeButton("\uE995");
    private readonly ToolTip _toolTip = new();
    private readonly ToolStripButton _settingsButton = new();
    private readonly TextBox _searchBox = new();
    private readonly Label _searchLabel = ToolbarLabel();
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
    // The TV state (paused, volume) is read every couple of seconds: it also changes from the remote.
    private readonly System.Windows.Forms.Timer _transportTimer = new() { Interval = 2000 };
    private bool _updatingRenderers;
    private bool _stopping;
    private bool _pausing;
    private bool _pollingTransport;
    // TV paused from here or from its remote; it stays a Pause/Stop target even if it closed the stream.
    private RendererInfo? _pausedOn;
    // TV playing a video sent from here, from Play until its transport state says it stopped
    // (the whole file may be downloaded long before the end, so streaming alone is not enough).
    private RendererInfo? _session;
    private DateTime _sessionStarted;
    private bool _sessionSeenPlaying;
    // Last transport state read from each TV: it wins over the stream, which a stopped TV may
    // keep open.
    private readonly Dictionary<string, string> _transportStates = new(StringComparer.OrdinalIgnoreCase);
    private string? _volumeUdn;
    private int? _volume;
    private int? _volumeSent;
    private bool? _muted;
    private string? _lastActivity;

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
            Group(_searchLabel, _searchBox),
            new ToolStripSeparator(),
            Group(_playToLabel, _rendererCombo),
            _playButton,
            _pauseButton,
            _stopButton,
            _muteButton,
            _volumeGroup = Group(_volumeDown, _volumeLabel, _volumeUp),
        ]);
        _settingsStrip.Items.Add(_settingsButton);

        foreach (ToolStripItem item in _toolbar.Items)
        {
            // Rows are as tall as the combo box: center the buttons, stretch the separators.
            item.Anchor = item is ToolStripSeparator ? AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Bottom : AnchorStyles.Left;
        }

        _volumeGroup.Visible = false;
        _toolbar.SizeChanged += (_, _) => FitToolbarHeight();
        _toolbar.LayoutCompleted += (_, _) => FitToolbarHeight();
        _toolbarPanel.Controls.Add(_toolbar);
        _toolbarPanel.Controls.Add(_settingsStrip);
        _settingsStrip.BringToFront();
        _settingsStrip.SizeChanged += (_, _) => FitToolbarHeight();

        var listPanel = new Panel { Dock = DockStyle.Fill };
        listPanel.Controls.Add(_list);
        listPanel.Controls.Add(_emptyLabel);
        _status.Items.AddRange([_serverLabel, _activityLabel, _countLabel]);
        Controls.Add(listPanel);
        Controls.Add(_toolbarPanel);
        Controls.Add(_status);
        ResumeLayout(false);
        PerformLayout();
        ApplyDpiSizes();
        DpiChanged += (_, _) => ApplyDpiSizes();

        _refreshButton.Click += async (_, _) => await RefreshAsync();
        _playButton.Click += async (_, _) => await PlaySelectedAsync(useDefault: false);
        _pauseButton.Click += async (_, _) => await TogglePauseAsync();
        _stopButton.Click += async (_, _) => await StopAsync();
        _muteButton.Click += async (_, _) => await ToggleMuteAsync();
        _volumeDown.Click += async (_, _) => await ChangeVolumeAsync(-VolumeStep);
        _volumeUp.Click += async (_, _) => await ChangeVolumeAsync(VolumeStep);
        foreach (var control in new Control[] { _volumeDown, _volumeLabel, _volumeUp })
        {
            control.MouseWheel += async (_, e) => await ChangeVolumeAsync(Math.Sign(e.Delta) * VolumeStep);
        }

        _transportTimer.Tick += async (_, _) => await PollTransportAsync();
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
        _transportTimer.Start();
    }

    /// <summary>
    /// Shows the window (restoring it if minimized) and brings it to the front. Coming back from
    /// the tray rescans the folders, as files may have changed while the window was hidden.
    /// </summary>
    public void ShowAndActivate()
    {
        if (!Visible)
        {
            Show();
            _ = RefreshAsync();
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
            _transportTimer.Dispose();
            _toolTip.Dispose();
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

    /// <summary>Tool strip hosted controls are not auto-scaled, so their widths follow the DPI here.</summary>
    private void ApplyDpiSizes()
    {
        _searchBox.Width = LogicalToDeviceUnits(220);
        // Wide enough for any level, so that the buttons do not move.
        _volumeLabel.MinimumSize = new Size(TextRenderer.MeasureText(VolumeText(100), _volumeLabel.Font).Width, 0);
        foreach (var button in new[] { _refreshButton, _playButton, _pauseButton, _stopButton, _muteButton, _settingsButton })
        {
            if (button.Tag is string glyph)
            {
                SetGlyph(button, glyph, force: true);
            }
        }

        FitRendererCombo();
    }

    /// <summary>
    /// The toolbar sizes its height to its rows and the panel follows it; the Settings strip sits
    /// in the space the toolbar leaves free on the right, centered on the first row.
    /// </summary>
    private void FitToolbarHeight()
    {
        var reserved = _settingsStrip.Width + LogicalToDeviceUnits(6);
        if (_toolbar.Padding.Right != reserved)
        {
            _toolbar.Padding = _toolbar.Padding with { Right = reserved };
        }

        if (_toolbarPanel.Height != _toolbar.Height)
        {
            _toolbarPanel.Height = _toolbar.Height;
        }

        var firstRow = _toolbar.Items.Cast<ToolStripItem>().Where(i => i.Available).ToList();
        if (firstRow.Count == 0)
        {
            return;
        }

        var top = firstRow.Min(i => i.Bounds.Top);
        var rowHeight = firstRow.Where(i => i.Bounds.Top - top < i.Bounds.Height / 2).Max(i => i.Bounds.Height);
        var location = new Point(
            _toolbarPanel.ClientSize.Width - _settingsStrip.Width - LogicalToDeviceUnits(6),
            Math.Max(0, top + (rowHeight - _settingsStrip.Height) / 2));
        if (_settingsStrip.Location != location)
        {
            _settingsStrip.Location = location;
        }
    }

    /// <summary>Shows <paramref name="glyph"/> as the button image, drawn for the current DPI.</summary>
    private void SetGlyph(ToolStripButton button, string glyph, bool force = false)
    {
        if (!force && button.Tag as string == glyph)
        {
            return;
        }

        button.Tag = glyph;
        button.ImageScaling = ToolStripItemImageScaling.None;
        button.TextImageRelation = TextImageRelation.ImageBeforeText;
        var old = button.Image;
        button.Image = GlyphImage(glyph);
        old?.Dispose();
    }

    /// <summary>
    /// A Segoe MDL2 Assets glyph as a tool strip image, centered on its drawn outline and shrunk
    /// when it would not fit (text layout boxes do not match the outline: the gear was clipped).
    /// </summary>
    private Bitmap GlyphImage(string glyph)
    {
        var size = LogicalToDeviceUnits(16);
        var bitmap = new Bitmap(size, size);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var family = new FontFamily("Segoe MDL2 Assets");
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddString(glyph, family, (int)FontStyle.Regular, size * 0.85f, PointF.Empty, StringFormat.GenericTypographic);
        var bounds = path.GetBounds();
        if (bounds.Width > 0 && bounds.Height > 0)
        {
            var available = size - 1f;
            var scale = Math.Min(1f, Math.Min(available / bounds.Width, available / bounds.Height));
            using var transform = new System.Drawing.Drawing2D.Matrix();
            transform.Translate(size / 2f, size / 2f);
            transform.Scale(scale, scale);
            transform.Translate(-(bounds.Left + bounds.Width / 2), -(bounds.Top + bounds.Height / 2));
            path.Transform(transform);
        }

        using var brush = new SolidBrush(SystemColors.ControlText);
        graphics.FillPath(brush, path);
        return bitmap;
    }

    private const int VolumeStep = 2;
    // Loading a video can take a while before the TV reports PLAYING.
    private static readonly TimeSpan SessionStartGrace = TimeSpan.FromSeconds(15);
    private static readonly string[] PlaybackStates = ["PLAYING", "PAUSED_PLAYBACK", "TRANSITIONING"];

    private static Label ToolbarLabel() => new()
    {
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        TextAlign = ContentAlignment.MiddleCenter,
        UseMnemonic = false,
        Margin = new Padding(3, 0, 3, 0),
    };

    private static Button VolumeButton(string text)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Left,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe MDL2 Assets", 10F),
            Margin = new Padding(0),
            TabStop = false,
            UseVisualStyleBackColor = false,
            BackColor = Color.Transparent,
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = ProfessionalColors.ButtonSelectedHighlight;
        button.FlatAppearance.MouseDownBackColor = ProfessionalColors.ButtonPressedHighlight;
        return button;
    }

    /// <summary>Keeps controls (a label and its field) together on one row when the toolbar wraps.</summary>
    private static ToolStripControlHost Group(params Control[] controls)
    {
        var panel = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Color.Transparent,
        };
        panel.Controls.AddRange(controls);
        return new ToolStripControlHost(panel) { AutoSize = true, Margin = new Padding(1, 1, 1, 1) };
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
        _refreshButton.Text = L.T("main.refresh");
        SetGlyph(_refreshButton, RefreshGlyph);
        _playToLabel.Text = L.T("main.playTo") + ":";
        _playButton.Text = L.T("main.play");
        SetGlyph(_playButton, PlayGlyph);
        _stopButton.Text = L.T("main.stop");
        SetGlyph(_stopButton, StopGlyph);
        UpdatePauseText();
        _toolTip.SetToolTip(_volumeDown, L.T("main.volumeDown"));
        _toolTip.SetToolTip(_volumeUp, L.T("main.volumeUp"));
        _muteButton.Text = L.T("main.muteButton");
        ApplyDpiSizes();
        _settingsButton.Text = L.T("main.settings");
        SetGlyph(_settingsButton, SettingsGlyph);
        _searchLabel.Text = L.T("main.search") + ":";
        _searchBox.PlaceholderText = L.T("main.searchHint");
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
        var choices = _host.PlayTargets()
            .Select(t => new RendererChoice(t.Udn, t.Name, t.Renderer, t.Udn == settings.DefaultRendererUdn))
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
        _ = RefreshVolumeAsync();
    }

    private void UpdateButtons()
    {
        var choice = _rendererCombo.SelectedItem as RendererChoice;
        // Offline TVs can be chosen too: Play then explains why it cannot start.
        _playButton.Visible = choice != null && _list.SelectedVideo != null;
        UpdateStopButton();
    }

    /// <summary>
    /// Pause and Stop are shown only during a playback (also while paused), disabled while a
    /// command is on its way; the tooltip names the TVs they act on.
    /// </summary>
    private void UpdateStopButton()
    {
        var targets = StopTargets();
        var names = string.Join(", ", targets.Select(r => r.FriendlyName));
        var busy = _stopping || _pausing;
        _stopButton.Visible = targets.Count > 0;
        _stopButton.Enabled = !busy;
        _stopButton.ToolTipText = names;
        _pauseButton.Visible = targets.Count > 0;
        _pauseButton.Enabled = !busy;
        _pauseButton.ToolTipText = names;
    }

    private void UpdatePauseText()
    {
        _pauseButton.Text = L.T(_pausedOn != null ? "main.resume" : "main.pause");
        SetGlyph(_pauseButton, _pausedOn != null ? PlayGlyph : PauseGlyph);
    }

    /// <summary>
    /// TVs in playback: the one chosen in the toolbar when it is playing, otherwise every TV
    /// that is. Candidates are the TVs streaming from the server, the one a video was sent to
    /// and the paused one; a TV whose transport state says it stopped is left out.
    /// </summary>
    private IReadOnlyList<RendererInfo> StopTargets()
    {
        var active = PlaybackCandidates().Where(IsPlaying).ToList();
        var selected = (_rendererCombo.SelectedItem as RendererChoice)?.Udn;
        return active.FirstOrDefault(r => r.Udn == selected) is { } chosen ? [chosen] : active;
    }

    private List<RendererInfo> PlaybackCandidates()
    {
        var candidates = _host.StreamingRenderers().ToList();
        foreach (var held in new[] { _session, _pausedOn })
        {
            if (held != null && candidates.All(r => r.Udn != held.Udn) && _host.Renderers.Find(held.Udn) is { } renderer)
            {
                candidates.Add(renderer);
            }
        }

        return candidates;
    }

    private bool IsPlaying(RendererInfo renderer)
    {
        if (!_transportStates.TryGetValue(renderer.Udn, out var state))
        {
            return true;
        }

        // While the TV loads the video it may still report its previous state.
        return PlaybackStates.Contains(state, StringComparer.OrdinalIgnoreCase)
               || (renderer.Udn == _session?.Udn && !_sessionSeenPlaying && DateTime.UtcNow - _sessionStarted < SessionStartGrace);
    }

    private void EndSession()
    {
        _session = null;
        _pausedOn = null;
        UpdatePauseText();
        UpdateStopButton();
    }

    private void SetPausedOn(RendererInfo? renderer)
    {
        _pausedOn = renderer;
        UpdatePauseText();
        UpdateStopButton();
    }

    /// <summary>Pauses the targets, or resumes them when the first one is paused.</summary>
    private async Task TogglePauseAsync()
    {
        var targets = StopTargets();
        if (targets.Count == 0)
        {
            return;
        }

        _pausing = true;
        UpdateStopButton();
        try
        {
            var state = await _host.Transport.GetTransportStateAsync(targets[0]);
            var resume = string.Equals(state, "PAUSED_PLAYBACK", StringComparison.OrdinalIgnoreCase);
            foreach (var renderer in targets)
            {
                try
                {
                    await (resume ? _host.ResumeAsync(renderer) : _host.PauseAsync(renderer));
                    _transportStates[renderer.Udn] = resume ? "PLAYING" : "PAUSED_PLAYBACK";
                }
                catch (UpnpException ex)
                {
                    Log.Warn($"Pause/resume failed on {renderer.FriendlyName}: {ex.Code} {ex.Message}");
                    MessageBox.Show(this, $"{L.T("msg.pauseError")}\n\n{renderer.FriendlyName}: {ex.Message}" + (ex.Code > 0 ? $" ({ex.Code})" : ""),
                        AppPaths.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }

            _pausedOn = resume ? null : targets[0];
        }
        finally
        {
            _pausing = false;
            UpdatePauseText();
            UpdateStopButton();
        }
    }

    /// <summary>
    /// Follows the TV: pauses made with the remote, the end of the playback (stopped, finished,
    /// failed) and the volume.
    /// </summary>
    private async Task PollTransportAsync()
    {
        if (_pollingTransport || _stopping || _pausing || !Visible)
        {
            return;
        }

        _pollingTransport = true;
        try
        {
            foreach (var renderer in PlaybackCandidates().Take(3))
            {
                var state = await _host.Transport.GetTransportStateAsync(renderer);
                if (state == null || _pausing || _stopping)
                {
                    continue;
                }

                _transportStates[renderer.Udn] = state;
                if (renderer.Udn == _session?.Udn && PlaybackStates.Contains(state, StringComparer.OrdinalIgnoreCase))
                {
                    _sessionSeenPlaying = true;
                }
            }

            var targets = StopTargets();
            if (targets.Count == 0)
            {
                if (_pausedOn != null || _session != null)
                {
                    EndSession();
                }
            }
            else
            {
                var paused = _transportStates.TryGetValue(targets[0].Udn, out var first)
                             && string.Equals(first, "PAUSED_PLAYBACK", StringComparison.OrdinalIgnoreCase);
                if (paused != (_pausedOn != null))
                {
                    SetPausedOn(paused ? targets[0] : null);
                }
            }

            UpdateStopButton();

            await RefreshVolumeAsync();
        }
        finally
        {
            _pollingTransport = false;
        }
    }

    /// <summary>The TV in playback, when it has a volume control.</summary>
    private RendererInfo? VolumeTarget() => StopTargets().FirstOrDefault(r => r.RenderingControl != null);

    private async Task RefreshVolumeAsync()
    {
        var target = VolumeTarget();
        if (target?.Udn != _volumeUdn)
        {
            _volumeUdn = target?.Udn;
            _volume = null;
            _muted = null;
            UpdateVolumeControls();
        }

        // Not while a change is being sent: the reading could be older than the change.
        if (target == null || _volumeSent != null)
        {
            return;
        }

        try
        {
            var volume = await _host.Transport.GetVolumeAsync(target);
            var muted = await _host.Transport.GetMuteAsync(target);
            if (_volumeUdn == target.Udn && _volumeSent == null)
            {
                _volume = volume;
                _muted = muted;
            }
        }
        catch (UpnpException)
        {
            // Logged by the client; the volume stays unknown.
            _volume = null;
            _muted = null;
        }

        UpdateVolumeControls();
    }

    /// <summary>Changes the volume; clicks made while a change is on its way are sent together.</summary>
    private async Task ChangeVolumeAsync(int delta)
    {
        var target = VolumeTarget();
        if (target == null || delta == 0)
        {
            return;
        }

        if (_volume == null)
        {
            await RefreshVolumeAsync();
            if (_volume == null)
            {
                return;
            }
        }

        _volume = Math.Clamp(_volume.Value + delta, 0, 100);
        UpdateVolumeControls();
        if (_volumeSent != null)
        {
            return;
        }

        try
        {
            while (_volume is { } wanted && wanted != _volumeSent && _volumeUdn == target.Udn)
            {
                _volumeSent = wanted;
                await _host.Transport.SetVolumeAsync(target, wanted);
            }
        }
        catch (UpnpException)
        {
            _volume = null;
        }
        finally
        {
            _volumeSent = null;
        }

        UpdateVolumeControls();
    }

    private async Task ToggleMuteAsync()
    {
        if (VolumeTarget() is not { } target || _muted is not { } muted)
        {
            return;
        }

        try
        {
            await _host.Transport.SetMuteAsync(target, !muted);
            _muted = !muted;
        }
        catch (UpnpException)
        {
            _muted = null;
        }

        UpdateVolumeControls();
    }

    private static string VolumeText(int? volume) =>
        volume is { } level ? L.T("main.volume") + " " + level.ToString(L.Culture) : L.T("main.volume");

    private void UpdateVolumeControls()
    {
        // Shown during a playback only (the LG CX refuses volume commands otherwise).
        _volumeLabel.Text = VolumeText(_volume);
        _volumeGroup.Visible = _volume != null;
        _muteButton.Visible = _muted != null;
        var muted = _muted == true;
        _muteButton.Checked = muted;
        _muteButton.ToolTipText = L.T(muted ? "main.unmute" : "main.mute");
        SetGlyph(_muteButton, muted ? MutedGlyph : SpeakerGlyph);
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
        foreach (var target in _host.PlayTargets())
        {
            if (target.Renderer is { } renderer)
            {
                playTo.DropDownItems.Add(new ToolStripMenuItem(target.Name, null, async (_, _) => await PlayAsync(video, renderer)));
            }
            else
            {
                playTo.DropDownItems.Add(new ToolStripMenuItem(target.Name + "  " + L.T("main.notFound"), null, (_, _) => ShowTvMissing(target.Name)));
            }
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

        if (_rendererCombo.SelectedItem is RendererChoice choice)
        {
            var renderer = _host.Renderers.Find(choice.Udn);
            if (renderer == null)
            {
                problem = L.F("msg.tvMissing", choice.Name);
            }

            return renderer;
        }

        var online = _host.PlayTargets().Where(t => t.Renderer != null).ToList();
        return online.Count == 1 ? online[0].Renderer : null;
    }

    private void ShowTvMissing(string name) =>
        MessageBox.Show(this, L.F("msg.tvMissing", name), AppPaths.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);

    private async Task PlayAsync(VideoItem video, RendererInfo renderer)
    {
        SetActivity(L.F("status.sending", renderer.FriendlyName));
        UseWaitCursor = true;
        try
        {
            await _host.PlayAsync(renderer, video);
            SetActivity(L.F("status.sent", renderer.FriendlyName, video.Title));
            _session = renderer;
            _sessionStarted = DateTime.UtcNow;
            _sessionSeenPlaying = false;
            _transportStates.Remove(renderer.Udn);
            SetPausedOn(null);
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
                    _transportStates[renderer.Udn] = "STOPPED";
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
            EndSession();
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
            _activityLabel.Text = snapshot.ScannedAt is { } at ? L.F("status.updatedAt", at.ToString("t", L.Culture)) : "";
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
        public override string ToString() => Name + (IsDefault ? "  ★" : "");
    }
}
