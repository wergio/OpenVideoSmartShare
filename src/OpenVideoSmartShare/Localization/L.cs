using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace OpenVideoSmartShare.Localization;

/// <summary>A UI language the application ships a translation for.</summary>
internal sealed record UiLanguage(string Code, string NativeName);

/// <summary>
/// String table lookup. Translations are embedded JSON files (Languages/&lt;code&gt;.json);
/// English is the fallback for missing keys and for unsupported system languages.
/// </summary>
internal static class L
{
    public const string AutoLanguage = "auto";
    private const string Fallback = "en";

    public static IReadOnlyList<UiLanguage> Languages { get; } =
    [
        new("cs", "Čeština"),
        new("da", "Dansk"),
        new("de", "Deutsch"),
        new("el", "Ελληνικά"),
        new("en", "English"),
        new("es", "Español"),
        new("fr", "Français"),
        new("it", "Italiano"),
        new("hu", "Magyar"),
        new("nl", "Nederlands"),
        new("nb", "Norsk"),
        new("pl", "Polski"),
        new("pt-BR", "Português (Brasil)"),
        new("pt-PT", "Português (Portugal)"),
        new("fi", "Suomi"),
        new("sv", "Svenska"),
        new("tr", "Türkçe"),
        new("ru", "Русский"),
        new("th", "ไทย"),
        new("ko", "한국어"),
        new("ja", "日本語"),
        new("zh-Hans", "简体中文"),
        new("zh-Hant", "繁體中文"),
    ];

    private static readonly object Gate = new();
    private static Dictionary<string, string> _fallback = new(StringComparer.Ordinal);
    private static Dictionary<string, string> _current = new(StringComparer.Ordinal);

    /// <summary>Raised after the active language changes (on the calling thread).</summary>
    public static event EventHandler? LanguageChanged;

    /// <summary>Effective language code (never "auto").</summary>
    public static string CurrentCode { get; private set; } = Fallback;

    /// <summary>Applies a language setting: a supported code or <see cref="AutoLanguage"/>.</summary>
    public static void SetLanguage(string? setting)
    {
        var code = Resolve(setting, CultureInfo.InstalledUICulture);
        lock (Gate)
        {
            if (_fallback.Count == 0)
            {
                _fallback = Load(Fallback);
            }

            _current = code == Fallback ? _fallback : Load(code);
            CurrentCode = code;
        }

        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Returns the translated text for <paramref name="key"/>.</summary>
    public static string T(string key)
    {
        lock (Gate)
        {
            if (_current.TryGetValue(key, out var value) || _fallback.TryGetValue(key, out value))
            {
                return value;
            }
        }

        return key;
    }

    /// <summary>Returns the translated composite format filled with <paramref name="args"/>.</summary>
    public static string F(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(key), args);

    /// <summary>Maps a language setting to a supported code, using the OS language for "auto".</summary>
    internal static string Resolve(string? setting, CultureInfo systemCulture)
    {
        if (!string.IsNullOrWhiteSpace(setting) && setting != AutoLanguage)
        {
            var exact = Languages.FirstOrDefault(l => string.Equals(l.Code, setting, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
            {
                return exact.Code;
            }
        }

        return FromCulture(systemCulture);
    }

    internal static string FromCulture(CultureInfo culture)
    {
        // Walk up the culture chain (e.g. it-CH -> it) looking for a shipped language.
        for (var c = culture; !string.IsNullOrEmpty(c.Name); c = c.Parent)
        {
            var name = c.Name;
            if (name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            {
                var traditional = name.Contains("Hant", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith("-TW", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith("-HK", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith("-MO", StringComparison.OrdinalIgnoreCase);
                return traditional ? "zh-Hant" : "zh-Hans";
            }

            if (name is "no" or "nn" || name.StartsWith("nb", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("nn", StringComparison.OrdinalIgnoreCase))
            {
                return "nb";
            }

            if (string.Equals(name, "pt", StringComparison.OrdinalIgnoreCase))
            {
                return "pt-PT";
            }

            var match = Languages.FirstOrDefault(l => string.Equals(l.Code, name, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                return match.Code;
            }
        }

        return Fallback;
    }

    internal static Dictionary<string, string> Load(string code)
    {
        var resource = $"OpenVideoSmartShare.Localization.Languages.{code}.json";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource);
        if (stream == null)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }
}
