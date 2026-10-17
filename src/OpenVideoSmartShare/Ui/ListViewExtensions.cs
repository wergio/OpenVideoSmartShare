namespace OpenVideoSmartShare.Ui;

internal static class ListViewExtensions
{
    /// <summary>Stretches the last column over the remaining width so no horizontal scrollbar appears.</summary>
    public static void FillLastColumn(this ListView list, int minimumLogicalWidth = 80)
    {
        if (list.Columns.Count == 0 || !list.IsHandleCreated)
        {
            return;
        }

        var others = 0;
        for (var i = 0; i < list.Columns.Count - 1; i++)
        {
            others += list.Columns[i].Width;
        }

        var remaining = list.ClientSize.Width - others;
        var minimum = list.LogicalToDeviceUnits(minimumLogicalWidth);
        var last = list.Columns[^1];
        var width = Math.Max(minimum, remaining);
        if (last.Width != width)
        {
            last.Width = width;
        }
    }

    /// <summary>Keeps the last column filling the list while it or its columns are resized.</summary>
    public static void KeepLastColumnFilled(this ListView list)
    {
        list.Resize += (_, _) => list.FillLastColumn();
        list.ColumnWidthChanged += (_, e) =>
        {
            if (e.ColumnIndex != list.Columns.Count - 1)
            {
                list.FillLastColumn();
            }
        };
        list.HandleCreated += (_, _) => list.FillLastColumn();
    }
}
