using System.Globalization;

namespace OpenVideoSmartShare.Library;

/// <summary>
/// Pairs a video with external subtitle files in the same folder. A subtitle must share the
/// video's base name ("movie.srt"); language-tagged names ("movie.ita.srt") are accepted too,
/// preferring tags of the given language.
/// </summary>
internal static class SubtitleFinder
{
    /// <summary>Tags that identify a language in subtitle file names (e.g. it, ita, italian, italiano).</summary>
    public static IReadOnlyList<string> LanguageTags(CultureInfo culture)
    {
        var tags = new List<string>
        {
            culture.TwoLetterISOLanguageName,
            culture.ThreeLetterISOLanguageName,
            culture.EnglishName.Split(' ')[0],
            culture.NativeName.Split(' ')[0],
        };
        return tags.Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The subtitle sent to TVs: the best match of <see cref="FindAll"/>.</summary>
    public static string? Find(string videoPath, IEnumerable<string> siblingFileNames, IReadOnlyList<string>? preferredTags = null) =>
        FindAll(videoPath, siblingFileNames, preferredTags).FirstOrDefault();

    /// <summary>Every subtitle file belonging to the video, best match first.</summary>
    /// <param name="videoPath">Full path of the video.</param>
    /// <param name="siblingFileNames">File names (no directory) present in the video's folder.</param>
    /// <param name="preferredTags">Language tags to prefer among tagged subtitles.</param>
    public static IReadOnlyList<string> FindAll(string videoPath, IEnumerable<string> siblingFileNames, IReadOnlyList<string>? preferredTags = null)
    {
        var directory = Path.GetDirectoryName(videoPath) ?? "";
        var baseName = Path.GetFileNameWithoutExtension(videoPath);
        var names = siblingFileNames as ICollection<string> ?? siblingFileNames.ToList();
        var hasIdx = names.Any(n =>
            string.Equals(Path.GetExtension(n), ".idx", StringComparison.OrdinalIgnoreCase));

        var matches = new List<(string Name, int Score)>();
        foreach (var name in names)
        {
            var ext = Path.GetExtension(name);
            if (!MediaTypes.IsSubtitle(ext))
            {
                continue;
            }

            // A .sub next to an .idx is a VobSub bitmap track, which TVs cannot render over DLNA.
            if (hasIdx && string.Equals(ext, ".sub", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var stem = Path.GetFileNameWithoutExtension(name);
            int score;
            if (string.Equals(stem, baseName, StringComparison.OrdinalIgnoreCase))
            {
                score = 0;
            }
            else if (stem.StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase))
            {
                var tags = stem[(baseName.Length + 1)..].Split('.', StringSplitOptions.RemoveEmptyEntries);
                var preferred = preferredTags != null
                    && tags.Any(t => preferredTags.Contains(t, StringComparer.OrdinalIgnoreCase));
                score = preferred ? 100 : 200;
            }
            else
            {
                continue;
            }

            matches.Add((name, score + MediaTypes.SubtitleRank(ext)));
        }

        return matches
            .OrderBy(m => m.Score)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Select(m => Path.Combine(directory, m.Name))
            .ToList();
    }
}
