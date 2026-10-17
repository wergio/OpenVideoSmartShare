using System.Globalization;
using System.Text;
using OpenVideoSmartShare.Library;

namespace OpenVideoSmartShare.Upnp;

/// <summary>Result of a ContentDirectory Browse.</summary>
internal sealed record BrowseResult(string Didl, int NumberReturned, int TotalMatches, uint UpdateId);

/// <summary>ContentDirectory:1 actions over a <see cref="LibrarySnapshot"/>.</summary>
internal static class ContentDirectory
{
    public const string ServiceType = "urn:schemas-upnp-org:service:ContentDirectory:1";

    /// <param name="allowed">False for blocked devices: they see empty folders.</param>
    /// <param name="translate">Resolves localized container titles.</param>
    public static BrowseResult Browse(LibrarySnapshot snapshot, string objectId, string browseFlag, int start, int count,
        bool allowed, string baseUrl, Func<string, string> translate)
    {
        if (start < 0 || count < 0)
        {
            throw new UpnpException(402, "Invalid Args");
        }

        var sb = new StringBuilder(Didl.Header);
        if (browseFlag == "BrowseMetadata")
        {
            if (snapshot.Containers.TryGetValue(objectId, out var container))
            {
                Didl.AppendContainer(sb, container, TitleOf(container, translate));
            }
            else if (allowed && snapshot.ById.TryGetValue(objectId, out var item))
            {
                Didl.AppendItem(sb, item, item.ParentId, baseUrl);
            }
            else
            {
                throw new UpnpException(701, "No such object");
            }

            return new BrowseResult(sb.Append(Didl.Footer).ToString(), 1, 1, snapshot.UpdateId);
        }

        if (browseFlag != "BrowseDirectChildren")
        {
            throw new UpnpException(402, "Invalid Args");
        }

        if (!snapshot.Containers.TryGetValue(objectId, out var parent))
        {
            throw new UpnpException(701, "No such object");
        }

        if (!allowed)
        {
            return new BrowseResult(sb.Append(Didl.Footer).ToString(), 0, 0, snapshot.UpdateId);
        }

        var total = parent.Containers.Count + parent.Items.Count;
        var end = count == 0 ? total : (int)Math.Min(total, (long)start + count);
        var returned = 0;
        for (var i = start; i < end; i++)
        {
            if (i < parent.Containers.Count)
            {
                var child = parent.Containers[i];
                Didl.AppendContainer(sb, child, TitleOf(child, translate));
            }
            else
            {
                Didl.AppendItem(sb, parent.Items[i - parent.Containers.Count], parent.Id, baseUrl);
            }

            returned++;
        }

        return new BrowseResult(sb.Append(Didl.Footer).ToString(), returned, total, snapshot.UpdateId);
    }

    /// <summary>Handles one SOAP action and returns the response envelope.</summary>
    public static string Handle(SoapRequest request, LibrarySnapshot snapshot, bool allowed, string baseUrl, Func<string, string> translate)
    {
        switch (request.Action)
        {
            case "Browse":
                var result = Browse(snapshot, request.Get("ObjectID", LibrarySnapshot.RootId), request.Get("BrowseFlag"),
                    request.GetInt("StartingIndex"), request.GetInt("RequestedCount"), allowed, baseUrl, translate);
                return Soap.Response(ServiceType, "Browse",
                    ("Result", result.Didl),
                    ("NumberReturned", result.NumberReturned.ToString(CultureInfo.InvariantCulture)),
                    ("TotalMatches", result.TotalMatches.ToString(CultureInfo.InvariantCulture)),
                    ("UpdateID", result.UpdateId.ToString(CultureInfo.InvariantCulture)));
            case "GetSystemUpdateID":
                return Soap.Response(ServiceType, "GetSystemUpdateID", ("Id", snapshot.UpdateId.ToString(CultureInfo.InvariantCulture)));
            case "GetSearchCapabilities":
                return Soap.Response(ServiceType, "GetSearchCapabilities", ("SearchCaps", ""));
            case "GetSortCapabilities":
                return Soap.Response(ServiceType, "GetSortCapabilities", ("SortCaps", ""));
            default:
                throw new UpnpException(401, "Invalid Action");
        }
    }

    private static string TitleOf(LibraryContainer container, Func<string, string> translate) =>
        container.TitleKey != null ? translate(container.TitleKey) : container.Title ?? container.Id;
}

/// <summary>ConnectionManager:1 actions for a source-only device.</summary>
internal static class ConnectionManager
{
    public const string ServiceType = "urn:schemas-upnp-org:service:ConnectionManager:1";

    public static string SourceProtocolInfo()
    {
        var video = MediaTypes.VideoExtensions.Select(e => MediaTypes.GetVideoMime(e)!).Distinct()
            .Select(m => $"http-get:*:{m}:*");
        var subtitles = MediaTypes.SubtitleExtensions.Select(e => $"http-get:*:{MediaTypes.GetSubtitleMime(e)}:*");
        return string.Join(",", video.Concat(subtitles));
    }

    public static string Handle(SoapRequest request)
    {
        switch (request.Action)
        {
            case "GetProtocolInfo":
                return Soap.Response(ServiceType, "GetProtocolInfo", ("Source", SourceProtocolInfo()), ("Sink", ""));
            case "GetCurrentConnectionIDs":
                return Soap.Response(ServiceType, "GetCurrentConnectionIDs", ("ConnectionIDs", "0"));
            case "GetCurrentConnectionInfo":
                if (request.Get("ConnectionID") != "0")
                {
                    throw new UpnpException(706, "Invalid connection reference");
                }

                return Soap.Response(ServiceType, "GetCurrentConnectionInfo",
                    ("RcsID", "-1"), ("AVTransportID", "-1"), ("ProtocolInfo", ""),
                    ("PeerConnectionManager", ""), ("PeerConnectionID", "-1"),
                    ("Direction", "Output"), ("Status", "OK"));
            default:
                throw new UpnpException(401, "Invalid Action");
        }
    }
}
