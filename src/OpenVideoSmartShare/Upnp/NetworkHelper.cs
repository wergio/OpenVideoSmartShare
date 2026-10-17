using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace OpenVideoSmartShare.Upnp;

/// <summary>An IPv4 address of a local network interface.</summary>
internal sealed record LocalAddress(IPAddress Address, IPAddress Mask, int InterfaceIndex, string InterfaceName, bool HasGateway)
{
    public bool Contains(IPAddress other)
    {
        if (other.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var a = Address.GetAddressBytes();
        var b = other.GetAddressBytes();
        var m = Mask.GetAddressBytes();
        for (var i = 0; i < 4; i++)
        {
            if ((a[i] & m[i]) != (b[i] & m[i]))
            {
                return false;
            }
        }

        return true;
    }
}

internal static class NetworkHelper
{
    private static readonly object CacheGate = new();
    private static IReadOnlyList<LocalAddress>? _cached;
    private static DateTime _cachedAt;

    static NetworkHelper()
    {
        NetworkChange.NetworkAddressChanged += (_, _) => InvalidateCache();
    }

    public static void InvalidateCache()
    {
        lock (CacheGate)
        {
            _cached = null;
        }
    }

    /// <summary>IPv4 addresses of the interfaces that are up, excluding loopback, tunnels and link-local.</summary>
    public static IReadOnlyList<LocalAddress> GetLocalAddresses()
    {
        lock (CacheGate)
        {
            if (_cached != null && DateTime.UtcNow - _cachedAt < TimeSpan.FromSeconds(30))
            {
                return _cached;
            }
        }

        var fresh = EnumerateLocalAddresses();
        lock (CacheGate)
        {
            _cached = fresh;
            _cachedAt = DateTime.UtcNow;
        }

        return fresh;
    }

    private static IReadOnlyList<LocalAddress> EnumerateLocalAddresses()
    {
        var result = new List<LocalAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel
                || !nic.SupportsMulticast)
            {
                continue;
            }

            IPInterfaceProperties props;
            int index;
            try
            {
                props = nic.GetIPProperties();
                index = props.GetIPv4Properties()?.Index ?? -1;
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            var hasGateway = props.GatewayAddresses.Any(g =>
                g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
            foreach (var unicast in props.UnicastAddresses)
            {
                var address = unicast.Address;
                if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address)
                    || address.GetAddressBytes() is [169, 254, ..])
                {
                    continue;
                }

                result.Add(new LocalAddress(address, unicast.IPv4Mask ?? IPAddress.Parse("255.255.255.0"), index, nic.Name, hasGateway));
            }
        }

        // Interfaces with a default gateway first: they are the real LAN rather than virtual adapters.
        return result.OrderByDescending(a => a.HasGateway).ToList();
    }

    /// <summary>The local address a device at <paramref name="remote"/> can reach us on.</summary>
    public static IPAddress GetAddressFor(IPAddress? remote, IReadOnlyList<LocalAddress>? addresses = null)
    {
        addresses ??= GetLocalAddresses();
        if (remote != null)
        {
            if (remote.IsIPv4MappedToIPv6)
            {
                remote = remote.MapToIPv4();
            }

            var sameSubnet = addresses.FirstOrDefault(a => a.Contains(remote));
            if (sameSubnet != null)
            {
                return sameSubnet.Address;
            }
        }

        return addresses.FirstOrDefault()?.Address ?? IPAddress.Loopback;
    }

    public static bool IsLocalMachine(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return IPAddress.IsLoopback(address) || GetLocalAddresses().Any(a => a.Address.Equals(address));
    }

    /// <summary>MAC address of a host on the local subnet via ARP, formatted as "B4-B2-91-99-25-09".</summary>
    public static string? TryGetMacAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }

        try
        {
            var mac = new byte[6];
            var length = mac.Length;
#pragma warning disable CS0618 // IPAddress.Address is the expected in_addr layout for SendARP.
            var destination = (int)address.Address;
#pragma warning restore CS0618
            if (SendARP(destination, 0, mac, ref length) != 0 || length < 6)
            {
                return null;
            }

            return string.Join("-", mac.Take(6).Select(b => b.ToString("X2")));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(int destIp, int srcIp, byte[] macAddress, ref int macAddressLength);
}
