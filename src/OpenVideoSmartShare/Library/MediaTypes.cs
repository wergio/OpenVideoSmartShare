namespace OpenVideoSmartShare.Library;

/// <summary>
/// File extension to MIME type tables. The video types are the ones webOS TVs accept
/// (e.g. mkv -> video/x-mkv, mov -> video/mp4, ts -> video/mpeg).
/// </summary>
internal static class MediaTypes
{
    private static readonly Dictionary<string, string> Video = new(StringComparer.OrdinalIgnoreCase)
    {
        ["k3g"] = "video/kr3g",
        ["rm"] = "video/x-pn-realmedia",
        ["rmvb"] = "video/x-pn-realmedia",
        ["rv"] = "video/x-pn-realmedia",
        ["trp"] = "video/mp2p",
        ["tp"] = "video/mp2t",
        ["3gp"] = "video/3gpp",
        ["3g2"] = "video/mp4",
        ["avi"] = "video/avi",
        ["divx"] = "video/avi",
        ["flv"] = "video/flv",
        ["f4v"] = "video/mp4",
        ["mp2p"] = "video/mp2p",
        ["mp2t"] = "video/mp2t",
        ["m4v"] = "video/mp4",
        ["mp4"] = "video/mp4",
        ["dvr-ms"] = "video/mpeg",
        ["ogv"] = "video/ogg",
        ["ogm"] = "video/ogg",
        ["m1v"] = "video/mpeg",
        ["mpe"] = "video/mpeg",
        ["mpeg"] = "video/mpeg",
        ["mpg"] = "video/mpeg",
        ["vob"] = "video/mpeg2",
        ["m2v"] = "video/mpeg2",
        ["mpeg2"] = "video/mpeg2",
        ["mpg2"] = "video/mpeg2",
        ["mov"] = "video/mp4",
        ["mts"] = "video/mpeg",
        ["m2ts"] = "video/mpeg",
        ["ts"] = "video/mpeg",
        ["dv"] = "video/x-dv",
        ["mkv"] = "video/x-mkv",
        ["asf"] = "video/x-ms-asf",
        ["wmv"] = "video/x-ms-wmv",
        ["wtv"] = "video/x-ms-wtv",
        ["webm"] = "video/webm",
    };

    /// <summary>Subtitle formats in order of preference when several match the same video.</summary>
    private static readonly string[] SubtitlePreference = ["srt", "ass", "ssa", "smi", "sub", "psb"];

    public static IReadOnlyCollection<string> VideoExtensions => Video.Keys;

    public static IReadOnlyList<string> SubtitleExtensions => SubtitlePreference;

    /// <summary>MIME type of a video extension (without dot), or null when it is not a video.</summary>
    public static string? GetVideoMime(string extension) =>
        Video.TryGetValue(extension.TrimStart('.'), out var mime) ? mime : null;

    public static bool IsSubtitle(string extension) =>
        Array.IndexOf(SubtitlePreference, extension.TrimStart('.').ToLowerInvariant()) >= 0;

    /// <summary>Lower rank is preferred.</summary>
    public static int SubtitleRank(string extension)
    {
        var index = Array.IndexOf(SubtitlePreference, extension.TrimStart('.').ToLowerInvariant());
        return index < 0 ? int.MaxValue : index;
    }

    /// <summary>Subtitles are advertised as "text/&lt;extension&gt;" (text/srt, text/smi...).</summary>
    public static string GetSubtitleMime(string extension) => "text/" + extension.TrimStart('.').ToLowerInvariant();
}
