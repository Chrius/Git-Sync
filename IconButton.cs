using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace GitSync;

/// <summary>
/// Square icon button, 40 x 40: discreet at rest and solid when the mouse is
/// over it. Disabled, it is barely visible and does not react.
///
/// Instead of disappearing, the icon stays drawn at low opacity: the user
/// sees there is something there without the button competing with the rest
/// of the screen.
///
/// The icons are drawn by code, so the button works without any image file.
/// </summary>
internal sealed class IconButton : Control
{
    private const int Side = 40;
    private const int IconMargin = 9;       // icon takes 22 x 22 in the center
    private const float FadeStep = 0.18f;   // ~90 ms to light up

    /// <summary>Opacity with the mouse away: visible, but dimmed.</summary>
    private const float RestOpacity = 0.45f;

    /// <summary>Disabled: clearly there, clearly not clickable.</summary>
    private const float DisabledOpacity = 0.14f;

    // Drawn once and shared; never disposed.
    internal static readonly Image GearIcon = DrawGear(128);
    internal static readonly Image PlusIcon = DrawPlus(128);
    internal static readonly Image RefreshIcon = DrawRefresh(128);
    internal static readonly Image UpdateAllIcon = DrawUpdateAll(128);

    private readonly Image _icon;
    private readonly System.Windows.Forms.Timer _fade;
    private bool _mouseOver;
    private float _opacity = RestOpacity;

