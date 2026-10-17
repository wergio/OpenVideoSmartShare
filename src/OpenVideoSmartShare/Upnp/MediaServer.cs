using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;
using OpenVideoSmartShare.Devices;
using OpenVideoSmartShare.Infrastructure;
using OpenVideoSmartShare.Library;
using OpenVideoSmartShare.Localization;
using OpenVideoSmartShare.Settings;

namespace OpenVideoSmartShare.Upnp;

/// <summary>
/// HTTP side of the DLNA MediaServer: device/service descriptions, ContentDirectory and
/// ConnectionManager control, GENA eventing, and file streaming with byte ranges.
/// </summary>
internal sealed class MediaServer : IAsyncDisposable
{
    private const int PortAttempts = 20;
    private const int CopyBufferSize = 256 * 1024;

    private readonly SettingsStore _settings;
    private readonly MediaLibrary _library;
    private readonly DeviceAccess _access;
    private readonly string _version;
    private readonly EventSubscriptions _events = new();
    private readonly Dictionary<string, byte[]> _icons = new(StringComparer.Ordinal);
    private WebApplication? _app;

    public MediaServer(SettingsStore settings, MediaLibrary library, DeviceAccess access, string version)
    {
        _settings = settings;
        _library = library;
        _access = access;
        _version = version;
        ServerHeader = $"Windows/{Environment.OSVersion.Version.Major}.{Environment.OSVersion.Version.Minor} UPnP/1.0 DLNADOC/1.50 OpenVideoSmartShare/{version}";
    }

    /// <summary>Value of the SERVER header used by HTTP and SSDP.</summary>
    public string ServerHeader { get; }

    public int Port { get; private set; }

    public bool IsRunning => _app != null;

    public string? LastError { get; private set; }

    /// <summary>Clients that are streaming a video right now.</summary>
    public StreamTracker Streams { get; } = new();

    /// <summary>Base URL ("http://ip:port") that a device at <paramref name="remote"/> can reach.</summary>
    public string BaseUrlFor(IPAddress? remote) => $"http://{NetworkHelper.GetAddressFor(remote)}:{Port}";

    public string DescriptionUrl(IPAddress local) => $"http://{local}:{Port}/description.xml";

