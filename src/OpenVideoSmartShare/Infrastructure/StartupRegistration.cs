using Microsoft.Win32;

namespace OpenVideoSmartShare.Infrastructure;

/// <summary>"Start with Windows" through the per-user Run key; the app starts minimized to the tray.</summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string TrayArgument = "--tray";

    public static string Command => $"\"{AppPaths.ExecutablePath}\" {TrayArgument}";

    public static bool IsRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(AppPaths.AppName) is string value
               && string.Equals(value, Command, StringComparison.OrdinalIgnoreCase);
    }

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled)
            {
                if (!string.Equals(key.GetValue(AppPaths.AppName) as string, Command, StringComparison.OrdinalIgnoreCase))
                {
                    key.SetValue(AppPaths.AppName, Command);
                    Log.Info($"Autostart registered: {Command}");
                }
            }
            else if (key.GetValue(AppPaths.AppName) != null)
            {
                key.DeleteValue(AppPaths.AppName, throwOnMissingValue: false);
                Log.Info("Autostart removed");
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Log.Warn("Cannot update the autostart registration", ex);
        }
    }
}
