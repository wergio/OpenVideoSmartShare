using System.Text.Json;
using OpenVideoSmartShare.Infrastructure;

namespace OpenVideoSmartShare.Settings;

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON; all access goes through a lock.</summary>
internal sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _gate = new();
    private AppSettings _settings;

    public SettingsStore(string path)
    {
        _path = path;
        _settings = LoadFromDisk(path);
    }

    /// <summary>Raised after <see cref="Update"/> saved new settings.</summary>
    public event EventHandler? Changed;

    /// <summary>Returns a deep copy of the current settings.</summary>
    public AppSettings Snapshot()
    {
        lock (_gate)
        {
            return Clone(_settings);
        }
    }

    /// <summary>Reads a value under the lock without copying everything.</summary>
    public T Read<T>(Func<AppSettings, T> selector)
    {
        lock (_gate)
        {
            return selector(_settings);
        }
    }

    /// <summary>Mutates the settings under the lock and persists them.</summary>
    public void Update(Action<AppSettings> mutate, bool notify = true)
    {
        lock (_gate)
        {
            mutate(_settings);
            Save(_settings);
        }

        if (notify)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private static AppSettings LoadFromDisk(string path)
    {
        AppSettings? settings = null;
        try
        {
            if (File.Exists(path))
            {
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warn($"Settings file unreadable, using defaults: {path}", ex);
        }

        settings ??= new AppSettings();
        var dirty = false;
        if (settings.ServerUuid == Guid.Empty)
        {
            settings.ServerUuid = Guid.NewGuid();
            dirty = true;
        }

        if (string.IsNullOrWhiteSpace(settings.ServerName))
        {
            settings.ServerName = AppSettings.DefaultServerName();
            dirty = true;
        }

        if (settings.HttpPort is <= 0 or > 65535)
        {
            settings.HttpPort = 52100;
            dirty = true;
        }

        if (dirty)
        {
            TrySave(path, settings);
        }

        return settings;
    }

    private void Save(AppSettings settings) => TrySave(_path, settings);

    private static void TrySave(string path, AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Unable to save settings to {path}", ex);
        }
    }

    private static AppSettings Clone(AppSettings settings) =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings, JsonOptions), JsonOptions)!;
}