    public async Task StartAsync()
    {
        var preferred = _settings.Read(s => s.HttpPort);
        for (var port = preferred; port < preferred + PortAttempts; port++)
        {
            var app = Build(port);
            try
            {
                await app.StartAsync().ConfigureAwait(false);
                _app = app;
                Port = port;
                LastError = null;
                Log.Info($"HTTP server listening on port {port}");
                return;
            }
            catch (Exception ex) when (IsAddressInUse(ex))
            {
                Log.Warn($"Port {port} busy, trying the next one");
                await app.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Log.Error("HTTP server failed to start", ex);
                await app.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        LastError = $"No free TCP port in {preferred}-{preferred + PortAttempts - 1}";
        throw new InvalidOperationException(LastError);
    }

    /// <summary>Tells event subscribers (TVs browsing the server) that the content changed.</summary>
    public void NotifyContentChanged()
    {
        var updateId = _library.Snapshot.UpdateId.ToString(CultureInfo.InvariantCulture);
        _events.Notify(ContentDirectory.ServiceType,
            EventSubscriptions.PropertySet(("SystemUpdateID", updateId), ("ContainerUpdateIDs", "")));
    }

    public async ValueTask DisposeAsync()
    {
        if (_app != null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await _app.StopAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            await _app.DisposeAsync().ConfigureAwait(false);
            _app = null;
        }

        _events.Dispose();
    }

    private WebApplication Build(int port)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            Args = [],
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            // A paused TV stops reading; the default minimum data rate would drop the connection.
            options.Limits.MinResponseDataRate = null;
            options.Limits.MinRequestBodyDataRate = null;
            options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(5);
            options.Listen(IPAddress.Any, port, listen => listen.Protocols = HttpProtocols.Http1);
        });
        var app = builder.Build();
        app.Run(HandleAsync);
        return app;
    }

    private static bool IsAddressInUse(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse or SocketError.AccessDenied }
                || e is IOException && e.Message.Contains("address", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async Task HandleAsync(HttpContext context)
    {
        var request = context.Request;
        var response = context.Response;
        var path = request.Path.Value ?? "/";
        var method = request.Method.ToUpperInvariant();
        var remote = context.Connection.RemoteIpAddress ?? IPAddress.None;
        if (remote.IsIPv4MappedToIPv6)
        {
            remote = remote.MapToIPv4();
        }

        response.Headers["Server"] = ServerHeader;
        var userAgent = request.Headers.UserAgent.ToString();
        var range = request.Headers.Range.ToString();
        Log.Info($"HTTP {method} {path} from {remote}" +
                 (range.Length > 0 ? $" Range={range}" : "") +
                 (userAgent.Length > 0 ? $" UA={userAgent}" : ""));

        try
        {
            switch (path)
            {
                case "/description.xml" when method is "GET" or "HEAD":
                    var (uuid, name) = _settings.Read(s => (s.ServerUuid, s.ServerName));
                    await WriteXmlAsync(context, DeviceDescription.Build(uuid, name, _version)).ConfigureAwait(false);
                    return;
                case "/cds.xml" when method is "GET" or "HEAD":
                    await WriteXmlAsync(context, DeviceDescription.ContentDirectoryScpd).ConfigureAwait(false);
                    return;
                case "/cms.xml" when method is "GET" or "HEAD":
                    await WriteXmlAsync(context, DeviceDescription.ConnectionManagerScpd).ConfigureAwait(false);
                    return;
                case "/cds/control" when method == "POST":
                    await HandleControlAsync(context, remote, userAgent, isContentDirectory: true).ConfigureAwait(false);
                    return;
                case "/cms/control" when method == "POST":
                    await HandleControlAsync(context, remote, userAgent, isContentDirectory: false).ConfigureAwait(false);
                    return;
                case "/cds/event" or "/cms/event":
                    HandleEventSubscription(context, path == "/cds/event" ? ContentDirectory.ServiceType : ConnectionManager.ServiceType);
                    return;
                case "/":
                    response.ContentType = "text/plain; charset=utf-8";
                    await response.WriteAsync($"OpenVideoSmartShare {_version}").ConfigureAwait(false);
                    return;
            }

            if (method is "GET" or "HEAD")
            {
                if (path.StartsWith("/media/", StringComparison.Ordinal))
                {
                    await ServeMediaAsync(context, remote, userAgent, path).ConfigureAwait(false);
                    return;
                }

                if (path.StartsWith("/subtitle/", StringComparison.Ordinal))
                {
                    await ServeSubtitleAsync(context, remote, userAgent, path).ConfigureAwait(false);
                    return;
                }

                if (path.StartsWith("/icon/", StringComparison.Ordinal))
                {
                    await ServeIconAsync(context, path).ConfigureAwait(false);
                    return;
                }
            }

            response.StatusCode = StatusCodes.Status404NotFound;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException && context.RequestAborted.IsCancellationRequested)
        {
            // The TV closed the connection (seek, stop, buffering): nothing to report.
        }
        catch (Exception ex)
        {
            Log.Error($"HTTP {method} {path} failed", ex);
            if (!response.HasStarted)
            {
                response.StatusCode = StatusCodes.Status500InternalServerError;
            }
        }
    }

    private async Task HandleControlAsync(HttpContext context, IPAddress remote, string userAgent, bool isContentDirectory)
    {
        string body;
        using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8))
        {
            body = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);
        }

        var soap = Soap.Parse(body);
        if (soap == null)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        string envelope;
        try
        {
            if (isContentDirectory)
            {
                var allowed = _access.IsAllowed(remote, userAgent);
                var baseUrl = LocalBaseUrl(context);
                envelope = ContentDirectory.Handle(soap, _library.Snapshot, allowed, baseUrl, L.T);
                if (soap.Action == "Browse")
                {
                    Log.Info($"Browse {soap.Get("BrowseFlag")} {soap.Get("ObjectID")} [{soap.Get("StartingIndex")}+{soap.Get("RequestedCount")}]" +
                             (allowed ? "" : " (blocked device)"));
                }
            }
            else
            {
                envelope = ConnectionManager.Handle(soap);
            }
        }
        catch (UpnpException ex)
        {
            Log.Warn($"SOAP {soap.Action} -> UPnP error {ex.Code} {ex.Message}");
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await WriteXmlAsync(context, Soap.Fault(ex.Code, ex.Message)).ConfigureAwait(false);
            return;
        }

        context.Response.Headers["EXT"] = "";
        await WriteXmlAsync(context, envelope).ConfigureAwait(false);
    }

    private void HandleEventSubscription(HttpContext context, string service)
    {
        var request = context.Request;
        var response = context.Response;
        var sid = request.Headers["SID"].ToString();
        switch (request.Method.ToUpperInvariant())
        {
            case "SUBSCRIBE":
                var result = _events.Subscribe(service, sid, request.Headers["CALLBACK"].ToString(),
                    request.Headers["TIMEOUT"].ToString(), () => InitialEvent(service));
                if (result == null)
                {
                    response.StatusCode = StatusCodes.Status412PreconditionFailed;
                    return;
                }

                response.Headers["SID"] = result.Value.Sid;
                response.Headers["TIMEOUT"] = "Second-" + result.Value.TimeoutSeconds.ToString(CultureInfo.InvariantCulture);
                response.ContentLength = 0;
                return;
            case "UNSUBSCRIBE":
                response.StatusCode = _events.Unsubscribe(sid) ? StatusCodes.Status200OK : StatusCodes.Status412PreconditionFailed;
                return;
            default:
                response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                return;
        }
    }

    private string InitialEvent(string service) => service == ContentDirectory.ServiceType
        ? EventSubscriptions.PropertySet(
            ("SystemUpdateID", _library.Snapshot.UpdateId.ToString(CultureInfo.InvariantCulture)),
            ("ContainerUpdateIDs", ""))
        : EventSubscriptions.PropertySet(
            ("SourceProtocolInfo", ConnectionManager.SourceProtocolInfo()),
            ("SinkProtocolInfo", ""),
            ("CurrentConnectionIDs", "0"));

    private async Task ServeMediaAsync(HttpContext context, IPAddress remote, string userAgent, string path)
    {
        // /media/{id}/{file name}
        var id = path.Split('/', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1) ?? "";
        if (!_library.Snapshot.ById.TryGetValue(id, out var item))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!_access.IsAllowed(remote, userAgent))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var headers = context.Response.Headers;
        headers["ContentFeatures.DLNA.ORG"] = Didl.VideoFeatures;
        headers["TransferMode.DLNA.ORG"] = RequestedTransferMode(context.Request, "Streaming");
        if (item.SubtitlePath != null && context.Request.Headers.ContainsKey("getCaptionInfo.sec"))
        {
            headers["CaptionInfo.sec"] = Didl.SubtitleUrl(LocalBaseUrl(context), item);
        }

        // HEAD only checks the file (the TV sends it on SetAVTransportURI): not a stream.
        using var stream = HttpMethods.IsHead(context.Request.Method) ? null : Streams.Begin(remote);
        await ServeFileAsync(context, item.FullPath, item.MimeType).ConfigureAwait(false);
    }

    private async Task ServeSubtitleAsync(HttpContext context, IPAddress remote, string userAgent, string path)
    {
        // /subtitle/{id}.{ext}
        var name = path["/subtitle/".Length..];
        var id = name.Split('.')[0];
        if (!_library.Snapshot.ById.TryGetValue(id, out var item) || item.SubtitlePath == null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!_access.IsAllowed(remote, userAgent))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        context.Response.Headers["TransferMode.DLNA.ORG"] = RequestedTransferMode(context.Request, "Background");
        await ServeFileAsync(context, item.SubtitlePath, MediaTypes.GetSubtitleMime(item.SubtitleExtension!)).ConfigureAwait(false);
    }

    private async Task ServeIconAsync(HttpContext context, string path)
    {
        // /icon/{size}.{png|jpg}
        var name = path["/icon/".Length..];
        var parts = name.Split('.');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var size) || size is < 16 or > 256 || parts[1] is not ("png" or "jpg"))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        byte[] bytes;
        lock (_icons)
        {
            if (!_icons.TryGetValue(name, out bytes!))
            {
                bytes = AppIcon.Render(size, jpeg: parts[1] == "jpg");
                _icons[name] = bytes;
            }
        }

        context.Response.ContentType = parts[1] == "jpg" ? "image/jpeg" : "image/png";
        context.Response.ContentLength = bytes.Length;
        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await context.Response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
        }
    }

    private static async Task ServeFileAsync(HttpContext context, string filePath, string mime)
    {
        var response = context.Response;
        FileStream stream;
        try
        {
            stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Cannot open {filePath}", ex);
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await using (stream.ConfigureAwait(false))
        {
            var size = stream.Length;
            response.Headers["Accept-Ranges"] = "bytes";
            response.ContentType = mime;

            long start = 0;
            var length = size;
            switch (ByteRange.TryParse(context.Request.Headers.Range.ToString(), size, out var range))
            {
                case RangeParse.Unsatisfiable:
                    response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                    response.Headers["Content-Range"] = $"bytes */{size}";
                    return;
                case RangeParse.Range:
                    start = range.Start;
                    length = range.Length;
                    response.StatusCode = StatusCodes.Status206PartialContent;
                    response.Headers["Content-Range"] = $"bytes {range.Start}-{range.End}/{size}";
                    break;
            }

            response.ContentLength = length;
            if (HttpMethods.IsHead(context.Request.Method) || length == 0)
            {
                return;
            }

            stream.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[CopyBufferSize];
            var remaining = length;
            var aborted = context.RequestAborted;
            while (remaining > 0)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), aborted).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await response.Body.WriteAsync(buffer.AsMemory(0, read), aborted).ConfigureAwait(false);
                remaining -= read;
            }
        }
    }

    private static string RequestedTransferMode(HttpRequest request, string fallback)
    {
        var requested = request.Headers["transferMode.dlna.org"].ToString();
        return requested is "Streaming" or "Interactive" or "Background" ? requested : fallback;
    }

    /// <summary>Base URL using the local address the request arrived on.</summary>
    private string LocalBaseUrl(HttpContext context)
    {
        var local = context.Connection.LocalIpAddress;
        if (local == null || IPAddress.IsLoopback(local) || local.Equals(IPAddress.Any))
        {
            return BaseUrlFor(context.Connection.RemoteIpAddress);
        }

        if (local.IsIPv4MappedToIPv6)
        {
            local = local.MapToIPv4();
        }

        return $"http://{local}:{Port}";
    }

    private static async Task WriteXmlAsync(HttpContext context, string xml)
    {
        var bytes = Encoding.UTF8.GetBytes(xml);
        context.Response.ContentType = "text/xml; charset=\"utf-8\"";
        context.Response.ContentLength = bytes.Length;
        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await context.Response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
        }
    }
}
