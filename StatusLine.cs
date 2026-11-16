using System.Drawing;
using System.Windows.Forms;

namespace GitSync;

/// <summary>
/// The line of text under the list.
///
/// Not a Label: a Label is a native STATIC control underneath, and changing
/// its text several times a second, as a check does ("Checking 12/1368…"),
/// left half-drawn frames on screen. The previous count stayed with its end
/// cut off ("Checking 12/13") while the control's own text was already the
/// next one, so the line seemed to blink. This control paints the whole line
/// at once, double-buffered, every time the text changes.
/// </summary>
internal sealed class StatusLine : Control
{
    private const TextFormatFlags Flags =
        TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.SingleLine |
        TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;

    private readonly ToolTip _tips = new();
    private string _tip = string.Empty;

    internal StatusLine()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
    }

    /// <summary>
    /// Painted at once rather than when the window is next idle: during a
    /// check the results keep it busy, and the count shown fell a few behind.
    /// One line is quick to paint.
    /// </summary>
    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Invalidate();
        Update();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);

        // Centered with the odd pixel below, as the Label had it: Windows' own
        // vertical centering puts it above, one pixel higher than before.
        Size text = TextRenderer.MeasureText(e.Graphics, Text.Length > 0 ? Text : " ", Font, Size.Empty, Flags);
        var area = new Rectangle(Padding.Left, (Height - text.Height + 1) / 2, Math.Max(0, Width - Padding.Horizontal), text.Height);
        TextRenderer.DrawText(e.Graphics, Text, Font, area, ForeColor, Flags);

        // Ends in "…" when the window is too narrow: the whole text then shows
        // on hover, as it did with the Label's AutoEllipsis.
        bool cut = text.Width > area.Width;
        string tip = cut ? Text : string.Empty;

        if (tip != _tip)
        {
            _tip = tip;
            _tips.SetToolTip(this, tip.Length > 0 ? tip : null);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tips.Dispose();
        }

        base.Dispose(disposing);
    }
}
