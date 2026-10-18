using OpenVideoSmartShare.Devices;
using OpenVideoSmartShare.Infrastructure;
using OpenVideoSmartShare.Library;
using OpenVideoSmartShare.Localization;
using OpenVideoSmartShare.Settings;
using OpenVideoSmartShare.Upnp;

namespace OpenVideoSmartShare.Ui;

/// <summary>
/// Settings: shared folders, DLNA server (name, address, firewall), devices (default TV and
/// allow/block list) and general options. There is no on/off switch: the server is always on.
/// </summary>
internal sealed class SettingsForm : Form
{
    private const int ServerTabIndex = 1;

    private readonly AppHost _host;
    private readonly AppSettings _edited;
    private readonly CardTabControl _tabs = new() { Dock = DockStyle.Fill };

    private readonly ListView _folders = NewListView(checkBoxes: false);
    private readonly CheckBox _includeSubfolders = new() { AutoSize = true };
    private readonly Button _addFolder = new() { AutoSize = true };
    private readonly Button _removeFolder = new() { AutoSize = true };
    private readonly Button _refresh = new() { AutoSize = true };

    private readonly Label _nameLabel = new() { AutoSize = true };
    private readonly TextBox _name = new() { Width = 360, MaxLength = 64 };
    private readonly Label _nameHint = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly Label _languageLabel = new() { AutoSize = true };
    private readonly ComboBox _language = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 360 };
    private readonly CheckBox _startWithWindows = new() { AutoSize = true };
    private readonly Label _addressLabel = new() { AutoSize = true };
    private readonly TextBox _address = new() { ReadOnly = true, Width = 360, BorderStyle = BorderStyle.FixedSingle };
    private readonly Label _firewallLabel = new() { AutoSize = true };
    private readonly Label _firewallState = new() { AutoSize = true };
    private readonly Button _firewallButton = new() { AutoSize = true };

    private readonly Label _defaultTvLabel = new() { AutoSize = true };
    private readonly ComboBox _defaultTv = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 360 };
    private readonly Label _defaultTvHint = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly Label _accessTitle = new() { AutoSize = true };
    private readonly ListView _devices = NewListView(checkBoxes: true);
    private readonly Button _allowAll = new() { AutoSize = true };
    private readonly Button _blockAll = new() { AutoSize = true };
    private readonly Button _removeDevice = new() { AutoSize = true };
    private readonly Label _accessHint = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    // Presence also ends when a device stops contacting the server, which raises no event.
    private readonly System.Windows.Forms.Timer _presenceTimer = new() { Interval = 15000 };

    private readonly Button _ok = new() { AutoSize = true, MinimumSize = new Size(90, 0) };
    private readonly Button _cancel = new() { AutoSize = true, MinimumSize = new Size(90, 0), DialogResult = DialogResult.Cancel };
    private readonly Button _apply = new() { AutoSize = true, MinimumSize = new Size(90, 0) };

    private bool _loadingDevices;
    private bool _loadingDefaultTv;

    public SettingsForm(AppHost host)
    {
        _host = host;
        _edited = host.Settings.Snapshot();
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Icon = AppIcon.Create();
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(900, 560);
        MinimumSize = new Size(620, 420);
        ShowInTaskbar = false;
        MinimizeBox = false;
        AcceptButton = _ok;
        CancelButton = _cancel;

        _tabs.TabPages.Add(BuildFoldersTab());
        _tabs.TabPages.Add(BuildServerTab());
        _tabs.TabPages.Add(BuildDevicesTab());
        _tabs.TabPages.Add(BuildGeneralTab());

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(8),
        };
        buttons.Controls.AddRange([_apply, _cancel, _ok]);
        Controls.Add(_tabs);
        Controls.Add(buttons);
        ResumeLayout(false);
        PerformLayout();
        _tabs.ScalePadding(14, 5);
        ScaleColumns(_folders, 480, 170);
        ScaleColumns(_devices, 220, 105, 135, 95, 110);
        _folders.KeepLastColumnFilled();
        _devices.KeepLastColumnFilled();

        _ok.Click += (_, _) =>
        {
            if (Save())
            {
                DialogResult = DialogResult.OK;
                Close();
            }
        };
        _apply.Click += (_, _) => Save();

        ApplyTexts();
        LoadValues();
        _host.Access.DevicesChanged += OnDevicesChanged;
        _host.Renderers.Changed += OnRenderersChanged;
        _presenceTimer.Tick += (_, _) => UpdatePresence();
        _presenceTimer.Start();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (DialogResult != DialogResult.OK && HasChanges())
        {
            var answer = MessageBox.Show(this, L.T("settings.saveChanges"), Text, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel || (answer == DialogResult.Yes && !Save()))
            {
                e.Cancel = true;
                return;
            }
        }

        _host.Access.DevicesChanged -= OnDevicesChanged;
        _host.Renderers.Changed -= OnRenderersChanged;
        _presenceTimer.Dispose();
        base.OnFormClosing(e);
    }

    private TabPage BuildFoldersTab()
    {
        var page = new TabPage { Padding = new Padding(10) };
        _folders.Columns.Add("", 480);
        _folders.Columns.Add("", 150);
        _folders.SelectedIndexChanged += (_, _) => UpdateFolderButtons();
        _folders.DoubleClick += (_, _) => _includeSubfolders.Checked = !_includeSubfolders.Checked;

        var side = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.TopDown, AutoSize = true, Padding = new Padding(8, 0, 0, 0) };
        side.Controls.AddRange([_addFolder, _removeFolder, _refresh]);
        foreach (Control c in side.Controls)
        {
            c.MinimumSize = new Size(130, 0);
        }

        _includeSubfolders.Dock = DockStyle.Bottom;
        _includeSubfolders.Padding = new Padding(0, 6, 0, 0);
        _includeSubfolders.CheckedChanged += (_, _) => OnIncludeSubfoldersChanged();
        _addFolder.Click += (_, _) => AddFolder();
        _removeFolder.Click += (_, _) => RemoveFolder();
        _refresh.Click += async (_, _) =>
        {
            if (Save())
            {
                await _host.RefreshAsync();
            }
        };

        page.Controls.Add(_folders);
        page.Controls.Add(_includeSubfolders);
        page.Controls.Add(side);
        return page;
    }

    private TabPage BuildServerTab()
    {
        var page = new TabPage { Padding = new Padding(10) };
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
        grid.Controls.Add(_nameLabel);
        grid.Controls.Add(_name);
        grid.Controls.Add(_nameHint);
        grid.Controls.Add(Spacer());
        grid.Controls.Add(_addressLabel);
        grid.Controls.Add(_address);
        grid.Controls.Add(Spacer());
        grid.Controls.Add(_firewallLabel);
        var firewallRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        _firewallState.Margin = new Padding(0, 7, 12, 0);
        firewallRow.Controls.AddRange([_firewallState, _firewallButton]);
        grid.Controls.Add(firewallRow);
        page.Controls.Add(grid);

        _firewallButton.Click += async (_, _) =>
        {
            _firewallButton.Enabled = false;
            var added = await Task.Run(() => FirewallHelper.AllowInbound(AppPaths.ExecutablePath));
            if (!added)
            {
                MessageBox.Show(this, L.T("firewall.failed"), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            await RefreshFirewallStateAsync();
        };
        return page;
    }

    private TabPage BuildGeneralTab()
    {
        var page = new TabPage { Padding = new Padding(10) };
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
        grid.Controls.Add(_languageLabel);
        grid.Controls.Add(_language);
        grid.Controls.Add(Spacer());
        grid.Controls.Add(_startWithWindows);
        page.Controls.Add(grid);
        return page;
    }

    private TabPage BuildDevicesTab()
    {
        var page = new TabPage { Padding = new Padding(10) };
        var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
        top.Controls.Add(_defaultTvLabel);
        top.Controls.Add(_defaultTv);
        top.Controls.Add(_defaultTvHint);
        top.Controls.Add(Spacer());
        top.Controls.Add(_accessTitle);
        _defaultTv.SelectedIndexChanged += (_, _) => OnDefaultTvChanged();

        _devices.Columns.Add("", 260);
        _devices.Columns.Add("", 120);
        _devices.Columns.Add("", 150);
        _devices.Columns.Add("", 110);
        _devices.Columns.Add("", 110);
        _devices.ItemChecked += (_, e) => OnDeviceChecked(e.Item);
        _devices.SelectedIndexChanged += (_, _) => _removeDevice.Enabled = _devices.SelectedItems.Count > 0;

        var side = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.TopDown, AutoSize = true, Padding = new Padding(8, 0, 0, 0) };
        side.Controls.AddRange([_allowAll, _blockAll, _removeDevice]);
        foreach (Control c in side.Controls)
        {
            c.MinimumSize = new Size(130, 0);
        }

        _allowAll.Click += (_, _) => SetAllDevices(true);
        _blockAll.Click += (_, _) => SetAllDevices(false);
        _removeDevice.Click += (_, _) => RemoveDevice();
        _accessHint.Dock = DockStyle.Bottom;
        _accessHint.Padding = new Padding(0, 6, 0, 0);

        // Docking runs from the last added control: the top block spans the full width.
        page.Controls.Add(_devices);
        page.Controls.Add(_accessHint);
        page.Controls.Add(side);
        page.Controls.Add(top);
        return page;
    }

    private void ApplyTexts()
    {
        Text = L.T("settings.title");
        _tabs.TabPages[0].Text = L.T("settings.tabShared");
        _tabs.TabPages[1].Text = L.T("settings.tabServer");
        _tabs.TabPages[2].Text = L.T("settings.tabDevices");
        _tabs.TabPages[3].Text = L.T("settings.tabGeneral");
        _folders.Columns[0].Text = L.T("settings.colFolder");
        _folders.Columns[1].Text = L.T("settings.includeSubfolders");
        _includeSubfolders.Text = L.T("settings.includeSubfolders");
        _addFolder.Text = L.T("settings.add") + "...";
        _removeFolder.Text = L.T("settings.remove");
        _refresh.Text = L.T("settings.refresh");
        _nameLabel.Text = L.T("settings.serverName");
        _nameHint.Text = L.T("settings.deviceNameHint");
        _languageLabel.Text = L.T("settings.language");
        _startWithWindows.Text = L.T("settings.startWithWindows");
        _addressLabel.Text = L.T("settings.serverAddress");
        _firewallLabel.Text = L.T("settings.firewall");
        _firewallButton.Text = L.T("settings.firewallAdd");
        _defaultTvLabel.Text = L.T("settings.defaultTv");
        _defaultTvHint.Text = L.T("settings.defaultTvHint");
        _accessTitle.Text = L.T("settings.accessTitle");
        _devices.Columns[0].Text = L.T("settings.colDevice");
        _devices.Columns[1].Text = L.T("settings.colIp");
        _devices.Columns[2].Text = L.T("settings.colMac");
        _devices.Columns[3].Text = L.T("settings.colStatus");
        _devices.Columns[4].Text = L.T("settings.colPresence");
        _allowAll.Text = L.T("settings.allowAll");
        _blockAll.Text = L.T("settings.blockAll");
        _removeDevice.Text = L.T("settings.remove");
        _accessHint.Text = L.T("settings.accessHint");
        _ok.Text = L.T("ok");
        _cancel.Text = L.T("cancel");
        _apply.Text = L.T("apply");
    }

    private void LoadValues()
    {
        ReloadFolders();
        _name.Text = _edited.ServerName;
        _language.Items.Clear();
        _language.Items.Add(new LanguageChoice(L.AutoLanguage, L.T("settings.languageAuto")));
        foreach (var language in L.Languages)
        {
            _language.Items.Add(new LanguageChoice(language.Code, language.NativeName));
        }

        _language.SelectedIndex = Math.Max(0, _language.Items.Cast<LanguageChoice>().ToList().FindIndex(l => l.Code == _edited.Language));
        _startWithWindows.Checked = _edited.StartWithWindows;
        _address.Text = _host.Server.IsRunning
            ? $"{_host.Server.BaseUrlFor(null)}/description.xml"
            : _host.Server.LastError ?? "-";
        ReloadDefaultTvOptions();
        ReloadDevices();
        _ = RefreshFirewallStateAsync();
    }

    /// <summary>
    /// Compares with the saved settings instead of tracking edits: controls raise change
    /// events on their own, e.g. a list view reports its checked items when first shown.
    /// </summary>
    private bool HasChanges()
    {
        var saved = _host.Settings.Snapshot();
        return ChangeKey(saved, saved.ServerName, saved.Language, saved.StartWithWindows)
               != ChangeKey(_edited, _name.Text.Trim(), (_language.SelectedItem as LanguageChoice)?.Code ?? L.AutoLanguage, _startWithWindows.Checked);
    }

    private static string ChangeKey(AppSettings s, string name, string language, bool startWithWindows) => string.Join("\n",
        name,
        language,
        startWithWindows,
        s.DefaultRendererUdn,
        string.Join("|", s.SharedFolders.Select(f => $"{f.Path}*{f.IncludeSubfolders}")),
        string.Join("|", s.Devices.Select(d => $"{d.Key}*{d.Allowed}").Order(StringComparer.Ordinal)));

    /// <summary>
    /// "(none)" and the TVs that are not blocked in the list below, as edited: online, known
    /// offline, and the saved default TV even if it is unknown. Blocking the default TV clears it.
    /// </summary>
    private void ReloadDefaultTvOptions()
    {
        var online = _host.Renderers.Renderers;
        var options = new List<DefaultTvChoice> { new(null, L.T("settings.defaultTvNone")) };
        options.AddRange(DeviceAccess.PlayTargets(online, _edited.Devices)
            .Select(t => new DefaultTvChoice(t.Udn, t.Renderer != null ? t.Name : t.Name + "  " + L.T("main.notFound"))));
        if (_edited.DefaultRendererUdn is { } udn && options.All(o => o.Udn != udn))
        {
            if (DeviceAccess.IsBlocked(udn, online, _edited.Devices))
            {
                _edited.DefaultRendererUdn = null;
                _edited.DefaultRendererName = null;
            }
            else
            {
                options.Add(new DefaultTvChoice(udn, (_edited.DefaultRendererName ?? udn) + "  " + L.T("main.notFound")));
            }
        }

        _loadingDefaultTv = true;
        try
        {
            _defaultTv.Items.Clear();
            _defaultTv.Items.AddRange([.. options]);
            _defaultTv.SelectedIndex = Math.Max(0, options.FindIndex(o => o.Udn == _edited.DefaultRendererUdn));
        }
        finally
        {
            _loadingDefaultTv = false;
        }
    }

    private void OnDefaultTvChanged()
    {
        if (_loadingDefaultTv || _defaultTv.SelectedItem is not DefaultTvChoice choice)
        {
            return;
        }

        _edited.DefaultRendererUdn = choice.Udn;
        _edited.DefaultRendererName = choice.Udn == null ? null
            : _host.Renderers.Find(choice.Udn)?.FriendlyName
              ?? _edited.Devices.FirstOrDefault(d => d.RendererUdn == choice.Udn)?.Name
              ?? _edited.DefaultRendererName;
    }

    private void OnRenderersChanged(object? sender, EventArgs e)
    {
        if (IsHandleCreated)
        {
            BeginInvoke(() =>
            {
                ReloadDefaultTvOptions();
                UpdatePresence();
            });
        }
    }

    private async Task RefreshFirewallStateAsync()
    {
        _firewallButton.Enabled = false;
        var state = await Task.Run(() => FirewallHelper.Check(AppPaths.ExecutablePath));
        if (IsDisposed)
        {
            return;
        }

        _firewallState.Text = L.T(state switch
        {
            FirewallState.Allowed => "settings.firewallAllowed",
            FirewallState.Disabled => "settings.firewallOff",
            FirewallState.Unknown => "settings.firewallUnknown",
            _ => "settings.firewallMissing",
        });
        _firewallState.ForeColor = state is FirewallState.Missing or FirewallState.Blocked ? Color.Firebrick : SystemColors.ControlText;
        _firewallButton.Enabled = state is not (FirewallState.Allowed or FirewallState.Disabled);
    }

    private void ReloadFolders()
    {
        var selected = _folders.SelectedIndices.Count > 0 ? _folders.SelectedIndices[0] : -1;
        _folders.BeginUpdate();
        _folders.Items.Clear();
        foreach (var folder in _edited.SharedFolders)
        {
            var item = new ListViewItem(MediaLibrary.NormalizeRoot(folder.Path)) { Tag = folder };
            item.SubItems.Add(folder.IncludeSubfolders ? L.T("settings.yes") : L.T("settings.no"));
            _folders.Items.Add(item);
        }

        if (selected >= 0 && selected < _folders.Items.Count)
        {
            _folders.Items[selected].Selected = true;
        }

        _folders.EndUpdate();
        UpdateFolderButtons();
    }

    private void UpdateFolderButtons()
    {
        var folder = SelectedFolder();
        _removeFolder.Enabled = folder != null;
        _includeSubfolders.Enabled = folder != null;
        _includeSubfolders.Checked = folder?.IncludeSubfolders ?? true;
    }

    private SharedFolder? SelectedFolder() =>
        _folders.SelectedItems.Count > 0 ? _folders.SelectedItems[0].Tag as SharedFolder : null;

    private void OnIncludeSubfoldersChanged()
    {
        var folder = SelectedFolder();
        if (folder == null || folder.IncludeSubfolders == _includeSubfolders.Checked)
        {
            return;
        }

        folder.IncludeSubfolders = _includeSubfolders.Checked;
        ReloadFolders();
    }

    private void AddFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = L.T("settings.addFolderMsg"),
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var path = MediaLibrary.NormalizeRoot(dialog.SelectedPath);
        var covered = _edited.SharedFolders.Any(f =>
        {
            var root = MediaLibrary.NormalizeRoot(f.Path);
            return string.Equals(root, path, StringComparison.OrdinalIgnoreCase)
                   || (f.IncludeSubfolders && path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
        });
        if (covered)
        {
            MessageBox.Show(this, L.T("settings.folderAlreadyShared"), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _edited.SharedFolders.Add(new SharedFolder { Path = path, IncludeSubfolders = true });
        ReloadFolders();
        _folders.Items[^1].Selected = true;
    }

    private void RemoveFolder()
    {
        var folder = SelectedFolder();
        if (folder == null
            || MessageBox.Show(this, L.T("settings.removeFolderConfirm"), Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        _edited.SharedFolders.Remove(folder);
        ReloadFolders();
    }

    private void OnDevicesChanged(object? sender, EventArgs e)
    {
        if (!IsHandleCreated)
        {
            return;
        }

        BeginInvoke(() =>
        {
            // Merge newly seen devices without losing unsaved edits.
            var known = _edited.Devices.Select(d => d.Key).ToHashSet();
            _edited.Devices.AddRange(_host.Settings.Read(s => s.Devices.Where(d => !known.Contains(d.Key)).Select(d => d.Clone()).ToList()));
            ReloadDevices();
            ReloadDefaultTvOptions();
        });
    }

    private void ReloadDevices()
    {
        var online = _host.Renderers.Renderers;
        _loadingDevices = true;
        _devices.BeginUpdate();
        try
        {
            _devices.Items.Clear();
            foreach (var device in _edited.Devices.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var item = new ListViewItem(device.Name) { Tag = device, Checked = device.Allowed };
                item.SubItems.Add(device.LastIp ?? "");
                item.SubItems.Add(device.MacAddress ?? "");
                item.SubItems.Add(device.Allowed ? L.T("settings.allowed") : L.T("settings.blocked"));
                item.SubItems.Add(PresenceText(device, online));
                item.ToolTipText = device.LastSeen == default ? "" : device.LastSeen.ToString("g", L.Culture);
                _devices.Items.Add(item);
            }
        }
        finally
        {
            _devices.EndUpdate();
            _loadingDevices = false;
        }

        _removeDevice.Enabled = _devices.SelectedItems.Count > 0;
    }

    private void UpdatePresence()
    {
        var online = _host.Renderers.Renderers;
        foreach (ListViewItem item in _devices.Items)
        {
            if (item.Tag is DeviceRule device)
            {
                var text = PresenceText(device, online);
                if (item.SubItems[4].Text != text)
                {
                    item.SubItems[4].Text = text;
                }
            }
        }
    }

    /// <summary>Present or not for TVs; devices that only browse the server are not TVs.</summary>
    private string PresenceText(DeviceRule device, IReadOnlyList<RendererInfo> online) =>
        L.T(!DeviceAccess.IsTv(device, online, _edited.Devices) ? "settings.notTv"
            : _host.Access.IsPresent(device, online, _edited.Devices) ? "settings.present" : "settings.absent");

    private void OnDeviceChecked(ListViewItem item)
    {
        if (_loadingDevices || item.Tag is not DeviceRule device || device.Allowed == item.Checked)
        {
            return;
        }

        device.Allowed = item.Checked;
        item.SubItems[3].Text = device.Allowed ? L.T("settings.allowed") : L.T("settings.blocked");
        ReloadDefaultTvOptions();
    }

    private void SetAllDevices(bool allowed)
    {
        foreach (var device in _edited.Devices)
        {
            device.Allowed = allowed;
        }

        ReloadDevices();
        ReloadDefaultTvOptions();
    }

    private void RemoveDevice()
    {
        if (_devices.SelectedItems.Count == 0 || _devices.SelectedItems[0].Tag is not DeviceRule device)
        {
            return;
        }

        _edited.Devices.Remove(device);
        ReloadDevices();
        ReloadDefaultTvOptions();
    }

    private bool Save()
    {
        var name = _name.Text.Trim();
        if (name.Length == 0 || name.Any(char.IsControl))
        {
            _tabs.SelectedIndex = ServerTabIndex;
            MessageBox.Show(this, L.T("settings.deviceNameInvalid"), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _name.Focus();
            return false;
        }

        _edited.ServerName = name;
        _edited.Language = (_language.SelectedItem as LanguageChoice)?.Code ?? L.AutoLanguage;
        _edited.StartWithWindows = _startWithWindows.Checked;
        _host.ApplySettings(_edited);
        ApplyTexts();
        ReloadFolders();
        ReloadDefaultTvOptions();
        ReloadDevices();
        return true;
    }

    private static ListView NewListView(bool checkBoxes) => new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = false,
        HideSelection = false,
        CheckBoxes = checkBoxes,
        ShowItemToolTips = true,
    };

    private static Control Spacer() => new Panel { Height = 10, Width = 1 };

    /// <summary>List view columns are not auto-scaled with the form.</summary>
    private void ScaleColumns(ListView list, params int[] widths)
    {
        for (var i = 0; i < widths.Length && i < list.Columns.Count; i++)
        {
            list.Columns[i].Width = LogicalToDeviceUnits(widths[i]);
        }
    }

    private sealed record LanguageChoice(string Code, string Name)
    {
        public override string ToString() => Name;
    }

    private sealed record DefaultTvChoice(string? Udn, string Name)
    {
        public override string ToString() => Name;
    }
}
