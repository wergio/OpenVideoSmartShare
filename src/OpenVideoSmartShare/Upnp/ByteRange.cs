using System.Globalization;

namespace OpenVideoSmartShare.Upnp;

/// <summary>A single inclusive byte range of an HTTP Range header.</summary>
internal readonly record struct ByteRange(long Start, long End)
{
    public long Length => End - Start + 1;

    /// <summary>
    /// Parses "bytes=a-b", "bytes=a-" or "bytes=-n" against a resource of <paramref name="size"/> bytes.
    /// Returns <see cref="RangeParse.None"/> when there is no usable header (full response),
    /// <see cref="RangeParse.Unsatisfiable"/> for ranges outside the file.
    /// </summary>
    public static RangeParse TryParse(string? header, long size, out ByteRange range)
    {
        range = default;
        if (string.IsNullOrWhiteSpace(header))
        {
            return RangeParse.None;
        }

        var value = header.Trim();
        if (!value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return RangeParse.None;
        }

        // Only the first range is honoured; TVs never ask for multipart ranges.
        var spec = value[6..].Split(',')[0].Trim();
        var dash = spec.IndexOf('-');
        if (dash < 0)
        {
            return RangeParse.None;
        }

        var first = spec[..dash].Trim();
        var last = spec[(dash + 1)..].Trim();
        long start, end;
        if (first.Length == 0)
        {
            if (!long.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out var suffix) || suffix <= 0)
            {
                return RangeParse.None;
            }

            if (size == 0)
            {
                return RangeParse.Unsatisfiable;
            }

            start = Math.Max(0, size - suffix);
            end = size - 1;
        }
        else
        {
            if (!long.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out start))
            {
                return RangeParse.None;
            }

            if (last.Length == 0)
            {
                end = size - 1;
            }
            else if (!long.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out end) || end < start)
            {
                return RangeParse.None;
            }

            if (start >= size)
            {
                return RangeParse.Unsatisfiable;
            }

            end = Math.Min(end, size - 1);
        }

        range = new ByteRange(start, end);
        return RangeParse.Range;
    }
}

internal enum RangeParse
{
    None,
    Range,
    Unsatisfiable,
}