    internal IconButton(Image icon)
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);

        _icon = icon;
        Size = new Size(Side, Side);
        Cursor = Cursors.Hand;
        TabStop = false;

        _fade = new System.Windows.Forms.Timer { Interval = 15 };
        _fade.Tick += Animate;
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;

        if (!Enabled)
        {
            _mouseOver = false;
        }

        _opacity = Enabled ? (_mouseOver ? 1f : RestOpacity) : DisabledOpacity;
        _fade.Stop();
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);

        if (Enabled)
        {
            _mouseOver = true;
            _fade.Start();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _mouseOver = false;

        if (Enabled)
        {
            _fade.Start();
        }
    }

    private void Animate(object? sender, EventArgs e)
    {
        float target = _mouseOver ? 1f : RestOpacity;

        _opacity = _opacity < target
            ? Math.Min(target, _opacity + FadeStep)
            : Math.Max(target, _opacity - FadeStep);

        if (Math.Abs(_opacity - target) < 0.001f)
        {
            _opacity = target;
            _fade.Stop();
        }

        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        // Rounded highlight behind the icon, measured from the resting opacity
        // so that at rest only the dimmed icon shows, without any background.
        float glow = Enabled ? Math.Clamp((_opacity - RestOpacity) / (1f - RestOpacity), 0f, 1f) : 0f;

        if (glow > 0.01f)
        {
            using GraphicsPath shape = DarkControls.RoundedRectangle(new RectangleF(0, 0, Width - 1, Height - 1), 9);
            using var brush = new SolidBrush(Color.FromArgb((int)(30 * glow), 255, 255, 255));
            g.FillPath(brush, shape);
        }

        var matrix = new ColorMatrix { Matrix33 = _opacity };
        using var attributes = new ImageAttributes();
        attributes.SetColorMatrix(matrix);

        Rectangle target = Rectangle.Inflate(ClientRectangle, -IconMargin, -IconMargin);
        g.DrawImage(_icon, target, 0, 0, _icon.Width, _icon.Height, GraphicsUnit.Pixel, attributes);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _fade.Stop();
            _fade.Dispose();
        }

        base.Dispose(disposing);
    }

    // ---------- icons ----------

    // A property, not a field: the icon fields above are initialized in text
    // order, before any field declared down here, and a field would still be
    // Color.Empty (transparent) when they are drawn.
    private static Color IconColor => Color.FromArgb(255, 230, 230, 238);

    private static (Bitmap Image, Graphics G) Canvas(int side)
    {
        var image = new Bitmap(side, side, PixelFormat.Format32bppArgb);
        Graphics g = Graphics.FromImage(image);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        return (image, g);
    }

    /// <summary>
    /// Gear with 8 teeth built as one polygon: an arc at the root between the
    /// teeth and a hole in the middle, cut out by the alternate fill mode.
    /// </summary>
    private static Image DrawGear(int side)
    {
        const int Teeth = 8;
        (Bitmap image, Graphics g) = Canvas(side);

        float center = side / 2f;
        float tipRadius = side * 0.469f;
        float rootRadius = side * 0.367f;
        float holeRadius = side * 0.164f;

        double step = Math.PI * 2 / Teeth;
        double halfTip = step * 0.16;
        double halfRoot = step * 0.29;

        var points = new List<PointF>();

        void Mark(double radius, double angle) =>
            points.Add(new PointF(
                (float)(center + radius * Math.Cos(angle)),
                (float)(center + radius * Math.Sin(angle))));

        for (int i = 0; i < Teeth; i++)
        {
            double baseAngle = i * step;

            Mark(rootRadius, baseAngle - halfRoot);
            Mark(tipRadius, baseAngle - halfTip);
            Mark(tipRadius, baseAngle + halfTip);
            Mark(rootRadius, baseAngle + halfRoot);

            // Follows the circle from one tooth to the next.
            double from = baseAngle + halfRoot;
            double to = baseAngle + step - halfRoot;

            for (int k = 1; k < 6; k++)
            {
                Mark(rootRadius, from + ((to - from) * k / 6.0));
            }
        }

        using (var path = new GraphicsPath { FillMode = FillMode.Alternate })
        using (var brush = new SolidBrush(IconColor))
        {
            path.AddPolygon([.. points]);
            path.AddEllipse(center - holeRadius, center - holeRadius, holeRadius * 2, holeRadius * 2);
            g.FillPath(brush, path);
        }

        g.Dispose();
        return image;
    }

    private static Image DrawPlus(int side)
    {
        (Bitmap image, Graphics g) = Canvas(side);

        float thickness = side * 0.15f;
        float length = side * 0.86f;
        float start = (side - length) / 2f;
        float middle = (side - thickness) / 2f;

        using (var brush = new SolidBrush(IconColor))
        using (GraphicsPath across = DarkControls.RoundedRectangle(new RectangleF(start, middle, length, thickness), thickness / 2))
        using (GraphicsPath down = DarkControls.RoundedRectangle(new RectangleF(middle, start, thickness, length), thickness / 2))
        {
            g.FillPath(brush, across);
            g.FillPath(brush, down);
        }

        g.Dispose();
        return image;
    }

    /// <summary>A circle open at the top right, ending in an arrowhead.</summary>
    private static Image DrawRefresh(int side)
    {
        (Bitmap image, Graphics g) = Canvas(side);

        float center = side / 2f;
        float radius = side * 0.34f;
        float width = side * 0.13f;
        const float Start = -40f;
        const float Sweep = 290f;

        using (var pen = new Pen(IconColor, width) { StartCap = LineCap.Round, EndCap = LineCap.Flat })
        {
            g.DrawArc(pen, center - radius, center - radius, radius * 2, radius * 2, Start, Sweep);
        }

        DrawArrowHead(g, center, radius, Start + Sweep, side * 0.30f);

        g.Dispose();
        return image;
    }

    /// <summary>A down arrow landing in an open tray.</summary>
    private static Image DrawUpdateAll(int side)
    {
        (Bitmap image, Graphics g) = Canvas(side);
        float width = side * 0.12f;

        using (var pen = new Pen(IconColor, width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
            // Tray.
            g.DrawLines(pen, new[]
            {
                new PointF(side * 0.10f, side * 0.62f),
                new PointF(side * 0.10f, side * 0.88f),
                new PointF(side * 0.90f, side * 0.88f),
                new PointF(side * 0.90f, side * 0.62f)
            });

            // Shaft.
            g.DrawLine(pen, side * 0.5f, side * 0.08f, side * 0.5f, side * 0.56f);
        }

        using (var brush = new SolidBrush(IconColor))
        {
            g.FillPolygon(brush, new[]
            {
                new PointF(side * 0.26f, side * 0.44f),
                new PointF(side * 0.74f, side * 0.44f),
                new PointF(side * 0.50f, side * 0.72f)
            });
        }

        g.Dispose();
        return image;
    }

    /// <summary>Arrowhead at the end of a clockwise arc, pointing along it.</summary>
    private static void DrawArrowHead(Graphics g, float center, float radius, float angleDegrees, float size)
    {
        double theta = angleDegrees * Math.PI / 180;
        float px = center + (float)(radius * Math.Cos(theta));
        float py = center + (float)(radius * Math.Sin(theta));

        // Tangent of a clockwise arc (y grows downwards) and the radial direction.
        float tx = (float)-Math.Sin(theta);
        float ty = (float)Math.Cos(theta);
        float nx = (float)Math.Cos(theta);
        float ny = (float)Math.Sin(theta);

        using var brush = new SolidBrush(IconColor);
        g.FillPolygon(brush, new[]
        {
            new PointF(px + tx * size * 0.7f, py + ty * size * 0.7f),
            new PointF(px - tx * size * 0.25f + nx * size * 0.55f, py - ty * size * 0.25f + ny * size * 0.55f),
            new PointF(px - tx * size * 0.25f - nx * size * 0.55f, py - ty * size * 0.25f - ny * size * 0.55f)
        });
    }
}
