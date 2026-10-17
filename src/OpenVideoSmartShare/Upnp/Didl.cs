using System.Globalization;
using System.Text;
using OpenVideoSmartShare.Library;

namespace OpenVideoSmartShare.Upnp;

/// <summary>
/// DIDL-Lite metadata for ContentDirectory results and AVTransport URIs. The video resource
/// declares DLNA.ORG_OP=01 (byte seek) and streaming flags 0x01700000, plus an extra
/// "text/&lt;ext&gt;" resource pointing at the external subtitle, which webOS TVs pick up.
/// </summary>
internal static class Didl
{
    public const string Header =
        "<DIDL-Lite xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\" " +
        "xmlns:dc=\"http://purl.org/dc/elements/1.1/\" " +
        "xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\" " +
        "xmlns:dlna=\"urn:schemas-dlna-org:metadata-1-0/\" " +
        "xmlns:sec=\"http://www.sec.co.kr/\">";

    public const string Footer = "</DIDL-Lite>";

    /// <summary>Streaming + background + connection stall + DLNA 1.5 flags.</summary>
    public const string VideoFeatures = "DLNA.ORG_OP=01;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=01700000000000000000000000000000";

    public static string ProtocolInfo(string mime) => $"http-get:*:{mime}:{VideoFeatures}";

    public static string MediaUrl(string baseUrl, VideoItem item) =>
        $"{baseUrl}/media/{item.Id}/{Uri.EscapeDataString(item.FileName)}";

    public static string SubtitleUrl(string baseUrl, VideoItem item) =>
        $"{baseUrl}/subtitle/{item.Id}.{item.SubtitleExtension}";

    /// <summary>Complete DIDL-Lite document describing a single video.</summary>
    public static string ForItem(VideoItem item, string parentId, string baseUrl)
    {
        var sb = new StringBuilder(Header);
        AppendItem(sb, item, parentId, baseUrl);
        return sb.Append(Footer).ToString();
    }

    public static void AppendItem(StringBuilder sb, VideoItem item, string parentId, string baseUrl)
    {
        sb.Append("<item id=\"").Append(Escape(item.Id))
          .Append("\" parentID=\"").Append(Escape(parentId))
          .Append("\" restricted=\"1\">");
        sb.Append("<dc:title>").Append(Escape(item.Title)).Append("</dc:title>");
        sb.Append("<upnp:class>object.item.videoItem</upnp:class>");
        sb.Append("<dc:date>").Append(item.Modified.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append("</dc:date>");

        sb.Append("<res protocolInfo=\"").Append(Escape(ProtocolInfo(item.MimeType)))
          .Append("\" size=\"").Append(item.Size.ToString(CultureInfo.InvariantCulture)).Append('"');
        var details = item.Details;
        if (details?.Duration is { } duration)
        {
            sb.Append(" duration=\"").Append(FormatDuration(duration)).Append('"');
        }

        if (details?.Resolution is { } resolution)
        {
            sb.Append(" resolution=\"").Append(resolution).Append('"');
        }

        if (details is { BitsPerSecond: > 0 })
        {
            // UPnP expresses res@bitrate in bytes per second.
            sb.Append(" bitrate=\"").Append((details.BitsPerSecond / 8).ToString(CultureInfo.InvariantCulture)).Append('"');
        }

        sb.Append('>').Append(Escape(MediaUrl(baseUrl, item))).Append("</res>");

        if (item.SubtitleExtension is { } ext)
        {
            var subtitleUrl = Escape(SubtitleUrl(baseUrl, item));
            sb.Append("<res protocolInfo=\"http-get:*:").Append(Escape(MediaTypes.GetSubtitleMime(ext)))
              .Append(":*\">").Append(subtitleUrl).Append("</res>");
            sb.Append("<sec:CaptionInfoEx sec:type=\"").Append(Escape(ext)).Append("\">")
              .Append(subtitleUrl).Append("</sec:CaptionInfoEx>");
        }

        sb.Append("</item>");
    }

    public static void AppendContainer(StringBuilder sb, LibraryContainer container, string title)
    {
        sb.Append("<container id=\"").Append(Escape(container.Id))
          .Append("\" parentID=\"").Append(Escape(container.ParentId))
          .Append("\" restricted=\"1\" searchable=\"0\" childCount=\"")
          .Append(container.ChildCount.ToString(CultureInfo.InvariantCulture)).Append("\">");
        sb.Append("<dc:title>").Append(Escape(title)).Append("</dc:title>");
        sb.Append("<upnp:class>object.container.storageFolder</upnp:class>");
        sb.Append("</container>");
    }

    /// <summary>H+:MM:SS.FFF as required by res@duration.</summary>
    public static string FormatDuration(TimeSpan duration) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}.{duration.Milliseconds:000}");

    public static string Escape(string value)
    {
        var sb = new StringBuilder(value.Length + 16);
        foreach (var c in value)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&apos;"); break;
                default:
                    // Drop characters that are not allowed in XML 1.0.
                    if (c >= 0x20 || c is '\t' or '\n' or '\r')
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        return sb.ToString();
    }
}
