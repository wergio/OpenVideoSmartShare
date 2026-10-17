namespace OpenVideoSmartShare.Settings;

/// <summary>A folder whose videos are shared on the network.</summary>
internal sealed class SharedFolder
{
    public string Path { get; set; } = "";

    public bool IncludeSubfolders { get; set; } = true;
}

/// <summary>Access rule for a network device, keyed by MAC address (or "ip:" + address when unknown).</summary>
internal sealed class DeviceRule
{
    public string Key { get; set; } = "";

    public string Name { get; set; } = "";

    public string? MacAddress { get; set; }

    public string? LastIp { get; set; }

    public bool Allowed { get; set; } = true;

    public DateTime LastSeen { get; set; }
}

/// <summary>Persisted user settings.</summary>
internal sealed class AppSettings
{
    public int Version { get; set; } = 1;

    /// <summary>Friendly name shown on TVs.</summary>
    public string ServerName { get; set; } = "";

    /// <summary>Stable UPnP device UUID, generated on first run.</summary>
    public Guid ServerUuid { get; set; }

    /// <summary>Preferred HTTP port; the next free one is used when it is busy.</summary>
    public int HttpPort { get; set; } = 52100;

    public List<SharedFolder> SharedFolders { get; set; } = [];

    /// <summary>UDN of the renderer used by double click.</summary>
    public string? DefaultRendererUdn { get; set; }

    /// <summary>Last known friendly name of the default renderer, shown while it is offline.</summary>
    public string? DefaultRendererName { get; set; }

    /// <summary>UDN of the renderer last selected in the main window.</summary>
    public string? SelectedRendererUdn { get; set; }

    /// <summary>UI language code, or "auto" for the Windows language.</summary>
    public string Language { get; set; } = "auto";

    public bool StartWithWindows { get; set; } = true;

    /// <summary>The user answered "No" to the firewall prompt; it is offered again only from Settings.</summary>
    public bool FirewallPromptDeclined { get; set; }

    public List<DeviceRule> Devices { get; set; } = [];

    public WindowLayout Window { get; set; } = new();

    public static string DefaultServerName() => $"OpenVideoSmartShare {Environment.MachineName}";
}

/// <summary>Main window geometry and list layout.</summary>
internal sealed class WindowLayout
{
    public int Left { get; set; }

    public int Top { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public bool Maximized { get; set; }

    public int[]? ColumnWidths { get; set; }

    public int SortColumn { get; set; }

    public bool SortDescending { get; set; }
}
