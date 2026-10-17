using System.Globalization;
using OpenVideoSmartShare.Library;

namespace OpenVideoSmartShare.Ui;

/// <summary>Columns of the video list, in display order.</summary>
internal enum VideoColumn
{
    Title,
    Duration,
    Subtitles,
    Size,
    Resolution,
    Date,
    Folder,
}

/// <summary>Flicker-free details list of <see cref="VideoItem"/> rows with column sorting.</summary>
internal sealed class VideoListView : ListView
{
    private readonly Dictionary<string, ListViewItem> _rows = new(StringComparer.Ordinal);

    public VideoListView()
    {
        View = View.Details;
        FullRowSelect = true;
        MultiSelect = false;
        HideSelection = false;
        DoubleBuffered = true;
        ShowItemToolTips = true;
        foreach (var _ in Enum.GetValues<VideoColumn>())
        {
            Columns.Add("");
        }

        ColumnClick += (_, e) => SortBy((VideoColumn)e.Column, toggle: true);
        this.KeepLastColumnFilled();
    }

    public VideoColumn SortColumn { get; private set; } = VideoColumn.Title;

    public bool SortDescending { get; private set; }

    public VideoItem? SelectedVideo => SelectedItems.Count > 0 ? SelectedItems[0].Tag as VideoItem : null;

    public void SetHeaders(Func<VideoColumn, string> header)
    {
        foreach (var column in Enum.GetValues<VideoColumn>())
        {
            Columns[(int)column].Text = header(column);
        }
    }

    /// <summary>Replaces the rows, keeping the selection when the same video is still present.</summary>
    public void SetItems(IEnumerable<VideoItem> items)
    {
        var selectedId = SelectedVideo?.Id;
        BeginUpdate();
        try
        {
            ListViewItemSorter = null;
            Items.Clear();
            _rows.Clear();
            var rows = items.Select(CreateRow).ToArray();
            Items.AddRange(rows);
            ApplySort();
            if (selectedId != null && _rows.TryGetValue(selectedId, out var row))
            {
                row.Selected = true;
                row.Focused = true;
                row.EnsureVisible();
            }
        }
        finally
        {
            EndUpdate();
        }

        // The vertical scrollbar may have appeared or disappeared.
        this.FillLastColumn();
    }

    /// <summary>Refreshes duration/resolution cells after the metadata reader filled them in.</summary>
    public void RefreshDetails()
    {
        BeginUpdate();
        try
        {
            foreach (var row in _rows.Values)
            {
                var item = (VideoItem)row.Tag!;
                row.SubItems[(int)VideoColumn.Duration].Text = FormatDuration(item.Details?.Duration);
                row.SubItems[(int)VideoColumn.Resolution].Text = ResolutionText(item);
                row.SubItems[(int)VideoColumn.Subtitles].Text = SubtitleText(item);
            }

            if (SortColumn is VideoColumn.Duration or VideoColumn.Resolution)
            {
                ApplySort();
            }
        }
        finally
        {
            EndUpdate();
        }
    }

    public void SortBy(VideoColumn column, bool toggle, bool? descending = null)
    {
        if (toggle)
        {
            SortDescending = SortColumn == column && !SortDescending;
        }
        else if (descending.HasValue)
        {
            SortDescending = descending.Value;
        }

        SortColumn = column;
        ApplySort();
    }

    private void ApplySort()
    {
        ListViewItemSorter = new RowComparer(SortColumn, SortDescending);
        Sort();
    }

    private ListViewItem CreateRow(VideoItem item)
    {
        var row = new ListViewItem(item.Title) { Tag = item, ToolTipText = item.FullPath };
        row.SubItems.Add(FormatDuration(item.Details?.Duration));
        row.SubItems.Add(SubtitleText(item));
        row.SubItems.Add(FormatSize(item.Size));
        row.SubItems.Add(ResolutionText(item));
        row.SubItems.Add(item.Modified.ToString("d", CultureInfo.CurrentCulture));
        row.SubItems.Add(item.FolderPath);
        _rows[item.Id] = row;
        return row;
    }

    /// <summary>Formats of external files and embedded tracks, with the total count when above one: "SRT (42)".</summary>
    internal static string SubtitleText(VideoItem item)
    {
        var formats = item.ExternalSubtitleFormats.Concat(item.EmbeddedSubtitleFormats).ToList();
        if (formats.Count == 0)
        {
            return "";
        }

        var names = string.Join(", ", formats.Distinct(StringComparer.OrdinalIgnoreCase));
        return formats.Count > 1 ? $"{names} ({formats.Count})" : names;
    }

    internal static string ResolutionText(VideoItem item) =>
        ResolutionLabel.Format(item.Details?.Width ?? 0, item.Details?.Height ?? 0);

    internal static string FormatDuration(TimeSpan? duration) =>
        duration is { } d ? $"{(int)d.TotalHours}:{d.Minutes:00}:{d.Seconds:00}" : "";

    internal static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : value.ToString(value < 10 ? "0.00" : value < 100 ? "0.0" : "0", CultureInfo.CurrentCulture) + " " + units[unit];
    }

    private sealed class RowComparer(VideoColumn column, bool descending) : System.Collections.IComparer
    {
        public int Compare(object? x, object? y)
        {
            var a = (VideoItem)((ListViewItem)x!).Tag!;
            var b = (VideoItem)((ListViewItem)y!).Tag!;
            var result = column switch
            {
                VideoColumn.Duration => Nullable.Compare(a.Details?.Duration, b.Details?.Duration),
                VideoColumn.Subtitles => a.SubtitleCount.CompareTo(b.SubtitleCount),
                VideoColumn.Size => a.Size.CompareTo(b.Size),
                VideoColumn.Resolution => ((long)(a.Details?.Width ?? 0) * (a.Details?.Height ?? 0))
                    .CompareTo((long)(b.Details?.Width ?? 0) * (b.Details?.Height ?? 0)),
                VideoColumn.Date => a.Modified.CompareTo(b.Modified),
                VideoColumn.Folder => StringComparer.CurrentCultureIgnoreCase.Compare(a.FolderPath, b.FolderPath),
                _ => 0,
            };
            if (result == 0)
            {
                result = StringComparer.CurrentCultureIgnoreCase.Compare(a.Title, b.Title);
            }

            return descending ? -result : result;
        }
    }
}
