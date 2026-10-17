using System.Text;

namespace OpenVideoSmartShare.Infrastructure;

/// <summary>Minimal thread-safe file logger with a single rotation step.</summary>
internal static class Log
{
    private const long MaxBytes = 5 * 1024 * 1024;
    private static readonly object Gate = new();
    private static string? _path;

    public static void Initialize(string path)
    {
        lock (Gate)
        {
            _path = path;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        }
    }

    public static void Info(string message) => Write("INFO ", message, null);

    public static void Warn(string message, Exception? ex = null) => Write("WARN ", message, ex);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        var line = new StringBuilder()
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
            .Append(' ').Append(level).Append(' ').Append(message);
        if (ex != null)
        {
            line.Append(" | ").Append(ex.GetType().Name).Append(": ").Append(ex.Message);
        }

        System.Diagnostics.Debug.WriteLine(line.ToString());
        lock (Gate)
        {
            if (_path == null)
            {
                return;
            }

            try
            {
                var info = new FileInfo(_path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    File.Copy(_path, Path.ChangeExtension(_path, ".old.txt"), overwrite: true);
                    File.WriteAllText(_path, string.Empty);
                }

                File.AppendAllText(_path, line.Append(Environment.NewLine).ToString(), Encoding.UTF8);
            }
            catch (IOException)
            {
                // Logging must never break the application.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
