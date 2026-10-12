namespace ThothTAS.Editor;

/// <summary>A visual frame annotation; never inserts anything into the script text or RTF.</summary>
internal sealed class FrameAnnotatedRichTextBox : RichTextBox
{
    private readonly Label annotation = new()
    {
        AutoSize = true, Visible = false, TabStop = false,
        ForeColor = Color.FromArgb(25, 99, 190), BackColor = Color.FromArgb(255, 239, 166),
        TextAlign = ContentAlignment.MiddleLeft,
        AccessibleName = "Current input segment frame"
    };
    private int sourceLine = -1;
    private int frame;
    private bool arranging;
    internal string AnnotationText => sourceLine >= 0 && frame > 0 ? annotation.Text : "";
    internal bool AnnotationVisible => annotation.Visible;
    internal Color AnnotationColor => annotation.ForeColor;
    internal Rectangle AnnotationBounds => annotation.Bounds;

    internal FrameAnnotatedRichTextBox()
    {
        Controls.Add(annotation);
        annotation.MouseDown += (_, _) => Focus();
        VisibleChanged += (_, _) => ArrangeAnnotation();
    }

    internal void ShowFrame(int zeroBasedLine, int segmentFrame)
    {
        sourceLine = zeroBasedLine;
        frame = segmentFrame;
        annotation.Text = " --- " + segmentFrame.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ArrangeAnnotation();
    }

    private void ArrangeAnnotation()
    {
        if (arranging || !IsHandleCreated || IsDisposed) return;
        arranging = true;
        try
        {
            var lines = Lines;
            if (sourceLine < 0 || sourceLine >= lines.Length || frame <= 0 || lines[sourceLine].Length == 0)
            { annotation.Visible = false; return; }
            int start = GetFirstCharIndexFromLine(sourceLine);
            int end = start + lines[sourceLine].Length;
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
            // EM_POSFROMCHAR at TextLength is not consistent across RichEdit versions.
            var point = GetPositionFromCharIndex(end - 1);
            using var graphics = CreateGraphics();
            point.X += TextRenderer.MeasureText(graphics, lines[sourceLine][^1].ToString(), Font, new Size(int.MaxValue, int.MaxValue), flags).Width;
            annotation.Font = Font;
            var visible = point.Y >= 0 && point.Y + annotation.PreferredHeight <= ClientSize.Height && point.X >= 0 && point.X < ClientSize.Width - 8;
            annotation.Location = new Point(point.X + 2, point.Y - 1);
            annotation.Visible = visible;
            if (visible) annotation.BringToFront();
        }
        finally { arranging = false; }
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        // Native RichEdit scrolls without raising managed Paint events.
        if (m.Msg is 0x000F or 0x0005 or 0x0114 or 0x0115 or 0x020A or 0x020E or 0x04DE)
            ArrangeAnnotation();
    }
}
