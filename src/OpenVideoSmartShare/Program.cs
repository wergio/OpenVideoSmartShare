using OpenVideoSmartShare.Infrastructure;
using OpenVideoSmartShare.Localization;
using OpenVideoSmartShare.Settings;
using OpenVideoSmartShare.Ui;

namespace OpenVideoSmartShare;

internal static class Program
{
    private const string InstanceMutexName = @"Local\OpenVideoSmartShare.Instance";
    private const string ShowEventName = @"Local\OpenVideoSmartShare.Show";
    private const string ExitEventName = @"Local\OpenVideoSmartShare.Exit";
    private const string ExitArgument = "--exit";

    [STAThread]
    private static void Main(string[] args)
    {
        var exitRequested = args.Any(a => string.Equals(a, ExitArgument, StringComparison.OrdinalIgnoreCase));
        using var mutex = new Mutex(initiallyOwned: !exitRequested, InstanceMutexName, out var firstInstance);
        if (exitRequested || !firstInstance)
        {
            // Already running: ask the existing instance to show its window (or to quit, for --exit).
            if (EventWaitHandle.TryOpenExisting(exitRequested ? ExitEventName : ShowEventName, out var signal))
            {
                using (signal)
                {
                    signal.Set();
                }
            }

            return;
        }

        ApplicationConfiguration.Initialize();
        Log.Initialize(AppPaths.LogFile);
        Application.ThreadException += (_, e) => ReportCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportCrash(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };

        var settings = new SettingsStore(AppPaths.SettingsFile);
        L.SetLanguage(settings.Read(s => s.Language));
        if (settings.Read(s => s.StartWithWindows))
        {
            StartupRegistration.Apply(true);
        }

        var startHidden = args.Any(a => string.Equals(a, StartupRegistration.TrayArgument, StringComparison.OrdinalIgnoreCase));
        Log.Info($"Start {AppPaths.ExecutablePath} (language {L.CurrentCode}{(startHidden ? ", tray" : "")})");

        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        using var exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        var host = new AppHost(settings);
        using var context = new TrayApplicationContext(host, showEvent, exitEvent, startHidden);
        Application.Run(context);
        GC.KeepAlive(mutex);
    }

    private static void ReportCrash(Exception? ex)
    {
        Log.Error("Unhandled exception", ex);
        MessageBox.Show(ex?.ToString() ?? "Unknown error", AppPaths.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
