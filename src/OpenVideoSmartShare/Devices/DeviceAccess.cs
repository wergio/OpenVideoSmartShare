using System.Collections.Concurrent;
using System.Net;
using OpenVideoSmartShare.Settings;
using OpenVideoSmartShare.Upnp;

namespace OpenVideoSmartShare.Devices;

/// <summary>
/// "Allow/Block other devices": devices are identified by MAC address (resolved through ARP)
/// so that a DHCP address change keeps the rule. New devices are allowed.
/// </summary>
internal sealed class DeviceAccess
{
    private static readonly TimeSpan MacCacheLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LastSeenPersistInterval = TimeSpan.FromMinutes(30);

    private readonly SettingsStore _settings;
    private readonly ConcurrentDictionary<IPAddress, (string? Mac, DateTime At)> _macCache = new();

    public DeviceAccess(SettingsStore settings) => _settings = settings;

    /// <summary>Raised when a device was added to the list.</summary>
    public event EventHandler? DevicesChanged;

    /// <summary>Whether <paramref name="address"/> may browse and stream; records unknown devices.</summary>
    public bool IsAllowed(IPAddress address, string? nameHint)
    {
        address = Normalize(address);
        if (NetworkHelper.IsLocalMachine(address))
        {
            return true;
        }

        return Observe(address, nameHint, preferName: false).Allowed;
    }

    /// <summary>Adds or refreshes a device. Renderer friendly names replace user-agent based names.</summary>
    public DeviceRule Observe(IPAddress address, string? name, bool preferName)
    {
        address = Normalize(address);
        var mac = GetMac(address);
        var key = mac ?? "ip:" + address;
        var ip = address.ToString();
        var now = DateTime.Now;

        var existing = _settings.Read(s =>
        {
            var rule = s.Devices.FirstOrDefault(d => d.Key == key)
                       ?? (mac == null ? null : s.Devices.FirstOrDefault(d => d.Key == "ip:" + ip));
            return rule == null ? null : Copy(rule);
        });
        if (existing != null)
        {
            var rename = !string.IsNullOrWhiteSpace(name) && (preferName || string.IsNullOrWhiteSpace(existing.Name))
                         && existing.Name != name;
            var stale = existing.LastIp != ip || existing.Key != key || now - existing.LastSeen > LastSeenPersistInterval;
            if (!rename && !stale)
            {
                return existing;
            }

            DeviceRule? updated = null;
            _settings.Update(s =>
            {
                var rule = s.Devices.FirstOrDefault(d => d.Key == existing.Key);
                if (rule == null)
                {
                    return;
                }

                rule.Key = key;
                rule.MacAddress = mac ?? rule.MacAddress;
                rule.LastIp = ip;
                rule.LastSeen = now;
                if (rename)
                {
                    rule.Name = name!;
                }

                updated = Copy(rule);
            }, notify: false);
            return updated ?? existing;
        }

        var added = new DeviceRule
        {
            Key = key,
            MacAddress = mac,
            LastIp = ip,
            Name = string.IsNullOrWhiteSpace(name) ? ip : name,
            Allowed = true,
            LastSeen = now,
        };
        _settings.Update(s =>
        {
            if (s.Devices.All(d => d.Key != key))
            {
                s.Devices.Add(Copy(added));
            }
        }, notify: false);
        DevicesChanged?.Invoke(this, EventArgs.Empty);
        return added;
    }

    private string? GetMac(IPAddress address)
    {
        if (_macCache.TryGetValue(address, out var cached) && DateTime.UtcNow - cached.At < MacCacheLifetime)
        {
            return cached.Mac;
        }

        var mac = NetworkHelper.TryGetMacAddress(address);
        _macCache[address] = (mac, DateTime.UtcNow);
        return mac;
    }

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static DeviceRule Copy(DeviceRule rule) => new()
    {
        Key = rule.Key,
        Name = rule.Name,
        MacAddress = rule.MacAddress,
        LastIp = rule.LastIp,
        Allowed = rule.Allowed,
        LastSeen = rule.LastSeen,
    };
}
