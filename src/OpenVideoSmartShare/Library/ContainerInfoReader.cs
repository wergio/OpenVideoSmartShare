using System.Buffers.Binary;
using System.Text;
using OpenVideoSmartShare.Infrastructure;

namespace OpenVideoSmartShare.Library;

/// <summary>What the container headers say about a video.</summary>
/// <param name="Subtitles">One format name (SRT, PGS...) per subtitle track.</param>
/// <param name="Width">Pixel width of the first video track, 0 when unknown.</param>
/// <param name="Height">Pixel height of the first video track, 0 when unknown.</param>
internal sealed record ContainerInfo(IReadOnlyList<string> Subtitles, int Width, int Height)
{
    public static readonly ContainerInfo Empty = new([], 0, 0);
}

/// <summary>
/// Reads subtitle tracks and video frame size from container headers only (about 1 MB,
/// plus the index when it sits elsewhere). Supports Matroska/WebM, ISO BMFF (MP4/MOV/M4V)
/// and MPEG transport streams (TS/M2TS, subtitles only). Fills the gaps of the Windows
/// property system, which reports no frame size for many MKV files.
/// </summary>
internal static class ContainerInfoReader
{
    private const int HeadBytes = 1024 * 1024;
    private const int MaxIndexBytes = 64 * 1024 * 1024;

