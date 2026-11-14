using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GitSync;

/// <summary>Colors of every screen, in one place.</summary>
internal static class Theme
{
    internal static readonly Color Background = Color.FromArgb(24, 24, 27);
    internal static readonly Color Surface = Color.FromArgb(31, 31, 35);
    internal static readonly Color Control = Color.FromArgb(51, 51, 51);
    internal static readonly Color Field = Color.FromArgb(46, 46, 46);
    internal static readonly Color Text = Color.FromArgb(240, 240, 240);
    internal static readonly Color DimText = Color.FromArgb(150, 150, 158);
    internal static readonly Color Highlight = Color.FromArgb(70, 70, 76);
    internal static readonly Color Hover = Color.FromArgb(37, 37, 43);
    internal static readonly Color Selection = Color.FromArgb(40, 66, 104);
    internal static readonly Color Separator = Color.FromArgb(48, 48, 54);
    internal static readonly Color Accent = Color.FromArgb(0, 120, 212);
    internal static readonly Color AccentHover = Color.FromArgb(30, 144, 232);
    internal static readonly Color Error = Color.FromArgb(232, 72, 85);
    internal static readonly Color Amber = Color.FromArgb(240, 170, 60);
    internal static readonly Color AmberFill = Color.FromArgb(70, 50, 18);
    internal static readonly Color Success = Color.FromArgb(90, 190, 110);
}

/// <summary>
/// Builders for the fields and buttons of the screens.
///
/// .NET's dark mode does not reach the raised border of the TextBox: it comes
/// out almost white and clashes with everything else. That border cannot be
/// colored, so the field is created without a border inside a panel, which
/// gives the background and the breathing room around the text.
/// </summary>
internal static class DarkControls
{
    /// <summary>Height of a one-line field, matching the drop downs.</summary>
    internal const int FieldHeight = 24;

    /// <summary>Returns the panel (what gets positioned) and the text box inside it.</summary>
    internal static (Panel Frame, TextBox Field) CreateField(int tabIndex)
    {
        var frame = new Panel
        {
            BackColor = Theme.Field,
            Padding = new Padding(4, 3, 4, 3),
            TabIndex = tabIndex
        };

        var field = new TextBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Field,
            ForeColor = Theme.Text,
            TabIndex = 0
        };

        frame.Controls.Add(field);

