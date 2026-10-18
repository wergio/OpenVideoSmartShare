using System.Drawing.Imaging;
using System.Reflection;
using Microsoft.Win32;

namespace OpenVideoSmartShare.Infrastructure;

/// <summary>The embedded application icon, also served to TVs as the device icon, and the tray glyphs.</summary>
internal static class AppIcon
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static readonly Lazy<byte[]> IconBytes = new(() => ReadResource("OpenVideoSmartShare.app.ico"));
    private static readonly Lazy<byte[]> TrayWhiteBytes = new(() => ReadResource("OpenVideoSmartShare.tray-white.ico"));
    private static readonly Lazy<byte[]> TrayBlackBytes = new(() => ReadResource("OpenVideoSmartShare.tray-black.ico"));

    public static Icon Create(int size = 32) => FromBytes(IconBytes.Value, size);

    /// <summary>Monochrome glyph for the notification area, white or black like the system icons beside it.</summary>
    public static Icon CreateTray(int size, bool lightTaskbar) =>
        FromBytes(lightTaskbar ? TrayBlackBytes.Value : TrayWhiteBytes.Value, size);

    /// <summary>Whether the taskbar is light ("Choose your default Windows mode"); dark when the value is missing.</summary>
    public static bool TaskbarUsesLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
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

    private static Icon FromBytes(byte[] bytes, int size)
    {
        using var memory = new MemoryStream(bytes);
        return new Icon(memory, size, size);
    }

    private static byte[] ReadResource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)!;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
