using System.Buffers.Binary;
using System.Text;
using OpenVideoSmartShare.Library;

namespace OpenVideoSmartShare.Tests;

public class ContainerInfoReaderTests
{
    [Fact]
    public void ReadsMatroskaFrameSizeAndSubtitleTracks()
    {
        var tracks = Ebml(0x1654AE6B,
            Ebml(0xAE, Ebml(0x83, Uint(1)), Ebml(0x86, Ascii("V_MPEGH/ISO/HEVC")),
                Ebml(0xE0, Ebml(0xB0, Uint(3840)), Ebml(0xBA, Uint(1600)))),
            Ebml(0xAE, Ebml(0x83, Uint(2)), Ebml(0x86, Ascii("A_EAC3"))),
            Ebml(0xAE, Ebml(0x83, Uint(0x11)), Ebml(0x86, Ascii("S_TEXT/UTF8"))),
            Ebml(0xAE, Ebml(0x83, Uint(0x11)), Ebml(0x86, Ascii("S_HDMV/PGS"))));
        var file = Concat(Ebml(0x1A45DFA3, Ebml(0x4282, Ascii("matroska"))), Ebml(0x18538067, tracks));

        var info = ContainerInfoReader.Read(new MemoryStream(file));

        Assert.Equal(3840, info.Width);
        Assert.Equal(1600, info.Height);
        Assert.Equal(["SRT", "PGS"], info.Subtitles);
    }

    [Fact]
    public void ReadsMp4FrameSizeAndSubtitleTracks()
    {
        var video = Box("trak", Box("tkhd", TrackHeader(1920, 800)), Box("mdia", Box("hdlr", Handler("vide"))));
        var subtitle = Box("trak", Box("tkhd", TrackHeader(0, 0)), Box("mdia", Box("hdlr", Handler("sbtl")),
            Box("minf", Box("stbl", Box("stsd", SampleDescription("tx3g"))))));
        var file = Concat(Box("ftyp", Ascii("isom"), new byte[4]), Box("moov", video, subtitle));

        var info = ContainerInfoReader.Read(new MemoryStream(file));

        Assert.Equal(1920, info.Width);
        Assert.Equal(800, info.Height);
        Assert.Equal(["TX3G"], info.Subtitles);
    }

    [Fact]
    public void ReadsTransportStreamSubtitleStreams()
    {
        var pat = Section(0x00, [0x00, 0x01, 0xC1, 0x00, 0x00, 0x00, 0x01, 0xF0, 0x00]);
        var pmt = Section(0x02,
        [
            0x00, 0x01, 0xC1, 0x00, 0x00, 0xE1, 0x00, 0xF0, 0x00,
            0x24, 0xE1, 0x00, 0xF0, 0x00,             // HEVC video
            0x90, 0xE1, 0x01, 0xF0, 0x00,             // PGS
            0x06, 0xE1, 0x02, 0xF0, 0x05, 0x59, 0x03, (byte)'i', (byte)'t', (byte)'a', // DVB subtitles
        ]);
        var file = Concat(Packet(0x0000, pat), Packet(0x1000, pmt), Packet(0x1FFF, []), Packet(0x1FFF, []));

        var info = ContainerInfoReader.Read(new MemoryStream(file));

        Assert.Equal(["PGS", "DVB"], info.Subtitles);
    }

    [Fact]
    public void UnknownContainerGivesEmptyInfo()
    {
        Assert.Same(ContainerInfo.Empty, ContainerInfoReader.Read(new MemoryStream(Encoding.ASCII.GetBytes("RIFF....AVI LIST"))));
    }

    /// <summary>Reads a real video when OVSS_SAMPLE_VIDEO points to one (skipped otherwise).</summary>
    [Fact]
    public void ReadsFrameSizeOfSampleVideo()
    {
        var sample = Environment.GetEnvironmentVariable("OVSS_SAMPLE_VIDEO");
        if (string.IsNullOrEmpty(sample) || !File.Exists(sample))
        {
            return;
        }

        var details = MediaLibrary.ReadDetails(sample);
        Assert.True(details.Width > 0 && details.Height > 0);
        Assert.NotEqual("", ResolutionLabel.Format(details.Width, details.Height));
    }