        return (frame, field);
    }

    /// <summary>
    /// Drop down in the color of the fields. Without this it gets the light
    /// border of the default theme. The items are drawn by hand because
    /// BackColor alone does not hold when the control gets focus.
    /// </summary>
    internal static ComboBox CreateList(int tabIndex)
    {
        var list = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            DrawMode = DrawMode.OwnerDrawFixed,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Control,
            ForeColor = Theme.Text,
            TabIndex = tabIndex
        };

        list.DrawItem += DrawListItem;

        return list;
    }

    internal static Label CreateLabel(string text, int x, int y) => new()
    {
        Text = text,
        Location = new Point(x, y),
        AutoSize = true,
        ForeColor = Theme.Text
    };

    /// <summary>
    /// Paints the closed box and the items of the open list. Only the open
    /// list highlights the item under the mouse; the closed box keeps one
    /// color, without the system's blue when it has focus.
    /// </summary>
    private static void DrawListItem(object? sender, DrawItemEventArgs e)
    {
        if (sender is not ComboBox combo)
        {
            return;
        }

        bool closedBox = e.State.HasFlag(DrawItemState.ComboBoxEdit);
        bool underMouse = e.State.HasFlag(DrawItemState.Selected);

        using var back = new SolidBrush(!closedBox && underMouse ? Theme.Highlight : Theme.Control);
        e.Graphics.FillRectangle(back, e.Bounds);

        if (e.Index < 0 || e.Index >= combo.Items.Count)
        {
            return;
        }

        TextRenderer.DrawText(
            e.Graphics, combo.Items[e.Index]?.ToString(), e.Font ?? combo.Font,
            new Rectangle(e.Bounds.X + 3, e.Bounds.Y, e.Bounds.Width - 6, e.Bounds.Height),
            combo.Enabled ? Theme.Text : Theme.DimText,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    // ---------- dark scrollbars ----------

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr window, string? subAppName, string? subIdList);

    /// <summary>
    /// Window scrollbars (Panel.AutoScroll, ListBox) stay light in .NET's dark
    /// mode. The Explorer dark theme is what Windows itself uses for them.
    /// Applied on every handle creation, because a recreated handle loses it.
    /// </summary>
    internal static void UseDarkScrollbars(Control control)
    {
        void Apply() => _ = SetWindowTheme(control.Handle, "DarkMode_Explorer", null);

        if (control.IsHandleCreated)
        {
            Apply();
        }

        control.HandleCreated += (_, _) => Apply();
    }

    internal static GraphicsPath RoundedRectangle(RectangleF r, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>
/// Button painted by itself.
///
/// In .NET's dark mode the themed button draws a light outline that
/// FlatAppearance.BorderSize = 0 does not remove — the background color is
/// honored, the border is not. Painting it here makes it look the same in
/// every state.
/// </summary>
internal class FlatButton : Button
{
    private bool _underMouse;
    private bool _pressed;

    private double? _progress;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Color NormalColor { get; set; } = Theme.Control;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Color HoverColor { get; set; } = Theme.Highlight;

    /// <summary>
    /// While set (0 to 1), the button turns into a progress bar: the accent
    /// fills it from the left and the text shows the percentage. The row keeps
    /// its layout during a download instead of growing a separate bar.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal double? Progress
    {
        get => _progress;
        set
        {
            _progress = value;
            Invalidate();
        }
    }

    internal FlatButton()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);

        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        ForeColor = Theme.Text;
    }

    /// <summary>The blue button of the main action (Update, Download).</summary>
    internal static FlatButton Accent(string text) => new()
    {
        Text = text,
        NormalColor = Theme.Accent,
        HoverColor = Theme.AccentHover
    };

    protected override void OnMouseEnter(EventArgs e)
    {
        _underMouse = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _underMouse = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        _pressed = true;
        Invalidate();
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(e);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Color back = !Enabled ? Theme.Control
            : _pressed || _underMouse ? HoverColor
            : NormalColor;

        e.Graphics.Clear(Parent?.BackColor ?? Theme.Background);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        using (GraphicsPath shape = DarkControls.RoundedRectangle(new RectangleF(0, 0, Width - 1, Height - 1), 3))
        {
            if (_progress is { } progress)
            {
                using var track = new SolidBrush(Theme.Control);
                using var fill = new SolidBrush(NormalColor);
                e.Graphics.FillPath(track, shape);

                Region? previous = e.Graphics.Clip;
                e.Graphics.SetClip(new RectangleF(0, 0, (float)(Width * Math.Clamp(progress, 0, 1)), Height));
                e.Graphics.FillPath(fill, shape);
                e.Graphics.Clip = previous;

                TextRenderer.DrawText(
                    e.Graphics, Text, Font, ClientRectangle, Theme.Text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                return;
            }

            using var brush = new SolidBrush(back);
            e.Graphics.FillPath(brush, shape);
        }

        TextRenderer.DrawText(
            e.Graphics, Text, Font, ClientRectangle, Enabled ? Theme.Text : Theme.DimText,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }
}

/// <summary>Dark colors for menus; the default ones stay light-grey in dark mode.</summary>
internal sealed class DarkMenuColors : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground => Theme.Surface;
    public override Color ImageMarginGradientBegin => Theme.Surface;
    public override Color ImageMarginGradientMiddle => Theme.Surface;
    public override Color ImageMarginGradientEnd => Theme.Surface;
    public override Color MenuBorder => Theme.Highlight;
    public override Color MenuItemBorder => Theme.Highlight;
    public override Color MenuItemSelected => Theme.Highlight;
    public override Color MenuItemSelectedGradientBegin => Theme.Highlight;
    public override Color MenuItemSelectedGradientEnd => Theme.Highlight;
    public override Color MenuItemPressedGradientBegin => Theme.Highlight;
    public override Color MenuItemPressedGradientEnd => Theme.Highlight;
    public override Color SeparatorDark => Theme.Separator;
    public override Color SeparatorLight => Theme.Surface;
    public override Color CheckBackground => Theme.Surface;
    public override Color CheckSelectedBackground => Theme.Highlight;
    public override Color CheckPressedBackground => Theme.Highlight;
}

internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    internal DarkMenuRenderer() : base(new DarkMenuColors())
    {
        RoundedEdges = false;
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Theme.Text : Theme.DimText;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = Theme.Text;
        base.OnRenderArrow(e);
    }

    /// <summary>The default check mark is a dark glyph on a light square; this one is drawn in the text color.</summary>
    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        Rectangle r = e.ImageRectangle;
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var pen = new Pen(Theme.Text, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        float x = r.X + (r.Width / 2f) - 6;
        float y = r.Y + (r.Height / 2f);

        g.DrawLines(pen, new[] { new PointF(x, y), new PointF(x + 4, y + 4), new PointF(x + 11, y - 4) });
    }
}
