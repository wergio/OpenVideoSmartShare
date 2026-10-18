namespace OpenVideoSmartShare.Ui;

/// <summary>
/// Tab control drawn as file cards: the selected tab joins the white page and the others
/// sit behind it, each with its own border (the themed tabs barely show where one ends).
/// </summary>
internal sealed class CardTabControl : TabControl
{
    private static readonly Color CardBorder = SystemColors.ControlDark;
    private static readonly Color PageColor = SystemColors.Window;
    private static readonly Color BackTabColor = SystemColors.ControlLight;
    private static readonly Color HotTabColor = Blend(SystemColors.ControlLight, SystemColors.Window);

    private int _hotIndex = -1;

    public CardTabControl()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    /// <summary>Space around the tab text, in pixels at 96 DPI.</summary>
    public void ScalePadding(int horizontal, int vertical) =>
        Padding = new Point(LogicalToDeviceUnits(horizontal), LogicalToDeviceUnits(vertical));

    protected override void OnControlAdded(ControlEventArgs e)
    {
        base.OnControlAdded(e);
        if (e.Control is TabPage page)
        {
            page.UseVisualStyleBackColor = false;
            page.BackColor = PageColor;
        }
    }

    protected override void OnHandleCreated(EventArgs e) => WithNativeFont(() => base.OnHandleCreated(e));

    protected override void OnFontChanged(EventArgs e) => WithNativeFont(() => base.OnFontChanged(e));

    protected override void OnSelectedIndexChanged(EventArgs e)
    {
        base.OnSelectedIndexChanged(e);
        Invalidate();
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hot = Enumerable.Range(0, TabCount).FirstOrDefault(i => GetTabRect(i).Contains(e.Location), -1);
        if (hot != _hotIndex)
        {
            _hotIndex = hot;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hotIndex = -1;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? SystemColors.Control);
        if (TabCount == 0)
        {
            return;
        }

        var stripBottom = GetTabRect(0).Bottom;
        var page = new Rectangle(0, stripBottom, Width - 1, Height - stripBottom - 1);
        using (var fill = new SolidBrush(PageColor))
        {
            g.FillRectangle(fill, page);
        }

        using var border = new Pen(CardBorder);
        g.DrawRectangle(border, page);

        for (var i = 0; i < TabCount; i++)
        {
            if (i != SelectedIndex)
            {
                DrawTab(g, border, i, selected: false);
            }
        }

        // Last, so that it overlaps its neighbours and covers the page border below it.
        if (SelectedIndex >= 0)
        {
            DrawTab(g, border, SelectedIndex, selected: true);
        }
    }

    private void DrawTab(Graphics g, Pen border, int index, bool selected)
    {
        var bounds = GetTabRect(index);
        var lift = LogicalToDeviceUnits(2);
        if (selected)
        {
            bounds = Rectangle.FromLTRB(bounds.Left - lift, bounds.Top - lift, bounds.Right + lift, bounds.Bottom + 1);
        }
        else
        {
            bounds = Rectangle.FromLTRB(bounds.Left, bounds.Top + lift, bounds.Right, bounds.Bottom);
        }

        bounds.Intersect(new Rectangle(0, 0, Width - 1, Height));
        var color = selected ? PageColor : index == _hotIndex ? HotTabColor : BackTabColor;
        using (var fill = new SolidBrush(color))
        {
            g.FillRectangle(fill, bounds);
        }

        // Left, top and right edges; the selected card stays open towards the page.
        g.DrawLines(border,
        [
            new Point(bounds.Left, bounds.Bottom - (selected ? 1 : 0)),
            new Point(bounds.Left, bounds.Top),
            new Point(bounds.Right, bounds.Top),
            new Point(bounds.Right, bounds.Bottom - (selected ? 1 : 0)),
        ]);

        var text = Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, selected ? bounds.Bottom - lift : bounds.Bottom);
        TextRenderer.DrawText(g, TabPages[index].Text, Font, text, SystemColors.ControlText,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);

        if (selected && Focused && ShowFocusCues)
        {
            var focus = text;
            focus.Inflate(-LogicalToDeviceUnits(4), -LogicalToDeviceUnits(3));
            ControlPaint.DrawFocusRectangle(g, focus);
        }
    }

    /// <summary>
    /// The native control sizes the tabs with its own font, which WinForms only hands to
    /// controls that do not paint themselves.
    /// </summary>
    private void WithNativeFont(Action action)
    {
        SetStyle(ControlStyles.UserPaint, false);
        try
        {
            action();
        }
        finally
        {
            SetStyle(ControlStyles.UserPaint, true);
        }
    }

    private static Color Blend(Color a, Color b) =>
        Color.FromArgb((a.R + b.R) / 2, (a.G + b.G) / 2, (a.B + b.B) / 2);
}
