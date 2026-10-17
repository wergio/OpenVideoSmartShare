using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using OpenVideoSmartShare.Infrastructure;

namespace OpenVideoSmartShare.Upnp;

/// <summary>A received SSDP datagram (M-SEARCH, NOTIFY or a search response).</summary>
internal sealed class SsdpMessage
{
    private readonly Dictionary<string, string> _headers;

    private SsdpMessage(string startLine, Dictionary<string, string> headers, IPEndPoint remote)
    {
        StartLine = startLine;
        _headers = headers;
        Remote = remote;
    }

    public string StartLine { get; }

    public IPEndPoint Remote { get; }

    public bool IsSearch => StartLine.StartsWith("M-SEARCH", StringComparison.OrdinalIgnoreCase);

    public bool IsNotify => StartLine.StartsWith("NOTIFY", StringComparison.OrdinalIgnoreCase);

    public bool IsResponse => StartLine.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase);

    public string? this[string header] => _headers.TryGetValue(header, out var value) ? value : null;

    /// <summary>"max-age" of CACHE-CONTROL in seconds (1800 when absent).</summary>
    public int MaxAge
    {
        get
        {
            var cache = this["CACHE-CONTROL"] ?? "";
            var index = cache.IndexOf("max-age", StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                var digits = new string(cache[(index + 7)..].SkipWhile(c => c is ' ' or '=').TakeWhile(char.IsDigit).ToArray());
                if (int.TryParse(digits, out var seconds) && seconds > 0)
                {
                    return seconds;
                }
            }

            return 1800;
        }
    }

    public static SsdpMessage? Parse(ReadOnlySpan<byte> data, IPEndPoint remote)
    {
        var text = Encoding.UTF8.GetString(data);
        var lines = text.Split("\r\n");
        if (lines.Length == 0 || lines[0].Length == 0)
        {
            return null;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0)
            {
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }

        return new SsdpMessage(lines[0].Trim(), headers, remote);
    }
}

/// <summary>What the local MediaServer advertises; null while the HTTP server is down.</summary>
internal sealed record SsdpAdvertisement(Guid Uuid, int Port, string ServerHeader);

/// <summary>
/// SSDP discovery: advertises the MediaServer (NOTIFY + M-SEARCH replies on UDP 1900) and
/// sends M-SEARCH for renderers from one socket per local interface.
/// </summary>
internal sealed class SsdpService : IDisposable
{
    public static readonly IPAddress MulticastAddress = IPAddress.Parse("239.255.255.250");
    private const int Port = 1900;
    private const int MaxAgeSeconds = 1800;
    private const int SioUdpConnReset = -1744830452;

    private static readonly string[] ServiceTypes =
    [
        DeviceDescription.DeviceType,
        ContentDirectory.ServiceType,
        ConnectionManager.ServiceType,
    ];

    private readonly Func<SsdpAdvertisement?> _advertisement;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<(LocalAddress Address, Socket Socket)> _senders = [];
    private Socket? _listener;
    private System.Threading.Timer? _announceTimer;
    private System.Threading.Timer? _rebuildTimer;
    private bool _started;

    public SsdpService(Func<SsdpAdvertisement?> advertisement) => _advertisement = advertisement;

    /// <summary>Raised for NOTIFY messages and M-SEARCH responses from other devices (worker thread).</summary>
    public event EventHandler<SsdpMessage>? MessageReceived;

