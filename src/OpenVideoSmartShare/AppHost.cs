using System.Reflection;
using OpenVideoSmartShare.Devices;
using OpenVideoSmartShare.Infrastructure;
using OpenVideoSmartShare.Library;
using OpenVideoSmartShare.Localization;
using OpenVideoSmartShare.Settings;
using OpenVideoSmartShare.Upnp;

namespace OpenVideoSmartShare;

/// <summary>Owns the services (library, DLNA server, SSDP, renderer control) for the app lifetime.</summary>
internal sealed class AppHost : IAsyncDisposable
{
    public AppHost(SettingsStore settings)
    {
        Settings = settings;
        Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        Library = new MediaLibrary(settings, AppPaths.LibraryCacheFile);
        Access = new DeviceAccess(settings);
        Server = new MediaServer(settings, Library, Access, Version);
        Ssdp = new SsdpService(() => Server.IsRunning
            ? new SsdpAdvertisement(Settings.Read(s => s.ServerUuid), Server.Port, Server.ServerHeader)
            : null);
        Renderers = new RendererRegistry(Ssdp);
        Transport = new AvTransportClient();

        Library.SnapshotChanged += (_, _) => Server.NotifyContentChanged();
        Renderers.Changed += (_, _) => RegisterRenderersAsDevices();
    }

    public string Version { get; }

    public SettingsStore Settings { get; }

    public MediaLibrary Library { get; }

    public DeviceAccess Access { get; }

    public MediaServer Server { get; }

    public SsdpService Ssdp { get; }

    public RendererRegistry Renderers { get; }

    public AvTransportClient Transport { get; }

    public async Task StartAsync()
    {
        Library.LoadCache();
        try
        {
            await Server.StartAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("DLNA server not started", ex);
        }

        Ssdp.Start();
        Ssdp.AnnounceAlive();
        Renderers.Start();

        // The shared folders are always re-read at startup.
        _ = Library.RefreshAsync();
    }

    /// <summary>The Refresh button: rescans the folders and searches TVs again.</summary>
    public Task RefreshAsync()
    {
        Renderers.Search();
        return Library.RefreshAsync();
    }

    /// <summary>Sends a video to a renderer, using the address of ours that the renderer can reach.</summary>
    public async Task PlayAsync(RendererInfo renderer, VideoItem item)
    {
        var baseUrl = Server.BaseUrlFor(renderer.Address);
        var uri = Didl.MediaUrl(baseUrl, item);
        var metadata = Didl.ForItem(item, item.ParentId, baseUrl);
        Log.Info($"Play {item.FullPath} on {renderer.FriendlyName} as {uri}");
        await Transport.PlayAsync(renderer, uri, metadata).ConfigureAwait(false);
    }

    /// <summary>Renderers that are streaming a video from the server right now.</summary>
    public IReadOnlyList<RendererInfo> StreamingRenderers()
    {
        var clients = Server.Streams.ActiveClients();
        return clients.Count == 0 ? [] : Renderers.Renderers.Where(r => clients.Contains(r.Address)).ToList();
    }

    /// <summary>Stops playback on a renderer.</summary>
    public async Task StopAsync(RendererInfo renderer)
    {
        Log.Info($"Stop on {renderer.FriendlyName}");
        await Transport.StopAsync(renderer).ConfigureAwait(false);
        // The TV may keep the connection open for a moment: do not count it as streaming.
        Server.Streams.Reset(renderer.Address);
    }

    /// <summary>Saves edited settings and applies what changed.</summary>
    public void ApplySettings(AppSettings edited)
    {
        var before = Settings.Snapshot();
        // Copies: the settings dialog keeps editing its own instances after "Apply".
        Settings.Update(s =>
        {
            s.ServerName = edited.ServerName;
            s.SharedFolders = edited.SharedFolders
                .Select(f => new SharedFolder { Path = f.Path, IncludeSubfolders = f.IncludeSubfolders })
                .ToList();
            s.Language = edited.Language;
            s.StartWithWindows = edited.StartWithWindows;
            s.DefaultRendererUdn = edited.DefaultRendererUdn;
            s.DefaultRendererName = edited.DefaultRendererName;
            s.Devices = edited.Devices.Select(d => new DeviceRule
            {
                Key = d.Key,
                Name = d.Name,
                MacAddress = d.MacAddress,
                LastIp = d.LastIp,
                Allowed = d.Allowed,
                LastSeen = d.LastSeen,
            }).ToList();
        });

        if (before.ServerName != edited.ServerName)
        {
            // TVs cache the friendly name: announce the device as gone and back.
            Ssdp.AnnounceByeBye();
            Ssdp.AnnounceAlive();
        }

        if (before.Language != edited.Language)
        {
            L.SetLanguage(edited.Language);
            Server.NotifyContentChanged();
        }

        if (before.StartWithWindows != edited.StartWithWindows)
        {
            StartupRegistration.Apply(edited.StartWithWindows);
        }

        var foldersBefore = string.Join("|", before.SharedFolders.Select(f => f.Path + f.IncludeSubfolders));
        var foldersAfter = string.Join("|", edited.SharedFolders.Select(f => f.Path + f.IncludeSubfolders));
        if (foldersBefore != foldersAfter)
        {
            _ = Library.RefreshAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Ssdp.AnnounceByeBye();
        Renderers.Dispose();
        Ssdp.Dispose();
        await Server.DisposeAsync().ConfigureAwait(false);
        Transport.Dispose();
        Library.Dispose();
    }

    private void RegisterRenderersAsDevices()
    {
        foreach (var renderer in Renderers.Renderers)
        {
            if (!renderer.Address.Equals(System.Net.IPAddress.None))
            {
                Access.Observe(renderer.Address, renderer.FriendlyName, preferName: true);
            }
        }
    }
}
