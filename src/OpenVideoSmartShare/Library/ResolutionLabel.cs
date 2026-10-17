namespace OpenVideoSmartShare.Library;

/// <summary>Short names of standard video resolutions (1080p, 2160p...) for the video list.</summary>
internal static class ResolutionLabel
{
    private const double Tolerance = 0.02;

    private static readonly (string Label, int Width, int Height, bool MatchWidth)[] Standards =
    [
        ("4320p", 7680, 4320, true),
        ("2160p", 3840, 2160, true),
        ("1440p", 2560, 1440, true),
        ("1080p", 1920, 1080, true),
        ("720p", 1280, 720, true),
        // SD shares the 720-pixel width between PAL and NTSC: only the height tells them apart.
        ("576p", 720, 576, false),
        ("480p", 720, 480, false),
    ];

    /// <summary>
    /// The standard name when the height matches one (2560x1080 → 1080p) or, for HD, the width does,
    /// as in cinemascope crops (3840x1600 → 2160p, 1920x800 → 1080p); otherwise "WxH". Empty when unknown.
    /// </summary>
    public static string Format(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return "";
        }

        foreach (var standard in Standards)
        {
            if (Near(height, standard.Height))
            {
                return standard.Label;
            }
        }

        foreach (var standard in Standards)
        {
            if (standard.MatchWidth && Near(width, standard.Width) && height <= standard.Height)
            {
                return standard.Label;
            }
        }

        return $"{width}x{height}";
    }

    private static bool Near(int value, int target) => Math.Abs(value - target) <= target * Tolerance;
}
