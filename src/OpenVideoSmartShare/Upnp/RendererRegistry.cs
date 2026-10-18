using System.Collections.Concurrent;
using System.Net;
using System.Xml.Linq;
using OpenVideoSmartShare.Infrastructure;

namespace OpenVideoSmartShare.Upnp;

/// <summary>A DLNA MediaRenderer (TV) found on the network.</summary>
internal sealed class RendererInfo
{
    public required string Udn { get; init; }

    public required string FriendlyName { get; init; }

    public string? Manufacturer { get; init; }

    public string? ModelName { get; init; }

    public required Uri Location { get; init; }

    public required IPAddress Address { get; init; }

    public required Uri AvTransportControl { get; init; }

    /// <summary>RenderingControl service (volume), when the renderer has one.</summary>
    public Uri? RenderingControl { get; init; }

    public DateTime LastSeen { get; set; }

    public int MaxAgeSeconds { get; set; } = 1800;

    public override string ToString() => FriendlyName;
}

/// <summary>Keeps the list of renderers announced over SSDP, reading their device descriptions.</summary>
internal sealed class RendererRegistry : IDisposable
{
    public const string RendererType = "urn:schemas-upnp-org:device:MediaRenderer:1";
    private static readonly XNamespace DeviceNs = "urn:schemas-upnp-org:device-1-0";

    private readonly SsdpService _ssdp;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(6) };
    private readonly ConcurrentDictionary<string, RendererInfo> _renderers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _fetching = new(StringComparer.OrdinalIgnoreCase);
    private System.Threading.Timer? _timer;
    private int _maintainTicks;

    public RendererRegistry(SsdpService ssdp)
    {
        _ssdp = ssdp;
        _ssdp.MessageReceived += OnMessage;
    }

    /// <summary>Raised (on a worker thread) when renderers appear or disappear.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<RendererInfo> Renderers =>
        _renderers.Values.OrderBy(r => r.FriendlyName, StringComparer.CurrentCultureIgnoreCase).ToList();

    public RendererInfo? Find(string? udn) => udn != null && _renderers.TryGetValue(udn, out var r) ? r : null;

    public void Start()
    {
        // Periodic search plus expiry of devices that stopped announcing themselves.
        _timer = new System.Threading.Timer(_ => Maintain(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        Search();
    }

    /// <summary>Asks renderers to identify themselves (two rounds, as UDP may be lost).</summary>
    public void Search()
    {
        _ = Task.Run(async () =>
        {
            _ssdp.Search(RendererType);
            await Task.Delay(1000).ConfigureAwait(false);
            _ssdp.Search(RendererType);
        });
    }

    public void Dispose()
    {
        _ssdp.MessageReceived -= OnMessage;
        _timer?.Dispose();
        _http.Dispose();
    }

    private void Maintain()
    {
        var now = DateTime.UtcNow;
        var removed = false;
        foreach (var renderer in _renderers.Values)
        {
            if (now - renderer.LastSeen > TimeSpan.FromSeconds(renderer.MaxAgeSeconds + 120)
                && _renderers.TryRemove(renderer.Udn, out _))
            {
                Log.Info($"Renderer expired: {renderer.FriendlyName}");
                removed = true;
            }
        }

        if (removed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        if (++_maintainTicks % 5 == 0)
        {
            Search();
        }
    }

    private void OnMessage(object? sender, SsdpMessage message)
    {
        var type = message.IsNotify ? message["NT"] : message["ST"];
        var usn = message["USN"] ?? "";
        var udn = usn.Split("::")[0];
        if (message.IsNotify && string.Equals(message["NTS"], "ssdp:byebye", StringComparison.OrdinalIgnoreCase))
        {
            if (udn.Length > 0 && _renderers.TryRemove(udn, out var gone))
            {
                Log.Info($"Renderer left: {gone.FriendlyName}");
                Changed?.Invoke(this, EventArgs.Empty);
            }

            return;
        }

        if (!string.Equals(type, RendererType, StringComparison.OrdinalIgnoreCase)
            || !Uri.TryCreate(message["LOCATION"], UriKind.Absolute, out var location))
        {
            return;
        }

        if (_renderers.TryGetValue(udn, out var known) && known.Location == location)
        {
            known.LastSeen = DateTime.UtcNow;
            known.MaxAgeSeconds = message.MaxAge;
            return;
        }

        if (_fetching.TryAdd(location.AbsoluteUri, 0))
        {
            _ = FetchAsync(location, message.MaxAge);
        }
    }

    private async Task FetchAsync(Uri location, int maxAge)
    {
        try
        {
            var xml = await _http.GetStringAsync(location).ConfigureAwait(false);
            var renderer = ParseDescription(xml, location);
            if (renderer == null)
            {
                return;
            }

            renderer.LastSeen = DateTime.UtcNow;
            renderer.MaxAgeSeconds = maxAge;
            _renderers[renderer.Udn] = renderer;
            Log.Info($"Renderer found: {renderer.FriendlyName} ({renderer.Address}) {renderer.Udn}");
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException)
        {
            Log.Warn($"Cannot read renderer description {location}", ex);
        }
        finally
        {
            _fetching.TryRemove(location.AbsoluteUri, out _);
        }
    }

    /// <summary>Reads a MediaRenderer description; null when it has no AVTransport service.</summary>
    internal static RendererInfo? ParseDescription(string xml, Uri location)
    {
        var doc = XDocument.Parse(xml);
        var root = doc.Root;
        if (root == null)
        {
            return null;
        }

        var baseUri = Uri.TryCreate(root.Element(DeviceNs + "URLBase")?.Value?.Trim(), UriKind.Absolute, out var urlBase)
            ? urlBase
            : location;
        var device = root.Descendants(DeviceNs + "device")
            .FirstOrDefault(d => (d.Element(DeviceNs + "deviceType")?.Value ?? "").Contains(":MediaRenderer:", StringComparison.OrdinalIgnoreCase));
        if (device == null)
        {
            return null;
        }

        string? ControlUrl(string service) => device.Element(DeviceNs + "serviceList")?.Elements(DeviceNs + "service")
            .FirstOrDefault(s => (s.Element(DeviceNs + "serviceType")?.Value ?? "").Contains($":{service}:", StringComparison.OrdinalIgnoreCase))
            ?.Element(DeviceNs + "controlURL")?.Value?.Trim();
        var control = ControlUrl("AVTransport");
        var renderingControl = ControlUrl("RenderingControl");
        var udn = device.Element(DeviceNs + "UDN")?.Value?.Trim();
        if (string.IsNullOrEmpty(control) || string.IsNullOrEmpty(udn))
        {
            return null;
        }

        return new RendererInfo
        {
            Udn = udn,
            FriendlyName = device.Element(DeviceNs + "friendlyName")?.Value?.Trim() is { Length: > 0 } name ? name : location.Host,
            Manufacturer = device.Element(DeviceNs + "manufacturer")?.Value?.Trim(),
            ModelName = device.Element(DeviceNs + "modelName")?.Value?.Trim(),
            Location = location,
            Address = IPAddress.TryParse(location.Host, out var address) ? address : IPAddress.None,
            AvTransportControl = new Uri(baseUri, control),
            RenderingControl = string.IsNullOrEmpty(renderingControl) ? null : new Uri(baseUri, renderingControl),
        };
    }
}
