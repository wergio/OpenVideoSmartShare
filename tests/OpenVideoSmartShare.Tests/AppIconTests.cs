using OpenVideoSmartShare.Infrastructure;

namespace OpenVideoSmartShare.Tests;

public class AppIconTests
{
    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    public void EveryIconHasTheNotificationAreaSizes(int size)
    {
        using var app = AppIcon.Create(size);
        using var dark = AppIcon.CreateTray(size, lightTaskbar: false);
        using var light = AppIcon.CreateTray(size, lightTaskbar: true);

        Assert.Equal(size, app.Width);
        Assert.Equal(size, dark.Width);
        Assert.Equal(size, light.Width);
    }

    [Fact]
    public void TrayGlyphsMatchTheTaskbar()
    {
        using var dark = AppIcon.CreateTray(16, lightTaskbar: false);
        using var light = AppIcon.CreateTray(16, lightTaskbar: true);
        using var white = dark.ToBitmap();
        using var black = light.ToBitmap();

        var (whiteInk, blackInk) = (Ink(white), Ink(black));
        Assert.True(whiteInk.R > 200 && whiteInk.G > 200 && whiteInk.B > 200);
        Assert.True(blackInk.R < 50 && blackInk.G < 50 && blackInk.B < 50);
    }

    [Fact]
    public void DeviceIconRendersForTheTv()
    {
        var png = AppIcon.Render(120, jpeg: false);
        var jpeg = AppIcon.Render(48, jpeg: true);

        Assert.Equal(0x89, png[0]);
        Assert.Equal(0xFF, jpeg[0]);
    }

    // The most opaque pixel: the colour of the glyph itself.
    private static Color Ink(Bitmap bitmap)
    {
        var best = Color.Transparent;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.A > best.A)
                {
                    best = pixel;
                }
            }
        }

        return best;
    }
}
