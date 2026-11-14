using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace GitSync;

/// <summary>
/// One line of the repo list: name and path on the left, the Latest / Current
/// / Selected columns on the right, and the Update or Download button.
///
/// Text is drawn with TextRenderer instead of Labels: an AutoSize Label keeps
/// a few pixels of padding around its text, which breaks the alignment of the
/// columns from one row to the next.
/// </summary>
internal sealed class RepoRow : Control
{
    private static readonly Font NameFont = new("Segoe UI Semibold", 12f);
    private static readonly Font SmallFont = new("Segoe UI", 9f);
    private static readonly Font PillFont = new("Segoe UI", 6.75f, FontStyle.Bold);

    private const TextFormatFlags LineFlags =
        TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPrefix |
        TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;

    private const string PillText = "SOURCE";

    private readonly FlatButton _action;
    private readonly ToolTip _tips;
    private readonly List<(Rectangle Area, string Text)> _tipAreas = [];
    private string _shownTip = string.Empty;
    private bool _hover;
    private bool _showSelected;
    private DownloadProgress? _progress;

    internal Repo Repo { get; private set; }

    internal event EventHandler? ActionClicked;
    internal event EventHandler? EditRequested;

    /// <summary>Screen point of a right click.</summary>
    internal event EventHandler<Point>? MenuRequested;

    private int Scaled(int value) => LogicalToDeviceUnits(value);
    private int PadLeft => Scaled(16);
    private int PadRight => Scaled(14);
    private int ButtonWidth => Scaled(96);
    private int ButtonHeight => Scaled(30);
    private int ColumnWidth => Scaled(150);
    private int ColumnGap => Scaled(14);

