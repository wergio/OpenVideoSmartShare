using System.Drawing.Imaging;
using System.Reflection;

namespace OpenVideoSmartShare.Infrastructure;

/// <summary>The embedded application icon, also served to TVs as the device icon.</summary>
internal static class AppIcon
{
    private static readonly Lazy<byte[]> IconBytes = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("OpenVideoSmartShare.app.ico")!;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    });

    public static Icon Create(int size = 32)
    {
        using var memory = new MemoryStream(IconBytes.Value);
        return new Icon(memory, size, size);
    }

    /// <summary>The icon rendered as PNG (transparent) or JPEG (on white) at the given size.</summary>
    public static byte[] Render(int size, bool jpeg)
    {
        using var icon = Create(size);
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(jpeg ? Color.White : Color.Transparent);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            using var source = icon.ToBitmap();
            g.DrawImage(source, new Rectangle(0, 0, size, size));
        }

        using var output = new MemoryStream();
        bitmap.Save(output, jpeg ? ImageFormat.Jpeg : ImageFormat.Png);
        return output.ToArray();
    }
}
