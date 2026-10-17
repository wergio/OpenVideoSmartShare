namespace OpenVideoSmartShare.Library;

/// <summary>A browsable folder in the ContentDirectory tree.</summary>
internal sealed class LibraryContainer
{
    public required string Id { get; init; }

    public required string ParentId { get; init; }

    /// <summary>Fixed title, or null when <see cref="TitleKey"/> is used.</summary>
    public string? Title { get; init; }

    /// <summary>Localization key resolved when the container is browsed.</summary>
    public string? TitleKey { get; init; }

    public List<LibraryContainer> Containers { get; } = [];

    public List<VideoItem> Items { get; } = [];

    public int ChildCount => Containers.Count + Items.Count;
}

/// <summary>Immutable view of the library produced by one scan.</summary>
internal sealed class LibrarySnapshot
{
    public const string RootId = "0";
    public const string AllVideosId = "all";

    public LibrarySnapshot(IReadOnlyList<VideoItem> items, IReadOnlyDictionary<string, LibraryContainer> containers,
        uint updateId, IReadOnlyList<string> unreachableFolders, DateTime? scannedAt)
    {
        Items = items;
        ById = items.ToDictionary(i => i.Id, StringComparer.Ordinal);
        Containers = containers;
        UpdateId = updateId;
        UnreachableFolders = unreachableFolders;
        ScannedAt = scannedAt;
    }

    public static LibrarySnapshot Empty { get; } = Build([], [], 1, [], null);

    /// <summary>All videos sorted by title.</summary>
    public IReadOnlyList<VideoItem> Items { get; }

    public IReadOnlyDictionary<string, VideoItem> ById { get; }

    public IReadOnlyDictionary<string, LibraryContainer> Containers { get; }

    /// <summary>ContentDirectory SystemUpdateID for this content.</summary>
    public uint UpdateId { get; }

    public IReadOnlyList<string> UnreachableFolders { get; }

    /// <summary>When the folders were last scanned; null for a snapshot restored from cache.</summary>
    public DateTime? ScannedAt { get; }

    /// <summary>
    /// Builds the container tree: root -> "All videos" plus one container per shared folder,
    /// with nested folders that (directly or below) contain videos.
    /// </summary>
    /// <param name="items">Videos; each ParentId must be a folder id from <paramref name="folders"/>.</param>
    /// <param name="folders">Folder id -> (parent id or root, display title), roots first.</param>
    public static LibrarySnapshot Build(IReadOnlyList<VideoItem> items,
        IReadOnlyList<(string Id, string ParentId, string Title)> folders,
        uint updateId, IReadOnlyList<string> unreachable, DateTime? scannedAt)
    {
        var sorted = items.OrderBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        var containers = new Dictionary<string, LibraryContainer>(StringComparer.Ordinal);
        var root = new LibraryContainer { Id = RootId, ParentId = "-1", Title = "root" };
        var all = new LibraryContainer { Id = AllVideosId, ParentId = RootId, TitleKey = "dlna.allVideos" };
        all.Items.AddRange(sorted);
        root.Containers.Add(all);
        containers[root.Id] = root;
        containers[all.Id] = all;

        foreach (var (id, parentId, title) in folders)
        {
            containers[id] = new LibraryContainer { Id = id, ParentId = parentId, Title = title };
        }

        foreach (var item in sorted)
        {
            if (containers.TryGetValue(item.ParentId, out var folder))
            {
                folder.Items.Add(item);
            }
        }

        // Keep only folders with videos somewhere below them, preserving the given order.
        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in sorted)
        {
            for (var id = item.ParentId; containers.ContainsKey(id) && id != RootId && keep.Add(id);)
            {
                id = containers[id].ParentId;
            }
        }

        foreach (var (id, parentId, _) in folders)
        {
            if (!keep.Contains(id))
            {
                containers.Remove(id);
                continue;
            }

            if (containers.TryGetValue(parentId, out var parent))
            {
                parent.Containers.Add(containers[id]);
            }
        }

        foreach (var container in containers.Values)
        {
            container.Containers.Sort((a, b) => ReferenceEquals(a, b) ? 0
                : a.Id == AllVideosId ? -1
                : b.Id == AllVideosId ? 1
                : StringComparer.CurrentCultureIgnoreCase.Compare(a.Title, b.Title));
        }

        return new LibrarySnapshot(sorted, containers, updateId, unreachable, scannedAt);
    }
}