    internal RepoRow(Repo repo, ToolTip tips)
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);

        Repo = repo;
        _tips = tips;

        // The button comes before the height: setting Height lays the row
        // out, and the layout positions the button.
        _action = FlatButton.Accent("Update");
        _action.Size = new Size(ButtonWidth, ButtonHeight);
        _action.Click += (_, _) => ActionClicked?.Invoke(this, EventArgs.Empty);

        // Moving onto the button leaves the row as far as Windows is concerned;
        // without this the row's highlight would blink off under the button.
        _action.MouseEnter += (_, _) => SetHover(true);

        Controls.Add(_action);

        Height = Scaled(62);
        BackColor = Theme.Background;
        UpdateAction();
    }

    /// <summary>
    /// Whether the Selected column is drawn. The list decides it for all rows
    /// at once, so the columns line up from one row to the next.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool ShowSelectedColumn
    {
        get => _showSelected;
        set
        {
            if (_showSelected != value)
            {
                _showSelected = value;
                Invalidate();
            }
        }
    }

    internal void SetRepo(Repo repo)
    {
        Repo = repo;
        UpdateAction();
        Invalidate();
    }

    /// <summary>Download progress, or null when idle.</summary>
    internal void SetProgress(DownloadProgress? progress)
    {
        _progress = progress;
        UpdateAction();
    }

    private void UpdateAction()
    {
        if (_progress is { } progress)
        {
            _action.Visible = true;
            _action.Enabled = false;
            _action.Progress = progress.Fraction ?? 0;
            _action.Text = progress.Fraction is { } fraction && fraction > 0
                ? string.Create(CultureInfo.InvariantCulture, $"{(int)(fraction * 100)}%")
                : progress.Bytes > 0
                    ? FileChecklist.FormatSize(progress.Bytes)
                    : "Starting…";
            return;
        }

        RepoStatus status = VersionState.StatusOf(Repo);

        _action.Progress = null;
        _action.Enabled = true;
        _action.Text = status == RepoStatus.Download ? "Download" : "Update";
        _action.Visible = status is RepoStatus.Update or RepoStatus.Download;
    }

    // ---------- layout ----------

    private int ButtonLeft => Width - PadRight - ButtonWidth;

    private int ColumnCount => _showSelected ? 3 : 2;

    /// <summary>Columns sit right before the button; the left text gets whatever remains.</summary>
    private int ColumnLeft(int index)
    {
        int total = (ColumnCount * ColumnWidth) + ((ColumnCount - 1) * ColumnGap);
        int first = ButtonLeft - ColumnGap - total;
        return first + (index * (ColumnWidth + ColumnGap));
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        _action.SetBounds(ButtonLeft, (Height - ButtonHeight) / 2, ButtonWidth, ButtonHeight);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        PerformLayout();
    }

    // ---------- painting ----------

    private static int LineHeight(Font font) =>
        TextRenderer.MeasureText("Ag", font, Size.Empty, LineFlags).Height;

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(_hover ? Theme.Hover : Theme.Background);

        using (var separator = new Pen(Theme.Separator))
        {
            g.DrawLine(separator, 0, Height - 1, Width, Height - 1);
        }

        _tipAreas.Clear();

        int nameHeight = LineHeight(NameFont);
        int smallHeight = LineHeight(SmallFont);

        // ----- left: name, path, red X -----

        int left = PadLeft;
        int leftWidth = Math.Max(0, ColumnLeft(0) - ColumnGap - left);
        int top = (Height - (nameHeight + Scaled(3) + smallHeight)) / 2;

        DrawLine(g, Repo.Name, NameFont, Theme.Text, new Rectangle(left, top, leftWidth, nameHeight));

        int pathTop = top + nameHeight + Scaled(3);
        int markSize = Scaled(9);
        int pathWidth = DrawLine(g, Repo.Path, SmallFont, Theme.DimText,
            new Rectangle(left, pathTop, Math.Max(0, leftWidth - markSize - Scaled(8)), smallHeight));

        if (Repo.Error.Length > 0)
        {
            var mark = new Rectangle(
                left + pathWidth + Scaled(7), pathTop + ((smallHeight - markSize) / 2) + 1, markSize, markSize);

            DrawErrorMark(g, mark);
            _tipAreas.Add((Rectangle.Inflate(mark, Scaled(5), Scaled(5)), Repo.Error));
        }

        // ----- right: version columns -----

        int columnsTop = (Height - ((2 * smallHeight) + Scaled(4))) / 2;

        RepoStatus status = VersionState.StatusOf(Repo);
        // A repo that has never answered has no known Latest: "No releases" would be a guess.
        string latestEmpty = status == RepoStatus.NeverChecked ? "Not checked"
            : Repo.Error.Length > 0 ? "—"
            : "No releases";

        DrawColumn(g, 0, "Latest", Repo.Latest, latestEmpty, columnsTop, smallHeight);
        DrawColumn(g, 1, "Current", Repo.Current, "—", columnsTop, smallHeight);

        // The column's space is kept on every row so the columns line up, but
        // only a pinned row says "Selected": on the others the label is noise.
        if (_showSelected && Repo.Selected is not null)
        {
            DrawColumn(g, 2, "Selected", Repo.Selected, string.Empty, columnsTop, smallHeight);
        }
    }

    /// <summary>
    /// Draws one line clipped to the rectangle, with "…" when it does not fit
    /// (and then a tooltip with the whole text). Returns the width used.
    /// </summary>
    private int DrawLine(Graphics g, string text, Font font, Color color, Rectangle area)
    {
        if (area.Width <= 0 || text.Length == 0)
        {
            return 0;
        }

        int width = TextRenderer.MeasureText(g, text, font, Size.Empty, LineFlags).Width;

        if (width <= area.Width)
        {
            TextRenderer.DrawText(g, text, font, area.Location, color, LineFlags);
            return width;
        }

        TextRenderer.DrawText(g, text, font, area, color, LineFlags | TextFormatFlags.EndEllipsis);
        _tipAreas.Add((area, text));
        return area.Width;
    }

    /// <summary>"Latest  2026-11-10" in dim text, then the tag with the SOURCE pill when it is source code.</summary>
    private void DrawColumn(Graphics g, int index, string label, VersionRef? version, string empty, int top, int lineHeight)
    {
        int x = ColumnLeft(index);
        int width = ColumnWidth;

        string head = version?.Date is { } date
            ? $"{label}  {date.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}"
            : label;

        DrawLine(g, head, SmallFont, Theme.DimText, new Rectangle(x, top, width, lineHeight));

        int valueTop = top + lineHeight + Scaled(4);

        if (version is null)
        {
            DrawLine(g, empty, SmallFont, Theme.DimText, new Rectangle(x, valueTop, width, lineHeight));
            return;
        }

        Size pill = version.IsSource ? PillSize(g) : Size.Empty;
        int pillGap = Scaled(6);
        int tagSpace = version.IsSource ? width - pill.Width - pillGap : width;

        int tagWidth = DrawLine(g, version.DisplayTag, SmallFont, Theme.Text, new Rectangle(x, valueTop, tagSpace, lineHeight));

        if (version.IsSource)
        {
            var area = new Rectangle(x + tagWidth + pillGap, valueTop + ((lineHeight - pill.Height) / 2) + 1, pill.Width, pill.Height);
            DrawPill(g, area);
            _tipAreas.Add((area, version.Kind == VersionKind.Commit
                ? "Source code of the newest commit: this repository has no releases."
                : "Source code: this release has no files of its own."));
        }
    }

    private Size PillSize(Graphics g)
    {
        Size text = TextRenderer.MeasureText(g, PillText, PillFont, Size.Empty, LineFlags);
        return new Size(text.Width + Scaled(8), Scaled(14));
    }

    /// <summary>Amber "SOURCE" tag: says the version is source code, not release files.</summary>
    private static void DrawPill(Graphics g, Rectangle area)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (GraphicsPath shape = DarkControls.RoundedRectangle(
            new RectangleF(area.X, area.Y, area.Width - 1, area.Height - 1), 3))
        using (var fill = new SolidBrush(Theme.AmberFill))
        using (var border = new Pen(Theme.Amber))
        {
            g.FillPath(fill, shape);
            g.DrawPath(border, shape);
        }

        TextRenderer.DrawText(g, PillText, PillFont, area, Theme.Amber,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix |
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);

        g.SmoothingMode = SmoothingMode.None;
    }

    /// <summary>The red X after the path: the last check got no answer from the site.</summary>
    private static void DrawErrorMark(Graphics g, Rectangle area)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var pen = new Pen(Theme.Error, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(pen, area.Left, area.Top, area.Right, area.Bottom);
        g.DrawLine(pen, area.Right, area.Top, area.Left, area.Bottom);

        g.SmoothingMode = SmoothingMode.None;
    }

    // ---------- mouse ----------

    private void SetHover(bool hover)
    {
        if (_hover != hover)
        {
            _hover = hover;
            Invalidate();
        }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        SetHover(true);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);

        // Still inside when the pointer only moved onto the button.
        SetHover(ClientRectangle.Contains(PointToClient(Cursor.Position)));
        ShowTip(string.Empty);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        string tip = _tipAreas.FirstOrDefault(a => a.Area.Contains(e.Location)).Text ?? string.Empty;
        ShowTip(tip);
    }

    private void ShowTip(string text)
    {
        if (text != _shownTip)
        {
            _shownTip = text;
            _tips.SetToolTip(this, text.Length > 0 ? text : null);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        if (e.Button == MouseButtons.Right)
        {
            MenuRequested?.Invoke(this, PointToScreen(e.Location));
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);

        if (e.Button == MouseButtons.Left)
        {
            EditRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
