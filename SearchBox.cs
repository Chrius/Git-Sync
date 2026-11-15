using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GitSync;

/// <summary>
/// One-line search field in the color of the other fields: a magnifier on the
/// left, and an × on the right that clears it once something is typed. The
/// panel draws both; the borderless text box sits between them.
/// </summary>
internal sealed class SearchBox : Panel
{
    private readonly TextBox _field;
    private bool _clearHover;

    /// <summary>Raised on every change of the text.</summary>
    internal event EventHandler? QueryChanged;

    /// <summary>Enter: the owner applies the search at once instead of waiting for the typing to pause.</summary>
    internal event EventHandler? Submitted;

    internal SearchBox(string placeholder)
    {
        BackColor = Theme.Field;
        DoubleBuffered = true;
        Cursor = Cursors.IBeam;

        _field = new HintTextBox(placeholder)
        {
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Field,
            ForeColor = Theme.Text
        };

        _field.TextChanged += (_, _) =>
        {
            Invalidate(ClearBounds);
            QueryChanged?.Invoke(this, EventArgs.Empty);
        };

        _field.KeyDown += FieldKeyDown;
        Controls.Add(_field);
    }

    internal string Query => _field.Text;

    /// <summary>True while the caret is in the field: Ctrl+V then pastes text instead of adding a link.</summary>
    internal bool Editing => _field.Focused;

    internal void FocusAndSelect()
    {
        _field.Focus();
        _field.SelectAll();
    }

    private int Scaled(int value) => LogicalToDeviceUnits(value);

    private int IconWidth => Scaled(28);

    private Rectangle ClearBounds => new(ClientSize.Width - Scaled(26), 0, Scaled(26), ClientSize.Height);

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);

        // Borderless, the text box is only as tall as its font: centered by hand.
        _field.SetBounds(
            IconWidth,
            (ClientSize.Height - _field.Height) / 2,
            Math.Max(0, ClearBounds.Left - IconWidth),
            _field.Height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Magnifier: a ring with its handle down to the right.
        float size = Scaled(12);
        float x = (IconWidth - size) / 2f;
        float y = (ClientSize.Height - size) / 2f;
        float ring = size * 0.68f;

        using (var pen = new Pen(Theme.DimText, Scaled(15) / 10f))
        {
            g.DrawEllipse(pen, x, y, ring, ring);
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            float from = ring * 0.85f;
            g.DrawLine(pen, x + from, y + from, x + size - 1, y + size - 1);
        }

        if (_field.TextLength == 0)
        {
            return;
        }

        Rectangle clear = ClearBounds;
        float half = Scaled(4);
        float cx = clear.Left + (clear.Width / 2f);
        float cy = clear.Top + (clear.Height / 2f);

        using var cross = new Pen(_clearHover ? Theme.Text : Theme.DimText, Scaled(15) / 10f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };

        g.DrawLine(cross, cx - half, cy - half, cx + half, cy + half);
        g.DrawLine(cross, cx - half, cy + half, cx + half, cy - half);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        bool hover = _field.TextLength > 0 && ClearBounds.Contains(e.Location);
        Cursor = hover ? Cursors.Default : Cursors.IBeam;

        if (hover != _clearHover)
        {
            _clearHover = hover;
            Invalidate(ClearBounds);
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);

        if (_clearHover)
        {
            _clearHover = false;
            Invalidate(ClearBounds);
        }
    }

    /// <summary>The magnifier and the margins act as part of the field; the × clears it.</summary>
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        if (_field.TextLength > 0 && ClearBounds.Contains(e.Location))
        {
            _field.Clear();
        }

        _field.Focus();
    }

    private void FieldKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.KeyData)
        {
            // SuppressKeyPress on these: the edit control beeps on an Enter or
            // Escape it has no use for.
            case Keys.Escape:
                _field.Clear();
                e.SuppressKeyPress = true;
                break;

            case Keys.Enter:
                Submitted?.Invoke(this, EventArgs.Empty);
                e.SuppressKeyPress = true;
                break;

            // The plain edit control has no Ctrl+Backspace: it would type a
            // box character into the search instead of deleting the word.
            case Keys.Control | Keys.Back:
                DeletePreviousWord();
                e.SuppressKeyPress = true;
                break;
        }
    }

    private void DeletePreviousWord()
    {
        if (_field.SelectionLength > 0)
        {
            _field.SelectedText = string.Empty;
            return;
        }

        string text = _field.Text;
        int end = _field.SelectionStart;
        int start = end;

        while (start > 0 && char.IsWhiteSpace(text[start - 1]))
        {
            start--;
        }

        while (start > 0 && !char.IsWhiteSpace(text[start - 1]))
        {
            start--;
        }

        if (start < end)
        {
            _field.Text = text.Remove(start, end - start);
            _field.SelectionStart = start;
        }
    }

    /// <summary>
    /// Text box that draws its hint exactly where the edit control draws what
    /// is typed: at the top left of its formatting rectangle. The built-in
    /// PlaceholderText is drawn one pixel to the right and one lower, so the
    /// words jumped as soon as the first key was typed. Like the built-in one,
    /// the hint shows only while the field is empty and not focused.
    /// </summary>
    private sealed class HintTextBox : TextBox
    {
        private const int WmPaint = 0x000F;
        private const int EmGetRect = 0x00B2;

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left, Top, Right, Bottom;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, ref Rect lParam);

        // A field, not a property: a public-facing property on a control must
        // declare how the designer saves it (WFO1000), and this one never is.
        private readonly string _hint;

        internal HintTextBox(string hint) => _hint = hint;

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);

            if (m.Msg != WmPaint || TextLength > 0 || Focused || _hint.Length == 0)
            {
                return;
            }

            var area = new Rect();
            SendMessage(Handle, EmGetRect, IntPtr.Zero, ref area);

            using Graphics g = CreateGraphics();
            TextRenderer.DrawText(g, _hint, Font, Rectangle.FromLTRB(area.Left, area.Top, area.Right, area.Bottom), Theme.DimText,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        // The edit control does not repaint on its own when only the focus
        // changes, which is when the hint comes and goes.
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
    }
}