    public void Start()
    {
        lock (_gate)
        {
            if (_started)
            {
                return;
            }

            _started = true;
            OpenSockets();
        }

        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        _announceTimer = new System.Threading.Timer(_ => AnnounceAlive(), null, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
    }

    /// <summary>Sends ssdp:alive for every advertised type on every interface (twice, as UDP is lossy).</summary>
    public void AnnounceAlive()
    {
        _ = Task.Run(async () =>
        {
            for (var round = 0; round < 2 && !_shutdown.IsCancellationRequested; round++)
            {
                SendNotify("ssdp:alive");
                await Task.Delay(300).ConfigureAwait(false);
            }
        });
    }

    /// <summary>Sends ssdp:byebye synchronously (used on exit and before a rename).</summary>
    public void AnnounceByeBye() => SendNotify("ssdp:byebye");

    /// <summary>Multicasts an M-SEARCH for <paramref name="searchTarget"/> on every interface.</summary>
    public void Search(string searchTarget)
    {
        var message = Encoding.ASCII.GetBytes(
            "M-SEARCH * HTTP/1.1\r\n" +
            "HOST: 239.255.255.250:1900\r\n" +
            "MAN: \"ssdp:discover\"\r\n" +
            "MX: 2\r\n" +
            $"ST: {searchTarget}\r\n" +
            "\r\n");
        foreach (var (_, socket) in Senders())
        {
            TrySend(socket, message, new IPEndPoint(MulticastAddress, Port));
        }
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        _announceTimer?.Dispose();
        _rebuildTimer?.Dispose();
        _shutdown.Cancel();
        lock (_gate)
        {
            CloseSockets();
        }
    }

    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        // Debounce: adapters often report several changes in a row.
        _rebuildTimer?.Dispose();
        _rebuildTimer = new System.Threading.Timer(_ =>
        {
            Log.Info("Network change detected, reopening SSDP sockets");
            NetworkHelper.InvalidateCache();
            lock (_gate)
            {
                if (_shutdown.IsCancellationRequested)
                {
                    return;
                }

                CloseSockets();
                OpenSockets();
            }

            AnnounceAlive();
        }, null, TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
    }

    private List<(LocalAddress Address, Socket Socket)> Senders()
    {
        lock (_gate)
        {
            return [.. _senders];
        }
    }

    private void OpenSockets()
    {
        var addresses = NetworkHelper.GetLocalAddresses();
        try
        {
            var listener = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            listener.ExclusiveAddressUse = false;
            listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            DisableConnReset(listener);
            listener.Bind(new IPEndPoint(IPAddress.Any, Port));
            foreach (var address in addresses)
            {
                try
                {
                    listener.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                        new MulticastOption(MulticastAddress, address.Address));
                }
                catch (SocketException ex)
                {
                    Log.Warn($"Cannot join SSDP group on {address.Address}", ex);
                }
            }

            _listener = listener;
            _ = ReceiveLoopAsync(listener, isListener: true);
        }
        catch (SocketException ex)
        {
            Log.Error("Cannot bind UDP 1900: the server will not answer TV searches", ex);
        }

        foreach (var address in addresses)
        {
            try
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                DisableConnReset(socket);
                socket.Bind(new IPEndPoint(address.Address, 0));
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.Address.GetAddressBytes());
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 4);
                _senders.Add((address, socket));
                _ = ReceiveLoopAsync(socket, isListener: false);
            }
            catch (SocketException ex)
            {
                Log.Warn($"Cannot open SSDP socket on {address.Address}", ex);
            }
        }

        Log.Info($"SSDP active on {string.Join(", ", _senders.Select(s => s.Address.Address))}");
    }

    private void CloseSockets()
    {
        _listener?.Dispose();
        _listener = null;
        foreach (var (_, socket) in _senders)
        {
            socket.Dispose();
        }

        _senders.Clear();
    }

    private static void DisableConnReset(Socket socket)
    {
        // Without this an ICMP "port unreachable" makes the next ReceiveFrom throw on Windows.
        try
        {
            socket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
        }
        catch (SocketException)
        {
        }
    }

    private async Task ReceiveLoopAsync(Socket socket, bool isListener)
    {
        var buffer = new byte[8192];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!_shutdown.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, _shutdown.Token).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize)
            {
                continue;
            }
            catch (SocketException ex)
            {
                Log.Warn("SSDP receive failed", ex);
                return;
            }

            var remote = (IPEndPoint)result.RemoteEndPoint;
            var message = SsdpMessage.Parse(buffer.AsSpan(0, result.ReceivedBytes), remote);
            if (message == null)
            {
                continue;
            }

            try
            {
                if (message.IsSearch && isListener)
                {
                    HandleSearch(message);
                }
                else if (message.IsNotify || message.IsResponse)
                {
                    MessageReceived?.Invoke(this, message);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("SSDP message handling failed", ex);
            }
        }
    }

    private void HandleSearch(SsdpMessage message)
    {
        var advertisement = _advertisement();
        if (advertisement == null || !string.Equals(message["MAN"]?.Trim('"'), "ssdp:discover", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var targets = MatchSearchTarget(message["ST"] ?? "", advertisement.Uuid);
        if (targets.Count == 0)
        {
            return;
        }

        var local = NetworkHelper.GetAddressFor(message.Remote.Address);
        var location = $"http://{local}:{advertisement.Port}/description.xml";
        var listener = _listener;
        if (listener == null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            // Spread replies a little, as recommended by the UPnP spec (MX).
            await Task.Delay(Random.Shared.Next(20, 300)).ConfigureAwait(false);
            foreach (var (st, usn) in targets)
            {
                var response = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\n" +
                    $"CACHE-CONTROL: max-age={MaxAgeSeconds}\r\n" +
                    $"DATE: {DateTime.UtcNow:R}\r\n" +
                    "EXT:\r\n" +
                    $"LOCATION: {location}\r\n" +
                    $"SERVER: {advertisement.ServerHeader}\r\n" +
                    $"ST: {st}\r\n" +
                    $"USN: {usn}\r\n" +
                    "Content-Length: 0\r\n" +
                    "\r\n");
                TrySend(listener, response, message.Remote);
            }
        });
    }

    /// <summary>(ST, USN) pairs the device must answer for a search target.</summary>
    internal static List<(string St, string Usn)> MatchSearchTarget(string searchTarget, Guid uuid)
    {
        var udn = "uuid:" + uuid;
        var all = new List<(string, string)>
        {
            ("upnp:rootdevice", udn + "::upnp:rootdevice"),
            (udn, udn),
        };
        all.AddRange(ServiceTypes.Select(t => (t, udn + "::" + t)));

        if (string.Equals(searchTarget, "ssdp:all", StringComparison.OrdinalIgnoreCase))
        {
            return all;
        }

        return all.Where(t => string.Equals(t.Item1, searchTarget, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void SendNotify(string nts)
    {
        var advertisement = _advertisement();
        if (advertisement == null)
        {
            return;
        }

        foreach (var (address, socket) in Senders())
        {
            var location = $"http://{address.Address}:{advertisement.Port}/description.xml";
            foreach (var (nt, usn) in MatchSearchTarget("ssdp:all", advertisement.Uuid))
            {
                var text = "NOTIFY * HTTP/1.1\r\n" +
                           "HOST: 239.255.255.250:1900\r\n" +
                           $"NT: {nt}\r\n" +
                           $"NTS: {nts}\r\n" +
                           $"USN: {usn}\r\n";
                if (nts == "ssdp:alive")
                {
                    text += $"CACHE-CONTROL: max-age={MaxAgeSeconds}\r\n" +
                            $"LOCATION: {location}\r\n" +
                            $"SERVER: {advertisement.ServerHeader}\r\n";
                }

                TrySend(socket, Encoding.ASCII.GetBytes(text + "\r\n"), new IPEndPoint(MulticastAddress, Port));
            }
        }
    }

    private static void TrySend(Socket socket, byte[] data, EndPoint target)
    {
        try
        {
            socket.SendTo(data, target);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            Log.Warn($"SSDP send to {target} failed", ex);
        }
    }
}
