using System.Collections.Concurrent;
using System.Net;
using OpenVideoSmartShare.Settings;
using OpenVideoSmartShare.Upnp;

namespace OpenVideoSmartShare.Devices;

/// <summary>
/// "Allow/Block other devices": devices are identified by MAC address (resolved through ARP)
/// so that a DHCP address change keeps the rule. Only TVs can be allowed: new TVs are, devices
/// that were never seen as a renderer are always refused.
/// </summary>
internal sealed class DeviceAccess
{
    private static readonly TimeSpan MacCacheLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LastSeenPersistInterval = TimeSpan.FromMinutes(30);
    // TVs that browse the server poll it every couple of minutes.
    private static readonly TimeSpan PresenceWindow = TimeSpan.FromMinutes(5);

    private readonly SettingsStore _settings;
    private readonly ConcurrentDictionary<IPAddress, (string? Mac, DateTime At)> _macCache = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastContact = new();

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

        // Allowed stays true on new devices, so a device found to be a TV later is allowed then.
        var rule = Observe(address, nameHint, preferName: false);
        return rule.Allowed && rule.RendererUdn != null;
    }

    /// <summary>
    /// Adds or refreshes a device. Renderer friendly names replace user-agent based names;
    /// <paramref name="rendererUdn"/> marks the device as a TV.
    /// </summary>
    public DeviceRule Observe(IPAddress address, string? name, bool preferName, string? rendererUdn = null)
    {
        address = Normalize(address);
        var mac = GetMac(address);
        var key = mac ?? "ip:" + address;
        var ip = address.ToString();
        var now = DateTime.Now;
        _lastContact[key] = DateTime.UtcNow;

        var existing = _settings.Read(s =>
        {
            var rule = s.Devices.FirstOrDefault(d => d.Key == key)
                       ?? (mac == null ? null : s.Devices.FirstOrDefault(d => d.Key == "ip:" + ip));
            return rule?.Clone();
        });
        if (existing != null)
        {
            var rename = !string.IsNullOrWhiteSpace(name) && (preferName || string.IsNullOrWhiteSpace(existing.Name))
                         && existing.Name != name;
            // The first UDN is kept: a device can host more than one renderer.
            var learnUdn = rendererUdn != null && existing.RendererUdn == null;
            var stale = existing.LastIp != ip || existing.Key != key || now - existing.LastSeen > LastSeenPersistInterval;
            if (!rename && !stale && !learnUdn)
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
                rule.RendererUdn ??= rendererUdn;
                if (rename)
                {
                    rule.Name = name!;
                }

                updated = rule.Clone();
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
            RendererUdn = rendererUdn,
        };
        _settings.Update(s =>
        {
            if (s.Devices.All(d => d.Key != key))
            {
                s.Devices.Add(added.Clone());
            }
        }, notify: false);
        DevicesChanged?.Invoke(this, EventArgs.Empty);
        return added;
    }

    /// <summary>
    /// Whether a device is on the network now: one of the TVs announcing themselves, or a
    /// device that contacted the server in the last few minutes.
    /// </summary>
    public bool IsPresent(DeviceRule rule, IReadOnlyList<RendererInfo> online, IReadOnlyList<DeviceRule> rules) =>
        online.Any(r => ReferenceEquals(FindRule(rules, r), rule))
        || (_lastContact.TryGetValue(rule.Key, out var at) && DateTime.UtcNow - at < PresenceWindow);

    /// <summary>Whether a device is a TV: one seen as a renderer before, or announcing itself now.</summary>
    public static bool IsTv(DeviceRule rule, IReadOnlyList<RendererInfo> online, IReadOnlyList<DeviceRule> rules) =>
        rule.RendererUdn != null || online.Any(r => ReferenceEquals(FindRule(rules, r), rule));

    /// <summary>
    /// TVs offered for playback: the renderers on the network that are not blocked, plus the
    /// allowed TVs seen before that are offline now.
    /// </summary>
    public static IReadOnlyList<PlayTarget> PlayTargets(IReadOnlyList<RendererInfo> online, IReadOnlyList<DeviceRule> rules)
    {
        var targets = online
            .Where(r => FindRule(rules, r)?.Allowed ?? true)
            .Select(r => new PlayTarget(r.Udn, r.FriendlyName, r))
            .ToList();
        targets.AddRange(rules
            .Where(d => d.Allowed && d.RendererUdn != null
                        && online.All(r => !string.Equals(r.Udn, d.RendererUdn, StringComparison.OrdinalIgnoreCase)))
            .Select(d => new PlayTarget(d.RendererUdn!, d.Name, null)));
        return targets.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Whether the TV <paramref name="udn"/> belongs to a blocked device.</summary>
    public static bool IsBlocked(string udn, IReadOnlyList<RendererInfo> online, IReadOnlyList<DeviceRule> rules)
    {
        var renderer = online.FirstOrDefault(r => string.Equals(r.Udn, udn, StringComparison.OrdinalIgnoreCase));
        var rule = renderer != null
            ? FindRule(rules, renderer)
            : rules.FirstOrDefault(d => string.Equals(d.RendererUdn, udn, StringComparison.OrdinalIgnoreCase));
        return rule is { Allowed: false };
    }

    /// <summary>The rule of a renderer: by UDN, otherwise by its current address.</summary>
    private static DeviceRule? FindRule(IReadOnlyList<DeviceRule> rules, RendererInfo renderer)
    {
        var ip = renderer.Address.ToString();
        return rules.FirstOrDefault(d => string.Equals(d.RendererUdn, renderer.Udn, StringComparison.OrdinalIgnoreCase))
               ?? rules.Where(d => d.LastIp == ip).MaxBy(d => d.LastSeen);
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
}

/// <summary>A TV offered for playback; <see cref="Renderer"/> is null while it is offline.</summary>
internal sealed record PlayTarget(string Udn, string Name, RendererInfo? Renderer);
