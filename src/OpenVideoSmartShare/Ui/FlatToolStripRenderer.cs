namespace OpenVideoSmartShare.Ui;

/// <summary>
/// Professional renderer with a flat background and square corners, so that two strips placed
/// side by side (the toolbar and the Settings strip) look like one.
/// </summary>
internal sealed class FlatToolStripRenderer : ToolStripProfessionalRenderer
{
    /// <summary>The strip background, for panels that show around a strip.</summary>
    public static Color Background => new FlatColors().ToolStripGradientMiddle;

    public FlatToolStripRenderer()
        : base(new FlatColors())
    {
        RoundedEdges = false;
    }

    /// <summary>False for a strip laid over another one, which draws the bottom line itself.</summary>
    public bool DrawBorder { get; init; } = true;

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        if (DrawBorder)
        {
            base.OnRenderToolStripBorder(e);
        }
    }

    private sealed class FlatColors : ProfessionalColorTable
    {
        public override Color ToolStripGradientBegin => ToolStripGradientMiddle;

        public override Color ToolStripGradientEnd => ToolStripGradientMiddle;
    }
}
