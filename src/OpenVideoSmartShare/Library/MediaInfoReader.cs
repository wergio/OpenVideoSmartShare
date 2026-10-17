using System.Runtime.InteropServices;
using OpenVideoSmartShare.Infrastructure;

namespace OpenVideoSmartShare.Library;

/// <summary>Duration, frame size, bitrate and embedded subtitle tracks of a video file.</summary>
internal sealed record MediaDetails(TimeSpan? Duration, int Width, int Height, long BitsPerSecond)
{
    public static readonly MediaDetails Unknown = new(null, 0, 0, 0);

    public string? Resolution => Width > 0 && Height > 0 ? $"{Width}x{Height}" : null;

    /// <summary>One format (SRT, PGS...) per subtitle track stored inside the file.</summary>
    public IReadOnlyList<string> EmbeddedSubtitles { get; init; } = [];
}

/// <summary>
/// Reads video metadata through the Windows property system (the same data Explorer shows),
/// so no media parsing library is needed. Works for MKV/MP4/AVI/TS on Windows 10/11.
/// </summary>
internal static class MediaInfoReader
{
    private static readonly Guid MediaFormat = new("64440490-4C8B-11D1-8B70-080036B11A03");
    private static readonly Guid VideoFormat = new("64440491-4C8B-11D1-8B70-080036B11A03");
    private static readonly PropertyKey Duration = new(MediaFormat, 3);       // VT_UI8, 100 ns units
    private static readonly PropertyKey FrameWidth = new(VideoFormat, 3);     // VT_UI4
    private static readonly PropertyKey FrameHeight = new(VideoFormat, 4);    // VT_UI4
    private static readonly PropertyKey TotalBitrate = new(VideoFormat, 43);  // VT_UI4, bit/s
    private static readonly PropertyKey EncodingBitrate = new(VideoFormat, 8); // VT_UI4, bit/s

    private const int GpsOpenSlowItem = 0x10;
    private const int GpsBestEffort = 0x40;
    private const int GpsNoOplock = 0x80;
    private const ushort VtUi4 = 19;
    private const ushort VtUi8 = 21;

    public static MediaDetails Read(string path)
    {
        IPropertyStore? store = null;
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            var hr = SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, GpsOpenSlowItem | GpsBestEffort | GpsNoOplock, ref iid, out store);
            if (hr != 0 || store == null)
            {
                return MediaDetails.Unknown;
            }

            var duration = ReadUInt64(store, Duration);
            var width = ReadUInt32(store, FrameWidth);
            var height = ReadUInt32(store, FrameHeight);
            var bitrate = ReadUInt32(store, TotalBitrate);
            if (bitrate == 0)
            {
                bitrate = ReadUInt32(store, EncodingBitrate);
            }

            return new MediaDetails(
                duration > 0 ? TimeSpan.FromTicks((long)duration) : null,
                (int)width,
                (int)height,
                bitrate);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            Log.Warn($"Media properties unavailable for {path}", ex);
            return MediaDetails.Unknown;
        }
        finally
        {
            if (store != null)
            {
                Marshal.ReleaseComObject(store);
            }
        }
    }

    private static ulong ReadUInt64(IPropertyStore store, PropertyKey key)
    {
        var k = key;
        if (store.GetValue(ref k, out var value) != 0)
        {
            return 0;
        }

        try
        {
            return value.Type switch
            {
                VtUi8 => value.UInt64,
                VtUi4 => value.UInt32,
                _ => 0,
            };
        }
        finally
        {
            PropVariantClear(ref value);
        }
    }

    private static uint ReadUInt32(IPropertyStore store, PropertyKey key) => (uint)Math.Min(ReadUInt64(store, key), uint.MaxValue);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHGetPropertyStoreFromParsingName(
        string path, IntPtr bindContext, int flags, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly struct PropertyKey(Guid formatId, uint propertyId)
    {
        private readonly Guid _formatId = formatId;
        private readonly uint _propertyId = propertyId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public uint UInt32;
        [FieldOffset(8)] public ulong UInt64;
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);

        [PreserveSig] int GetAt(uint index, out PropertyKey key);

        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);

        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);

        [PreserveSig] int Commit();
    }
}
