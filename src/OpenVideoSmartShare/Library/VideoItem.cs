using System.Security.Cryptography;
using System.Text;

namespace OpenVideoSmartShare.Library;

/// <summary>A shared video file.</summary>
internal sealed class VideoItem
{
    public required string Id { get; init; }

    public required string FullPath { get; init; }

    /// <summary>Id of the folder container holding the file.</summary>
    public required string ParentId { get; init; }

    public required string MimeType { get; init; }

    public required long Size { get; init; }

    public required DateTime Modified { get; init; }

    /// <summary>Subtitle files next to the video, best match first.</summary>
    public IReadOnlyList<string> ExternalSubtitles { get; init; } = [];

    /// <summary>The external subtitle advertised to TVs.</summary>
    public string? SubtitlePath => ExternalSubtitles.Count > 0 ? ExternalSubtitles[0] : null;

    /// <summary>Filled in asynchronously after the scan; replaced atomically.</summary>
    public MediaDetails? Details { get; set; }

    /// <summary>Formats (SRT, ASS...) of the external subtitle files.</summary>
    public IEnumerable<string> ExternalSubtitleFormats =>
        ExternalSubtitles.Select(p => Path.GetExtension(p).TrimStart('.').ToUpperInvariant());

    /// <summary>Formats of the subtitle tracks inside the file; empty until the details are read.</summary>
    public IReadOnlyList<string> EmbeddedSubtitleFormats => Details?.EmbeddedSubtitles ?? [];

    public int SubtitleCount => ExternalSubtitles.Count + EmbeddedSubtitleFormats.Count;

    public string FileName => Path.GetFileName(FullPath);

    public string Title => Path.GetFileNameWithoutExtension(FullPath);

    public string Extension => Path.GetExtension(FullPath).TrimStart('.').ToLowerInvariant();

    public string FolderPath => Path.GetDirectoryName(FullPath) ?? "";

    public string? SubtitleExtension => SubtitlePath == null ? null : Path.GetExtension(SubtitlePath).TrimStart('.').ToLowerInvariant();

    /// <summary>Stable identifier derived from a path ("v" for files, "d" for folders).</summary>
    public static string MakeId(char prefix, string path)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant()));
        return prefix + Convert.ToHexString(bytes, 0, 10).ToLowerInvariant();
    }
}