    /// <summary>Container facts; <see cref="ContainerInfo.Empty"/> when the container is unknown or unreadable.</summary>
    public static ContainerInfo Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 64 * 1024, FileOptions.RandomAccess);
            return Read(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or InvalidDataException or IndexOutOfRangeException or OverflowException)
        {
            Log.Warn($"Cannot read the container headers of {path}", ex);
            return ContainerInfo.Empty;
        }
    }

    internal static ContainerInfo Read(Stream stream)
    {
        var head = ReadAt(stream, 0, HeadBytes);
        if (head.Length >= 4 && BinaryPrimitives.ReadUInt32BigEndian(head) == Matroska.EbmlId)
        {
            return Matroska.Read(stream, head);
        }

        if (head.Length >= 8 && Encoding.ASCII.GetString(head, 4, 4) is "ftyp" or "moov" or "mdat" or "free" or "wide" or "skip")
        {
            return IsoBmff.Read(stream);
        }

        if (TransportStream.DetectPacketLayout(head) is { } layout)
        {
            return new ContainerInfo(TransportStream.Read(head, layout.Size, layout.Offset), 0, 0);
        }

        return ContainerInfo.Empty;
    }

    private static byte[] ReadAt(Stream stream, long position, int count)
    {
        if (position >= stream.Length)
        {
            return [];
        }

        count = (int)Math.Min(count, stream.Length - position);
        var buffer = new byte[count];
        stream.Position = position;
        var read = 0;
        while (read < count)
        {
            var n = stream.Read(buffer, read, count - read);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        return read == count ? buffer : buffer[..read];
    }

    /// <summary>Matroska / WebM (EBML): Segment → Tracks → TrackEntry (type 0x11 subtitles, type 1 video).</summary>
    private static class Matroska
    {
        public const uint EbmlId = 0x1A45DFA3;
        private const uint SegmentId = 0x18538067;
        private const uint SeekHeadId = 0x114D9B74;
        private const uint SeekId = 0x4DBB;
        private const uint SeekIdId = 0x53AB;
        private const uint SeekPositionId = 0x53AC;
        private const uint TracksId = 0x1654AE6B;
        private const uint TrackEntryId = 0xAE;
        private const uint TrackTypeId = 0x83;
        private const uint CodecIdId = 0x86;
        private const uint VideoId = 0xE0;
        private const uint PixelWidthId = 0xB0;
        private const uint PixelHeightId = 0xBA;
        private const uint PixelCropBottomId = 0x54AA;
        private const uint PixelCropTopId = 0x54BB;
        private const uint PixelCropLeftId = 0x54CC;
        private const uint PixelCropRightId = 0x54DD;
        private const ulong VideoTrackType = 0x01;
        private const uint ClusterId = 0x1F43B675;
        private const ulong SubtitleTrackType = 0x11;

        public static ContainerInfo Read(Stream stream, byte[] head)
        {
            var position = 0L;
            if (!TryHeader(head, position, out var id, out var size, out var headerLength) || id != EbmlId)
            {
                return ContainerInfo.Empty;
            }

            position += headerLength + size;
            if (!TryHeader(head, position, out id, out size, out headerLength) || id != SegmentId)
            {
                return ContainerInfo.Empty;
            }

            var segmentData = position + headerLength;
            long? tracksPosition = null;
            position = segmentData;
            while (TryHeader(head, position, out id, out size, out headerLength))
            {
                var data = position + headerLength;
                if (id == TracksId)
                {
                    var body = data + size <= head.Length
                        ? head[(int)data..(int)(data + size)]
                        : ReadAt(stream, data, (int)Math.Min(size, MaxIndexBytes));
                    return ParseTracks(body);
                }

                if (id == SeekHeadId && data + size <= head.Length)
                {
                    tracksPosition ??= FindSeek(head, data, data + size, TracksId) is { } relative ? segmentData + relative : null;
                }

                if (id == ClusterId || size == long.MaxValue)
                {
                    break;
                }

                position = data + size;
            }

            // Tracks stored after the clusters: follow the SeekHead pointer.
            if (tracksPosition is { } at)
            {
                var header = ReadAt(stream, at, 12);
                if (TryHeader(header, 0, out id, out size, out headerLength) && id == TracksId)
                {
                    return ParseTracks(ReadAt(stream, at + headerLength, (int)Math.Min(size, MaxIndexBytes)));
                }
            }

            return ContainerInfo.Empty;
        }

        private static long? FindSeek(byte[] data, long start, long end, uint wanted)
        {
            var position = start;
            while (position < end && TryHeader(data, position, out var id, out var size, out var headerLength))
            {
                var body = position + headerLength;
                if (id == SeekId)
                {
                    uint seekId = 0;
                    long? seekPosition = null;
                    var inner = body;
                    while (inner < body + size && TryHeader(data, inner, out var childId, out var childSize, out var childHeader))
                    {
                        var value = data.AsSpan((int)(inner + childHeader), (int)childSize);
                        if (childId == SeekIdId)
                        {
                            seekId = (uint)ReadUnsigned(value);
                        }
                        else if (childId == SeekPositionId)
                        {
                            seekPosition = (long)ReadUnsigned(value);
                        }

                        inner += childHeader + childSize;
                    }

                    if (seekId == wanted && seekPosition.HasValue)
                    {
                        return seekPosition;
                    }
                }

                position = body + size;
            }

            return null;
        }

        private static ContainerInfo ParseTracks(byte[] data)
        {
            var result = new List<string>();
            int width = 0, height = 0;
            var position = 0L;
            while (TryHeader(data, position, out var id, out var size, out var headerLength))
            {
                var body = position + headerLength;
                if (id == TrackEntryId && body + size <= data.Length)
                {
                    ulong type = 0;
                    var codec = "";
                    (int Width, int Height) video = default;
                    var inner = body;
                    while (inner < body + size && TryHeader(data, inner, out var childId, out var childSize, out var childHeader))
                    {
                        var value = data.AsSpan((int)(inner + childHeader), (int)Math.Min(childSize, data.Length - inner - childHeader));
                        if (childId == TrackTypeId)
                        {
                            type = ReadUnsigned(value);
                        }
                        else if (childId == CodecIdId)
                        {
                            codec = Encoding.ASCII.GetString(value).TrimEnd('\0');
                        }
                        else if (childId == VideoId)
                        {
                            video = ParseVideo(data, inner + childHeader, inner + childHeader + childSize);
                        }

                        inner += childHeader + childSize;
                    }

                    if (type == SubtitleTrackType)
                    {
                        result.Add(CodecName(codec));
                    }
                    else if (type == VideoTrackType && width == 0 && video.Width > 0)
                    {
                        (width, height) = video;
                    }
                }

                position = body + size;
            }

            return new ContainerInfo(result, width, height);
        }

        /// <summary>Visible frame size of a Video element: pixel size minus the crop margins.</summary>
        private static (int Width, int Height) ParseVideo(byte[] data, long start, long end)
        {
            long width = 0, height = 0, cropTop = 0, cropBottom = 0, cropLeft = 0, cropRight = 0;
            var position = start;
            while (position < end && TryHeader(data, position, out var id, out var size, out var headerLength))
            {
                var length = (int)Math.Min(size, Math.Max(0, data.Length - position - headerLength));
                var value = (long)ReadUnsigned(data.AsSpan((int)(position + headerLength), length));
                switch (id)
                {
                    case PixelWidthId: width = value; break;
                    case PixelHeightId: height = value; break;
                    case PixelCropTopId: cropTop = value; break;
                    case PixelCropBottomId: cropBottom = value; break;
                    case PixelCropLeftId: cropLeft = value; break;
                    case PixelCropRightId: cropRight = value; break;
                }

                position += headerLength + size;
            }

            width -= cropLeft + cropRight;
            height -= cropTop + cropBottom;
            return width is > 0 and < 100_000 && height is > 0 and < 100_000 ? ((int)width, (int)height) : default;
        }

        internal static string CodecName(string codecId) => codecId.ToUpperInvariant() switch
        {
            "S_TEXT/UTF8" or "S_TEXT/ASCII" => "SRT",
            "S_TEXT/ASS" or "S_ASS" => "ASS",
            "S_TEXT/SSA" or "S_SSA" => "SSA",
            "S_TEXT/WEBVTT" or "D_WEBVTT/SUBTITLES" or "D_WEBVTT/CAPTIONS" => "VTT",
            "S_HDMV/PGS" => "PGS",
            "S_HDMV/TEXTST" => "TEXTST",
            "S_VOBSUB" => "VOBSUB",
            "S_DVBSUB" => "DVB",
            "S_TEXT/USF" => "USF",
            "S_KATE" => "KATE",
            var other => other.StartsWith("S_", StringComparison.Ordinal) ? other[2..] : other,
        };

        /// <summary>Reads an element header; unknown sizes are reported as <see cref="long.MaxValue"/>.</summary>
        private static bool TryHeader(byte[] data, long position, out uint id, out long size, out int headerLength)
        {
            id = 0;
            size = 0;
            headerLength = 0;
            if (position < 0 || position >= data.Length)
            {
                return false;
            }

            var idLength = VintLength(data[position]);
            if (idLength is 0 or > 4 || position + idLength >= data.Length)
            {
                return false;
            }

            for (var i = 0; i < idLength; i++)
            {
                id = (id << 8) | data[position + i];
            }

            var sizeLength = VintLength(data[position + idLength]);
            if (sizeLength == 0 || position + idLength + sizeLength > data.Length)
            {
                return false;
            }

            var first = data[position + idLength] & (0xFF >> sizeLength);
            ulong value = (ulong)first;
            var allOnes = first == (0xFF >> sizeLength);
            for (var i = 1; i < sizeLength; i++)
            {
                var b = data[position + idLength + i];
                allOnes &= b == 0xFF;
                value = (value << 8) | b;
            }

            size = allOnes ? long.MaxValue : (long)Math.Min(value, long.MaxValue / 2);
            headerLength = idLength + sizeLength;
            return true;
        }

        private static int VintLength(byte first)
        {
            for (var length = 1; length <= 8; length++)
            {
                if ((first & (0x80 >> (length - 1))) != 0)
                {
                    return length;
                }
            }

            return 0;
        }

        private static ulong ReadUnsigned(ReadOnlySpan<byte> value)
        {
            ulong result = 0;
            foreach (var b in value[..Math.Min(value.Length, 8)])
            {
                result = (result << 8) | b;
            }

            return result;
        }
    }

    /// <summary>ISO BMFF (MP4, MOV, M4V, 3GP): moov → trak → mdia → hdlr (subtitle handlers, "vide" + tkhd size).</summary>
    private static class IsoBmff
    {
        private static readonly string[] SubtitleHandlers = ["sbtl", "subt", "clcp"];

        public static ContainerInfo Read(Stream stream)
        {
            var position = 0L;
            var length = stream.Length;
            while (position + 8 <= length)
            {
                var header = ReadAt(stream, position, 16);
                if (header.Length < 8)
                {
                    break;
                }

                long size = BinaryPrimitives.ReadUInt32BigEndian(header);
                var type = Encoding.ASCII.GetString(header, 4, 4);
                var headerLength = 8;
                if (size == 1 && header.Length >= 16)
                {
                    size = (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8));
                    headerLength = 16;
                }
                else if (size == 0)
                {
                    size = length - position;
                }

                if (size < headerLength)
                {
                    break;
                }

                if (type == "moov")
                {
                    return size - headerLength > MaxIndexBytes
                        ? ContainerInfo.Empty
                        : ParseMoov(ReadAt(stream, position + headerLength, (int)(size - headerLength)));
                }

                position += size;
            }

            return ContainerInfo.Empty;
        }

        private static ContainerInfo ParseMoov(byte[] moov)
        {
            var result = new List<string>();
            int width = 0, height = 0;
            foreach (var (type, start, end) in Boxes(moov, 0, moov.Length))
            {
                if (type != "trak")
                {
                    continue;
                }

                string? handler = null;
                string? format = null;
                var size = Boxes(moov, start, end).Where(b => b.Type == "tkhd").Select(b => TrackSize(moov, b.Start, b.End)).FirstOrDefault();
                foreach (var mdia in Boxes(moov, start, end).Where(b => b.Type == "mdia"))
                {
                    foreach (var child in Boxes(moov, mdia.Start, mdia.End))
                    {
                        if (child.Type == "hdlr" && child.End - child.Start >= 12)
                        {
                            handler = Encoding.ASCII.GetString(moov, child.Start + 8, 4);
                        }
                        else if (child.Type == "minf")
                        {
                            format = SampleFormat(moov, child.Start, child.End) ?? format;
                        }
                    }
                }

                if (handler != null && SubtitleHandlers.Contains(handler))
                {
                    result.Add(FormatName(format));
                }
                else if (handler == "vide" && width == 0 && size.Width > 0)
                {
                    (width, height) = size;
                }
            }

            return new ContainerInfo(result, width, height);
        }

        /// <summary>Presentation size from tkhd (16.16 fixed point, after the 3x3 matrix).</summary>
        private static (int Width, int Height) TrackSize(byte[] data, int start, int end)
        {
            var version = data[start];
            var offset = start + (version == 1 ? 88 : 76);
            if (offset + 8 > end)
            {
                return default;
            }

            var width = (int)(BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset)) >> 16);
            var height = (int)(BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + 4)) >> 16);
            return width > 0 && height > 0 ? (width, height) : default;
        }

        /// <summary>Fourcc of the first sample entry in minf → stbl → stsd.</summary>
        private static string? SampleFormat(byte[] data, int start, int end)
        {
            foreach (var stbl in Boxes(data, start, end).Where(b => b.Type == "stbl"))
            {
                foreach (var stsd in Boxes(data, stbl.Start, stbl.End).Where(b => b.Type == "stsd"))
                {
                    // version/flags (4) + entry count (4) + first entry: size (4) + format (4)
                    if (stsd.End - stsd.Start >= 16)
                    {
                        return Encoding.ASCII.GetString(data, stsd.Start + 12, 4);
                    }
                }
            }

            return null;
        }

        internal static string FormatName(string? fourcc) => fourcc switch
        {
            "tx3g" or "text" => "TX3G",
            "wvtt" => "VTT",
            "stpp" => "TTML",
            "c608" => "CEA-608",
            "c708" => "CEA-708",
            null or "" => "TEXT",
            _ => fourcc.Trim().ToUpperInvariant(),
        };

        private static IEnumerable<(string Type, int Start, int End)> Boxes(byte[] data, int start, int end)
        {
            var position = start;
            while (position + 8 <= end)
            {
                long size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(position));
                var headerLength = 8;
                if (size == 1 && position + 16 <= end)
                {
                    size = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(position + 8));
                    headerLength = 16;
                }
                else if (size == 0)
                {
                    size = end - position;
                }

                if (size < headerLength || position + size > end)
                {
                    yield break;
                }

                yield return (Encoding.ASCII.GetString(data, position + 4, 4), position + headerLength, (int)(position + size));
                position += (int)size;
            }
        }
    }

    /// <summary>MPEG-TS / M2TS: subtitle streams declared in the PMT.</summary>
    private static class TransportStream
    {
        private const byte SyncByte = 0x47;

        public static (int Size, int Offset)? DetectPacketLayout(byte[] head)
        {
            foreach (var (size, offset) in new[] { (188, 0), (192, 4) })
            {
                if (head.Length > offset + size * 3
                    && head[offset] == SyncByte && head[offset + size] == SyncByte && head[offset + size * 2] == SyncByte)
                {
                    return (size, offset);
                }
            }

            return null;
        }

        public static IReadOnlyList<string> Read(byte[] data, int packetSize, int syncOffset)
        {
            var sections = new Dictionary<int, List<byte>>();
            var pmtPids = new HashSet<int>();
            var parsedPmts = new HashSet<int>();
            var patSeen = false;
            var result = new List<string>();

            for (var p = syncOffset; p + 188 <= data.Length; p += packetSize)
            {
                if (data[p] != SyncByte)
                {
                    continue;
                }

                var pid = ((data[p + 1] & 0x1F) << 8) | data[p + 2];
                var isTable = pid == 0 || pmtPids.Contains(pid);
                if (!isTable || parsedPmts.Contains(pid) || (pid == 0 && patSeen))
                {
                    continue;
                }

                var unitStart = (data[p + 1] & 0x40) != 0;
                var adaptation = (data[p + 3] >> 4) & 0x3;
                var payload = p + 4;
                if (adaptation is 2 or 0)
                {
                    continue;
                }

                if (adaptation == 3)
                {
                    payload += 1 + data[payload];
                }

                if (payload >= p + 188)
                {
                    continue;
                }

                if (unitStart)
                {
                    var pointer = data[payload];
                    var sectionStart = payload + 1 + pointer;
                    if (sectionStart >= p + 188)
                    {
                        continue;
                    }

                    sections[pid] = new List<byte>(data.AsSpan(sectionStart, p + 188 - sectionStart).ToArray());
                }
                else if (sections.TryGetValue(pid, out var pending))
                {
                    pending.AddRange(data.AsSpan(payload, p + 188 - payload).ToArray());
                }
                else
                {
                    continue;
                }

                var section = sections[pid];
                if (section.Count < 3)
                {
                    continue;
                }

                var sectionLength = ((section[1] & 0x0F) << 8) | section[2];
                if (section.Count < sectionLength + 3)
                {
                    continue;
                }

                var table = section.GetRange(0, sectionLength + 3).ToArray();
                sections.Remove(pid);
                if (pid == 0 && table[0] == 0x00)
                {
                    patSeen = true;
                    for (var i = 8; i + 4 <= table.Length - 4; i += 4)
                    {
                        var program = (table[i] << 8) | table[i + 1];
                        if (program != 0)
                        {
                            pmtPids.Add(((table[i + 2] & 0x1F) << 8) | table[i + 3]);
                        }
                    }
                }
                else if (table[0] == 0x02)
                {
                    parsedPmts.Add(pid);
                    result.AddRange(ParsePmt(table));
                    if (patSeen && pmtPids.IsSubsetOf(parsedPmts))
                    {
                        break;
                    }
                }
            }

            return result;
        }

        private static IEnumerable<string> ParsePmt(byte[] table)
        {
            var end = table.Length - 4; // CRC32
            var programInfoLength = ((table[10] & 0x0F) << 8) | table[11];
            for (var i = 12 + programInfoLength; i + 5 <= end;)
            {
                var streamType = table[i];
                var infoLength = ((table[i + 3] & 0x0F) << 8) | table[i + 4];
                var descriptors = table.AsSpan(i + 5, Math.Max(0, Math.Min(infoLength, end - i - 5)));
                var name = streamType switch
                {
                    0x90 => "PGS",
                    0x92 => "TEXTST",
                    0x06 => DescriptorSubtitle(descriptors),
                    _ => null,
                };
                if (name != null)
                {
                    yield return name;
                }

                i += 5 + infoLength;
            }
        }

        /// <summary>Private PES streams carry subtitles when they have a DVB subtitling or teletext subtitle descriptor.</summary>
        private static string? DescriptorSubtitle(ReadOnlySpan<byte> descriptors)
        {
            for (var d = 0; d + 2 <= descriptors.Length;)
            {
                var tag = descriptors[d];
                var length = descriptors[d + 1];
                var body = descriptors.Slice(d + 2, Math.Min(length, descriptors.Length - d - 2));
                if (tag == 0x59)
                {
                    return "DVB";
                }

                if (tag == 0x56)
                {
                    // Teletext entries: language (3) + type (5 bits) + magazine/page; 2 and 5 are subtitle pages.
                    for (var e = 0; e + 5 <= body.Length; e += 5)
                    {
                        if ((body[e + 3] >> 3) is 2 or 5)
                        {
                            return "TELETEXT";
                        }
                    }
                }

                d += 2 + length;
            }

            return null;
        }
    }
}
