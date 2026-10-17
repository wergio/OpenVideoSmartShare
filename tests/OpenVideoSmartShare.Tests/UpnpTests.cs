using System.Xml.Linq;
using OpenVideoSmartShare.Library;
using OpenVideoSmartShare.Upnp;

namespace OpenVideoSmartShare.Tests;

public class UpnpTests
{
    private static readonly XNamespace DidlNs = "urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/";
    private static readonly XNamespace DcNs = "http://purl.org/dc/elements/1.1/";

    [Fact]
    public void ItemDidlIsWellFormedAndCarriesSubtitle()
    {
        var item = LibraryTests.Video(@"Z:\Tom & Jerry <1>.mkv", "d1", @"Z:\Tom & Jerry <1>.srt");
        item.Details = new MediaDetails(TimeSpan.FromSeconds(3725.5), 3840, 2160, 16_000_000);

        var doc = XDocument.Parse(Didl.ForItem(item, "d1", "http://192.168.1.103:52100"));
        var element = doc.Root!.Element(DidlNs + "item")!;
        Assert.Equal("Tom & Jerry <1>", element.Element(DcNs + "title")!.Value);

        var resources = element.Elements(DidlNs + "res").ToList();
        Assert.Equal(2, resources.Count);
        Assert.Equal("http-get:*:video/x-mkv:DLNA.ORG_OP=01;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=01700000000000000000000000000000",
            resources[0].Attribute("protocolInfo")!.Value);
        Assert.Equal("1:02:05.500", resources[0].Attribute("duration")!.Value);
        Assert.Equal("3840x2160", resources[0].Attribute("resolution")!.Value);
        Assert.Equal("2000000", resources[0].Attribute("bitrate")!.Value);
        Assert.Equal($"http://192.168.1.103:52100/media/{item.Id}/Tom%20%26%20Jerry%20%3C1%3E.mkv", resources[0].Value);
        Assert.Equal("http-get:*:text/srt:*", resources[1].Attribute("protocolInfo")!.Value);
        Assert.Equal($"http://192.168.1.103:52100/subtitle/{item.Id}.srt", resources[1].Value);
    }

    [Fact]
    public void BrowsePagesChildrenAndHidesContentFromBlockedDevices()
    {
        var root = VideoItem.MakeId('d', @"Z:\");
        var items = Enumerable.Range(1, 5).Select(i => LibraryTests.Video($@"Z:\Video{i}.mkv", root)).ToList();
        var snapshot = LibrarySnapshot.Build(items, [(root, "0", @"Z:\")], 42, [], DateTime.Now);

        var page = ContentDirectory.Browse(snapshot, root, "BrowseDirectChildren", 1, 2, true, "http://h:1", k => k);
        Assert.Equal(2, page.NumberReturned);
        Assert.Equal(5, page.TotalMatches);
        Assert.Equal(42u, page.UpdateId);
        Assert.Equal(["Video2", "Video3"], XDocument.Parse(page.Didl).Descendants(DcNs + "title").Select(t => t.Value));

        var rootChildren = ContentDirectory.Browse(snapshot, "0", "BrowseDirectChildren", 0, 0, true, "http://h:1", k => "T:" + k);
        Assert.Equal(2, rootChildren.TotalMatches);
        Assert.Contains("T:dlna.allVideos", rootChildren.Didl);

        var blocked = ContentDirectory.Browse(snapshot, root, "BrowseDirectChildren", 0, 0, false, "http://h:1", k => k);
        Assert.Equal(0, blocked.TotalMatches);

        var ex = Assert.Throws<UpnpException>(() =>
            ContentDirectory.Browse(snapshot, "missing", "BrowseMetadata", 0, 0, true, "http://h:1", k => k));
        Assert.Equal(701, ex.Code);
    }

    [Fact]
    public void SoapRoundTrip()
    {
        var request = Soap.Request(ContentDirectory.ServiceType, "Browse", ("ObjectID", "0"), ("BrowseFlag", "BrowseDirectChildren"), ("RequestedCount", "10"));
        var parsed = Soap.Parse(request)!;
        Assert.Equal("Browse", parsed.Action);
        Assert.Equal(10, parsed.GetInt("RequestedCount"));

        var response = Soap.Response(ContentDirectory.ServiceType, "Browse", ("Result", "<DIDL-Lite/>"));
        Assert.Equal("<DIDL-Lite/>", Soap.ParseResponse(response)["Result"]);

        var fault = Assert.Throws<UpnpException>(() => Soap.ParseResponse(Soap.Fault(716, "Resource not found")));
        Assert.Equal(716, fault.Code);
    }

    [Theory]
    [InlineData("ssdp:all", 5)]
    [InlineData("upnp:rootdevice", 1)]
    [InlineData("urn:schemas-upnp-org:device:MediaServer:1", 1)]
    [InlineData("urn:schemas-upnp-org:service:ContentDirectory:1", 1)]
    [InlineData("urn:schemas-upnp-org:device:MediaRenderer:1", 0)]
    public void AnswersOnlyMatchingSearchTargets(string st, int expected)
    {
        Assert.Equal(expected, SsdpService.MatchSearchTarget(st, Guid.NewGuid()).Count);
    }

    [Fact]
    public void ParsesLgRendererDescription()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <root xmlns="urn:schemas-upnp-org:device-1-0">
              <device>
                <deviceType>urn:schemas-upnp-org:device:MediaRenderer:1</deviceType>
                <friendlyName>[LG] webOS TV OLED65CX6LA</friendlyName>
                <manufacturer>LG Electronics.</manufacturer>
                <modelName>LG TV</modelName>
                <UDN>uuid:eee34e06-32db-5959-0731-9fb6420e56b3</UDN>
                <serviceList>
                  <service>
                    <serviceType>urn:schemas-upnp-org:service:AVTransport:1</serviceType>
                    <controlURL>/AVTransport/eee34e06-32db-5959-0731-9fb6420e56b3/control.xml</controlURL>
                  </service>
                </serviceList>
              </device>
            </root>
            """;
        var renderer = RendererRegistry.ParseDescription(xml, new Uri("http://192.168.1.142:1754/"))!;
        Assert.Equal("[LG] webOS TV OLED65CX6LA", renderer.FriendlyName);
        Assert.Equal("uuid:eee34e06-32db-5959-0731-9fb6420e56b3", renderer.Udn);
        Assert.Equal("192.168.1.142", renderer.Address.ToString());
        Assert.Equal("http://192.168.1.142:1754/AVTransport/eee34e06-32db-5959-0731-9fb6420e56b3/control.xml",
            renderer.AvTransportControl.ToString());
    }
}
