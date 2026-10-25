using System.Net;
using OpenVideoSmartShare.Devices;
using OpenVideoSmartShare.Settings;
using OpenVideoSmartShare.Upnp;

namespace OpenVideoSmartShare.Tests;

public sealed class DeviceAccessTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ovss-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void FirstRunHasNoDevicesAndNoDefaultTv()
    {
        var settings = new SettingsStore(Path.Combine(_folder, "settings.json")).Snapshot();

        Assert.Empty(settings.Devices);
        Assert.Null(settings.DefaultRendererUdn);
        Assert.Null(settings.SelectedRendererUdn);
    }

    [Fact]
    public void NewDevicesAreAllowedAndRememberTheirTv()
    {
        var store = new SettingsStore(Path.Combine(_folder, "settings.json"));
        var access = new DeviceAccess(store);

        // IPv6 has no ARP lookup, so the device is keyed by address.
        var device = access.Observe(IPAddress.IPv6Loopback, "Living room", preferName: true, "uuid:tv");

        Assert.True(device.Allowed);
        var saved = Assert.Single(store.Snapshot().Devices);
        Assert.True(saved.Allowed);
        Assert.Equal("uuid:tv", saved.RendererUdn);
    }

    [Fact]
    public void DevicesAreRefusedUntilSeenAsTv()
    {
        var store = new SettingsStore(Path.Combine(_folder, "settings.json"));
        var access = new DeviceAccess(store);
        // A documentation address: never the local machine, and keyed by address (no ARP on IPv6).
        var address = IPAddress.Parse("2001:db8::50");

        Assert.False(access.IsAllowed(address, "Phone"));

        access.Observe(address, "Living room", preferName: true, "uuid:tv");
        Assert.True(access.IsAllowed(address, "Living room"));

        store.Update(s => s.Devices[0].Allowed = false, notify: false);
        Assert.False(access.IsAllowed(address, "Living room"));
    }

    [Fact]
    public void PlayTargetsSkipBlockedTvsAndKeepAllowedOfflineOnes()
    {
        var online = new[] { Renderer("uuid:lg", "LG", "192.168.1.142"), Renderer("uuid:samsung", "Samsung", "192.168.1.141"), Renderer("uuid:new", "New", "192.168.1.150") };
        var rules = new List<DeviceRule>
        {
            new() { Key = "lg", Name = "LG", LastIp = "192.168.1.142", Allowed = true },
            new() { Key = "samsung", Name = "Samsung", LastIp = "192.168.1.141", Allowed = false },
            new() { Key = "living", Name = "Living room", LastIp = "192.168.1.144", Allowed = true, RendererUdn = "uuid:living" },
            new() { Key = "bedroom", Name = "Bedroom", LastIp = "192.168.1.145", Allowed = false, RendererUdn = "uuid:bedroom" },
            new() { Key = "phone", Name = "Phone", LastIp = "192.168.1.160", Allowed = true },
        };

        var targets = DeviceAccess.PlayTargets(online, rules);

        Assert.Equal(["LG", "Living room", "New"], targets.Select(t => t.Name));
        Assert.Null(targets.Single(t => t.Udn == "uuid:living").Renderer);
        Assert.NotNull(targets.Single(t => t.Udn == "uuid:lg").Renderer);
        Assert.True(DeviceAccess.IsBlocked("uuid:samsung", online, rules));
        Assert.True(DeviceAccess.IsBlocked("uuid:bedroom", online, rules));
        Assert.False(DeviceAccess.IsBlocked("uuid:living", online, rules));
        Assert.False(DeviceAccess.IsBlocked("uuid:unknown", online, rules));
    }

    [Fact]
    public void DevicesArePresentWhenAnnouncingOrContactingTheServer()
    {
        var store = new SettingsStore(Path.Combine(_folder, "settings.json"));
        var access = new DeviceAccess(store);
        access.Observe(IPAddress.IPv6Loopback, "Phone", preferName: false);
        var rules = new List<DeviceRule>(store.Snapshot().Devices)
        {
            new() { Key = "lg", Name = "LG", LastIp = "192.168.1.142" },
            new() { Key = "samsung", Name = "Samsung", LastIp = "192.168.1.141" },
        };
        var online = new[] { Renderer("uuid:lg", "LG", "192.168.1.142") };

        Assert.Equal([true, true, false], rules.Select(d => access.IsPresent(d, online, rules)));
    }

    private static RendererInfo Renderer(string udn, string name, string ip) => new()
    {
        Udn = udn,
        FriendlyName = name,
        Location = new Uri($"http://{ip}:1234/"),
        Address = IPAddress.Parse(ip),
        AvTransportControl = new Uri($"http://{ip}:1234/avt"),
    };
}
