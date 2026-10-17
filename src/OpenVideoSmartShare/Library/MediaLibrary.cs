using System.Globalization;
using System.Text.Json;
using OpenVideoSmartShare.Infrastructure;
using OpenVideoSmartShare.Settings;

namespace OpenVideoSmartShare.Library;

/// <summary>
/// Scans the shared folders for videos and keeps the current <see cref="LibrarySnapshot"/>.
/// A scan simply re-reads the folders, so new files appear on every refresh (at startup and
/// from the Refresh button) without a separate database update step.
/// </summary>
internal sealed class MediaLibrary : IDisposable
{
    private static readonly EnumerationOptions ListOptions = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
    };

    private readonly SettingsStore _settings;
    private readonly string _cacheFile;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<string, CachedDetails> _detailsCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _detailsAttempted = new(StringComparer.OrdinalIgnoreCase);
    private volatile LibrarySnapshot _snapshot = LibrarySnapshot.Empty;
    private Task? _scanTask;
    private bool _scanPending;
    private Task? _detailsTask;
    private uint _updateId = 1;
    private string _contentSignature = "";

    public MediaLibrary(SettingsStore settings, string cacheFile)
    {
        _settings = settings;
        _cacheFile = cacheFile;
    }

    /// <summary>Raised (on a worker thread) when a new snapshot replaces the current one.</summary>
    public event EventHandler? SnapshotChanged;

    /// <summary>Raised (on a worker thread) when durations/resolutions of some items became known.</summary>
    public event EventHandler? DetailsChanged;

    /// <summary>Raised (on a worker thread) when a scan starts or ends.</summary>
    public event EventHandler? ScanStateChanged;

    public LibrarySnapshot Snapshot => _snapshot;

    public bool IsScanning
    {
        get
        {
            lock (_gate)
            {
                return _scanTask != null;
            }
        }
    }

    /// <summary>Restores the last scan from the cache so the list is available before the folders are read.</summary>
    public void LoadCache()
    {
        try
        {
            if (!File.Exists(_cacheFile))
            {
                return;
            }

            var cache = JsonSerializer.Deserialize<LibraryCache>(File.ReadAllText(_cacheFile));
            if (cache == null)
            {
                return;
            }

            lock (_gate)
            {
                _updateId = Math.Max(1, cache.UpdateId);
                _contentSignature = cache.ContentSignature ?? "";
                foreach (var entry in cache.Details ?? [])
                {
                    _detailsCache[entry.Path] = entry;
                }
            }

            if (cache.FoldersSignature == FoldersSignature(SharedFolders()) && cache.Items != null)
            {
                var result = new ScanResult();
                foreach (var entry in cache.Items)
                {
                    result.AddCached(entry);
                }

                result.Folders.AddRange(cache.Folders?.Select(f => (f.Id, f.ParentId, f.Title)) ?? []);
                var items = result.Items.Select(WithCachedDetails).ToList();
                Publish(LibrarySnapshot.Build(items, result.Folders, _updateId, [], null));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warn("Library cache unreadable, starting empty", ex);
        }
    }

    /// <summary>Starts (or queues) a rescan of the shared folders.</summary>
    public Task RefreshAsync()
    {
        lock (_gate)
        {
            _scanPending = true;
            _scanTask ??= Task.Run(ScanLoop);
            return _scanTask;
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
    }

    private void ScanLoop()
    {
        // Wait until RefreshAsync has published _scanTask so IsScanning is already true.
        lock (_gate)
        {
        }

        ScanStateChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            while (true)
            {
                lock (_gate)
                {
                    if (!_scanPending || _shutdown.IsCancellationRequested)
                    {
                        _scanTask = null;
                        break;
                    }

                    _scanPending = false;
                }

                try
                {
                    ScanOnce();
                }
                catch (Exception ex)
                {
                    Log.Error("Library scan failed", ex);
                }
            }
        }
        finally
        {
            ScanStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ScanOnce()
    {
        var folders = SharedFolders();
        var started = DateTime.Now;
        var preferredTags = SubtitleFinder.LanguageTags(CultureInfo.InstalledUICulture);
        var result = new ScanResult();
        foreach (var folder in folders)
        {
            if (_shutdown.IsCancellationRequested)
            {
                break;
            }

            var root = NormalizeRoot(folder.Path);
            if (!Directory.Exists(root))
            {
                Log.Warn($"Shared folder not reachable: {root}");
                result.Unreachable.Add(root);
                continue;
            }

            var rootId = VideoItem.MakeId('d', root);
            if (result.FolderIds.Add(rootId))
            {
                result.Folders.Add((rootId, LibrarySnapshot.RootId, RootTitle(root)));
            }

            Walk(root, rootId, folder.IncludeSubfolders, result, preferredTags);
        }

        var items = result.Items.Select(WithCachedDetails).ToList();
        var signature = ContentSignature(items, result.Folders);
        uint updateId;
        lock (_gate)
        {
            if (signature != _contentSignature)
            {
                _updateId++;
                _contentSignature = signature;
            }

            updateId = _updateId;
        }

        Publish(LibrarySnapshot.Build(items, result.Folders, updateId, result.Unreachable, DateTime.Now));
        Log.Info($"Scan completed: {items.Count} videos in {(DateTime.Now - started).TotalSeconds:0.0}s, " +
                 $"{result.Unreachable.Count} unreachable folders, update id {updateId}");
        SaveCache(result, FoldersSignature(folders));
        EnsureDetailsWorker();
    }

    private void Walk(string directory, string directoryId, bool recurse, ScanResult result, IReadOnlyList<string> preferredTags)
    {
        List<FileSystemInfo> entries;
        try
        {
            entries = new DirectoryInfo(directory).EnumerateFileSystemInfos("*", ListOptions).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Warn($"Cannot list {directory}", ex);
            return;
        }

        var fileNames = entries.OfType<FileInfo>().Select(f => f.Name).ToList();
        foreach (var file in entries.OfType<FileInfo>())
        {
            var mime = MediaTypes.GetVideoMime(file.Extension);
            if (mime == null || !result.Paths.Add(file.FullName))
            {
                continue;
            }

            result.Items.Add(new VideoItem
            {
                Id = VideoItem.MakeId('v', file.FullName),
                FullPath = file.FullName,
                ParentId = directoryId,
                MimeType = mime,
                Size = file.Length,
                Modified = file.LastWriteTime,
                ExternalSubtitles = SubtitleFinder.FindAll(file.FullName, fileNames, preferredTags),
            });
        }

        if (!recurse)
        {
            return;
        }

        foreach (var sub in entries.OfType<DirectoryInfo>())
        {
            // Junctions and symlinks could create cycles.
            if ((sub.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            var subId = VideoItem.MakeId('d', sub.FullName);
            if (result.FolderIds.Add(subId))
            {
                result.Folders.Add((subId, directoryId, sub.Name));
                Walk(sub.FullName, subId, recurse, result, preferredTags);
            }
        }
    }

    private VideoItem WithCachedDetails(VideoItem item)
    {
        lock (_gate)
        {
            if (_detailsCache.TryGetValue(item.FullPath, out var cached)
                && cached.Size == item.Size && cached.ModifiedTicks == item.Modified.Ticks
                && cached.ProbeVersion == CachedDetails.CurrentProbeVersion)
            {
                item.Details = cached.ToDetails();
            }
        }

        return item;
    }

    /// <summary>
    /// Duration and bitrate from the Windows property system; subtitle tracks from the container
    /// headers, which also supply the frame size when Windows has none (typical for MKV).
    /// </summary>
    internal static MediaDetails ReadDetails(string path)
    {
        var shell = MediaInfoReader.Read(path);
        var container = ContainerInfoReader.Read(path);
        var (width, height) = shell.Width > 0 && shell.Height > 0
            ? (shell.Width, shell.Height)
            : (container.Width, container.Height);
        return shell with { Width = width, Height = height, EmbeddedSubtitles = container.Subtitles };
    }

    private void EnsureDetailsWorker()
    {
        lock (_gate)
        {
            if (_detailsTask is { IsCompleted: false })
            {
                return;
            }

            _detailsTask = Task.Run(DetailsLoop);
        }
    }

    private void DetailsLoop()
    {
        var lastNotify = DateTime.UtcNow;
        var changed = 0;
        var restart = true;
        while (restart && !_shutdown.IsCancellationRequested)
        {
            restart = false;
            var snapshot = _snapshot;
            List<VideoItem> pending;
            lock (_gate)
            {
                pending = snapshot.Items.Where(i => i.Details == null && _detailsAttempted.Add(AttemptKey(i))).ToList();
            }

            foreach (var item in pending)
            {
                if (_shutdown.IsCancellationRequested)
                {
                    break;
                }

                if (!ReferenceEquals(snapshot, _snapshot))
                {
                    // A rescan published new item objects; continue on those.
                    lock (_gate)
                    {
                        foreach (var skipped in pending.Where(p => p.Details == null))
                        {
                            _detailsAttempted.Remove(AttemptKey(skipped));
                        }
                    }

                    restart = true;
                    break;
                }

                var details = ReadDetails(item.FullPath);
                item.Details = details;
                lock (_gate)
                {
                    _detailsCache[item.FullPath] = CachedDetails.From(item, details);
                }

                changed++;
                if ((DateTime.UtcNow - lastNotify).TotalSeconds >= 1)
                {
                    DetailsChanged?.Invoke(this, EventArgs.Empty);
                    lastNotify = DateTime.UtcNow;
                    changed = 0;
                }
            }
        }

        if (changed > 0)
        {
            DetailsChanged?.Invoke(this, EventArgs.Empty);
        }

        SaveDetailsOnly();
    }

    private static string AttemptKey(VideoItem item) => $"{item.FullPath}|{item.Size}|{item.Modified.Ticks}";

    private void Publish(LibrarySnapshot snapshot)
    {
        _snapshot = snapshot;
        SnapshotChanged?.Invoke(this, EventArgs.Empty);
    }

    private List<SharedFolder> SharedFolders() =>
        _settings.Read(s => s.SharedFolders.Select(f => new SharedFolder { Path = f.Path, IncludeSubfolders = f.IncludeSubfolders }).ToList());

    /// <summary>"Z:" becomes "Z:\" so that it means the drive root rather than its current directory.</summary>
    internal static string NormalizeRoot(string path)
    {
        var trimmed = path.Trim();
        if (trimmed.Length == 2 && trimmed[1] == ':')
        {
            return trimmed + "\\";
        }

        return trimmed.Length > 3 ? trimmed.TrimEnd('\\', '/') : trimmed;
    }

    internal static string RootTitle(string root)
    {
        var name = Path.GetFileName(root.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(name) ? root : name;
    }

    private static string FoldersSignature(IEnumerable<SharedFolder> folders) =>
        string.Join("|", folders.Select(f => $"{NormalizeRoot(f.Path).ToUpperInvariant()}:{f.IncludeSubfolders}"));

    private static string ContentSignature(IEnumerable<VideoItem> items, IEnumerable<(string Id, string ParentId, string Title)> folders)
    {
        var text = string.Join("\n", items.Select(i => $"{i.Id}|{i.Size}|{i.Modified.Ticks}|{string.Join(";", i.ExternalSubtitles)}").Order(StringComparer.Ordinal))
                   + "\n" + string.Join("\n", folders.Select(f => f.Id).Order(StringComparer.Ordinal));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
    }

    private void SaveCache(ScanResult result, string foldersSignature)
    {
        LibraryCache cache;
        lock (_gate)
        {
            // Forget details of files that are no longer shared.
            foreach (var stale in _detailsCache.Keys.Where(p => !result.Paths.Contains(p)).ToList())
            {
                _detailsCache.Remove(stale);
            }

            cache = new LibraryCache
            {
                UpdateId = _updateId,
                ContentSignature = _contentSignature,
                FoldersSignature = foldersSignature,
                Items = result.Items.Select(i => new CachedItem
                {
                    Path = i.FullPath,
                    ParentId = i.ParentId,
                    Mime = i.MimeType,
                    Size = i.Size,
                    ModifiedTicks = i.Modified.Ticks,
                    Subtitles = [.. i.ExternalSubtitles],
                }).ToList(),
                Folders = result.Folders.Select(f => new CachedFolder { Id = f.Id, ParentId = f.ParentId, Title = f.Title }).ToList(),
                Details = _detailsCache.Values.ToList(),
            };
        }

        WriteCache(cache);
    }

    private void SaveDetailsOnly()
    {
        try
        {
            if (!File.Exists(_cacheFile))
            {
                return;
            }

            var cache = JsonSerializer.Deserialize<LibraryCache>(File.ReadAllText(_cacheFile)) ?? new LibraryCache();
            lock (_gate)
            {
                cache.Details = _detailsCache.Values.ToList();
            }

            WriteCache(cache);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warn("Unable to update library cache", ex);
        }
    }

    private void WriteCache(LibraryCache cache)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile)!);
            var temp = _cacheFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(cache));
            File.Move(temp, _cacheFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Unable to write library cache", ex);
        }
    }

    private sealed class ScanResult
    {
        public List<VideoItem> Items { get; } = [];

        public HashSet<string> Paths { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> FolderIds { get; } = new(StringComparer.Ordinal);

        public List<(string Id, string ParentId, string Title)> Folders { get; } = [];

        public List<string> Unreachable { get; } = [];

        public void AddCached(CachedItem entry)
        {
            if (!Paths.Add(entry.Path))
            {
                return;
            }

            Items.Add(new VideoItem
            {
                Id = VideoItem.MakeId('v', entry.Path),
                FullPath = entry.Path,
                ParentId = entry.ParentId,
                MimeType = entry.Mime,
                Size = entry.Size,
                Modified = new DateTime(entry.ModifiedTicks, DateTimeKind.Local),
                ExternalSubtitles = entry.Subtitles ?? (entry.Subtitle == null ? [] : [entry.Subtitle]),
            });
        }
    }

    private sealed class LibraryCache
    {
        public uint UpdateId { get; set; }

        public string? ContentSignature { get; set; }

        public string? FoldersSignature { get; set; }

        public List<CachedItem>? Items { get; set; }

        public List<CachedFolder>? Folders { get; set; }

        public List<CachedDetails>? Details { get; set; }
    }

    private sealed class CachedItem
    {
        public string Path { get; set; } = "";

        public string ParentId { get; set; } = "";

        public string Mime { get; set; } = "";

        public long Size { get; set; }

        public long ModifiedTicks { get; set; }

        public string? Subtitle { get; set; }

        public List<string>? Subtitles { get; set; }
    }

    private sealed class CachedFolder
    {
        public string Id { get; set; } = "";

        public string ParentId { get; set; } = "";

        public string Title { get; set; } = "";
    }

    private sealed class CachedDetails
    {
        /// <summary>Bumped when the way details are read changes, so older cache entries are read again.</summary>
        public const int CurrentProbeVersion = 2;

        public int ProbeVersion { get; set; }

        public string Path { get; set; } = "";

        public long Size { get; set; }

        public long ModifiedTicks { get; set; }

        public long? DurationTicks { get; set; }

        public int Width { get; set; }

        public int Height { get; set; }

        public long Bitrate { get; set; }

        public List<string>? EmbeddedSubtitles { get; set; }

        public static CachedDetails From(VideoItem item, MediaDetails details) => new()
        {
            ProbeVersion = CurrentProbeVersion,
            Path = item.FullPath,
            Size = item.Size,
            ModifiedTicks = item.Modified.Ticks,
            DurationTicks = details.Duration?.Ticks,
            Width = details.Width,
            Height = details.Height,
            Bitrate = details.BitsPerSecond,
            EmbeddedSubtitles = [.. details.EmbeddedSubtitles],
        };

        public MediaDetails ToDetails() =>
            new(DurationTicks.HasValue ? TimeSpan.FromTicks(DurationTicks.Value) : null, Width, Height, Bitrate)
            {
                EmbeddedSubtitles = EmbeddedSubtitles ?? [],
            };
    }
}
