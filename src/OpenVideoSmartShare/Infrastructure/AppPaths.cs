namespace OpenVideoSmartShare.Infrastructure;

/// <summary>Well-known folders used by the application.</summary>
internal static class AppPaths
{
    public const string AppName = "OpenVideoSmartShare";

    /// <summary>Roaming folder holding user settings (settings.json).</summary>
    public static string SettingsDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);

    /// <summary>Local folder holding caches and logs.</summary>
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);

    public static string SettingsFile => Path.Combine(SettingsDirectory, "settings.json");

    public static string LibraryCacheFile => Path.Combine(DataDirectory, "library-cache.json");

    public static string LogFile => Path.Combine(DataDirectory, "log.txt");

    public static string ExecutablePath => Environment.ProcessPath ?? Application.ExecutablePath;
}
