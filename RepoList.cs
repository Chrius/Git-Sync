using System.Drawing;
using System.Windows.Forms;

namespace GitSync;

/// <summary>Scrollable stack of <see cref="RepoRow"/>, each taking the whole width.</summary>
internal sealed class RepoList : Panel
{
    private readonly Dictionary<long, RepoRow> _rows = [];
    private readonly ToolTip _tips = new() { InitialDelay = 300, ReshowDelay = 100, AutoPopDelay = 15000 };
    private string _emptyText = string.Empty;

    internal event EventHandler<Repo>? ActionClicked;
    internal event EventHandler<Repo>? EditRequested;
    internal event EventHandler<(Repo Repo, Point ScreenPoint)>? MenuRequested;

    internal RepoList()
    {
        AutoScroll = true;
        BackColor = Theme.Background;
        DoubleBuffered = true;
        DarkControls.UseDarkScrollbars(this);

        // Takes the focus when clicked, as a list box does. A panel never
        // takes it by itself, so after typing in the search box a click on the
        // list left the caret there, and Ctrl+V pasted the copied link into
        // the search instead of adding it. Only another control that takes
        // the focus, such as the tree, got it out.
        SetStyle(ControlStyles.Selectable, true);
    }

    internal RepoRow? RowOf(long id) => _rows.GetValueOrDefault(id);

    /// <summary>
    /// Replaces the rows. Downloads in progress keep showing their percentage,
    /// because the rows are rebuilt whenever the filter changes.
    /// </summary>
    internal void ShowRepos(IReadOnlyList<Repo> repos, IReadOnlyDictionary<long, DownloadProgress> progress, string emptyText, bool keepScroll)
    {
        int scroll = keepScroll ? -AutoScrollPosition.Y : 0;
        _emptyText = emptyText;

        SuspendLayout();

        List<Control> old = [.. Controls.Cast<Control>()];
        Controls.Clear();

        foreach (Control control in old)
        {
            control.Dispose();
        }

        _rows.Clear();

        bool pinned = repos.Any(r => r.Selected is not null);

        // Docked to the top, the last one added ends up first: added backwards.
        for (int i = repos.Count - 1; i >= 0; i--)
        {
            Repo repo = repos[i];
            var row = new RepoRow(repo, _tips) { Dock = DockStyle.Top, ShowSelectedColumn = pinned };

            if (progress.TryGetValue(repo.Id, out DownloadProgress value))
            {
                row.SetProgress(value);
            }

            row.ActionClicked += (_, _) => ActionClicked?.Invoke(this, row.Repo);
            row.EditRequested += (_, _) => EditRequested?.Invoke(this, row.Repo);
            row.MenuRequested += (_, point) => MenuRequested?.Invoke(this, (row.Repo, point));

            // The rows themselves do not take the focus: the list does, so it
            // never moves to a row that the next rebuild throws away.
            row.MouseDown += (_, _) => Focus();

            Controls.Add(row);
            _rows[repo.Id] = row;
        }

        ResumeLayout(performLayout: true);

        AutoScrollPosition = new Point(0, scroll);
        Invalidate();
    }

    /// <summary>Redraws one row after its data changed (check, download, pin).</summary>
    internal void UpdateRepo(Repo repo)
    {
        if (!_rows.TryGetValue(repo.Id, out RepoRow? row))
        {
            return;
        }

        row.SetRepo(repo);

        bool pinned = _rows.Values.Any(r => r.Repo.Selected is not null);

        foreach (RepoRow each in _rows.Values)
        {
            each.ShowSelectedColumn = pinned;
        }
    }

    /// <summary>The empty space below the rows, or the "No repositories" text.</summary>
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        if (_rows.Count == 0 && _emptyText.Length > 0)
        {
            TextRenderer.DrawText(e.Graphics, _emptyText, Font, ClientRectangle, Theme.DimText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        }
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);

        // The empty-list message is centered on the whole area.
        if (_rows.Count == 0)
        {
            Invalidate();
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