    [Theory]
    [InlineData(3840, 2160, "2160p")]
    [InlineData(3840, 1600, "2160p")]
    [InlineData(4096, 2160, "2160p")]
    [InlineData(1920, 1080, "1080p")]
    [InlineData(1920, 800, "1080p")]
    [InlineData(1440, 1080, "1080p")]
    [InlineData(2560, 1080, "1080p")]
    [InlineData(2560, 1440, "1440p")]
    [InlineData(1280, 720, "720p")]
    [InlineData(1280, 534, "720p")]
    [InlineData(720, 576, "576p")]
    [InlineData(704, 480, "480p")]
    [InlineData(1000, 562, "1000x562")]
    [InlineData(0, 0, "")]
    public void LabelsStandardResolutions(int width, int height, string expected)
    {
        Assert.Equal(expected, ResolutionLabel.Format(width, height));
    }

    private static byte[] Ebml(uint id, params byte[][] children)
    {
        var payload = Concat(children);
        var idBytes = id > 0xFFFFFF ? 4 : id > 0xFFFF ? 3 : id > 0xFF ? 2 : 1;
        var header = new byte[idBytes + 8];
        for (var i = 0; i < idBytes; i++)
        {
            header[i] = (byte)(id >> (8 * (idBytes - 1 - i)));
        }

        // 8-byte size: marker 0x01 followed by 7 bytes.
        header[idBytes] = 0x01;
        for (var i = 0; i < 7; i++)
        {
            header[idBytes + 1 + i] = (byte)((long)payload.Length >> (8 * (6 - i)));
        }

        return Concat(header, payload);
    }

    private static byte[] Uint(ulong value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        return bytes.SkipWhile((b, i) => b == 0 && i < 7).ToArray();
    }

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    private static byte[] Box(string type, params byte[][] children)
    {
        var payload = Concat(children);
        var header = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)(payload.Length + 8));
        Encoding.ASCII.GetBytes(type).CopyTo(header, 4);
        return Concat(header, payload);
    }

    private static byte[] TrackHeader(int width, int height)
    {
        var tkhd = new byte[84]; // version 0
        BinaryPrimitives.WriteUInt32BigEndian(tkhd.AsSpan(76), (uint)width << 16);
        BinaryPrimitives.WriteUInt32BigEndian(tkhd.AsSpan(80), (uint)height << 16);
        return tkhd;
    }

    private static byte[] Handler(string type) => Concat(new byte[8], Ascii(type), new byte[13]);

    private static byte[] SampleDescription(string format)
    {
        var stsd = new byte[24];
        BinaryPrimitives.WriteUInt32BigEndian(stsd.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(stsd.AsSpan(8), 16);
        Encoding.ASCII.GetBytes(format).CopyTo(stsd, 12);
        return stsd;
    }

    /// <summary>PSI section: table id, length, body and a (dummy) CRC32.</summary>
    private static byte[] Section(byte tableId, byte[] body)
    {
        var length = body.Length + 4;
        return Concat([tableId, (byte)(0xB0 | (length >> 8)), (byte)length], body, new byte[4]);
    }

    private static byte[] Packet(int pid, byte[] section)
    {
        var packet = Enumerable.Repeat((byte)0xFF, 188).ToArray();
        packet[0] = 0x47;
        packet[1] = (byte)((section.Length > 0 ? 0x40 : 0) | (pid >> 8));
        packet[2] = (byte)pid;
        packet[3] = 0x10;
        if (section.Length > 0)
        {
            packet[4] = 0x00; // pointer field
            section.CopyTo(packet, 5);
        }

        return packet;
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
}
