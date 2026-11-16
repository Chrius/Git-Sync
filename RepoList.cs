using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GitSync;

/// <summary>
/// The repo list: one <see cref="RepoRow"/> per repo, each taking the whole width.
///
/// Only the lines on screen have a row. A handful of RepoRow controls are
/// moved and handed other repos as the list scrolls, and the scroll bar is the
/// list's own, worked by hand. With one control per repo, as before, 1,368
/// repos broke the list: Windows places a child window at most 32,767 pixels
/// down, so every row past about the 528th piled up on that line (the list
/// ended at "I", then showed only the last repo), and creating some 2,700
/// windows, rows and their buttons, took seconds at every change of folder.
/// </summary>
internal sealed class RepoList : Control
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ScrollInfo
    {
        public int Size;
        public int Mask;
        public int Min;
        public int Max;
        public int Page;
        public int Pos;
        public int TrackPos;
    }

    [DllImport("user32.dll")]
    private static extern int SetScrollInfo(IntPtr window, int bar, ref ScrollInfo info, bool redraw);

    [DllImport("user32.dll")]
    private static extern bool GetScrollInfo(IntPtr window, int bar, ref ScrollInfo info);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool RedrawWindow(IntPtr window, IntPtr rect, IntPtr region, int flags);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point point);

    private const int WS_VSCROLL = 0x00200000;
    private const int WM_SETREDRAW = 0x000B;
    private const int WM_VSCROLL = 0x0115;
    private const int SB_VERT = 1;
    private const int SIF_RANGE = 0x1;
    private const int SIF_PAGE = 0x2;
    private const int SIF_POS = 0x4;
    private const int SIF_TRACKPOS = 0x10;
    private const int RDW_INVALIDATE = 0x1;
    private const int RDW_ERASE = 0x4;
    private const int RDW_ALLCHILDREN = 0x80;
    private const int RDW_UPDATENOW = 0x100;
    private const int RDW_FRAME = 0x400;

    private readonly List<RepoRow> _rows = [];
    private readonly ToolTip _tips = new() { InitialDelay = 300, ReshowDelay = 100, AutoPopDelay = 15000 };
    private List<Repo> _repos = [];
    private IReadOnlyDictionary<long, DownloadProgress> _progress = new Dictionary<long, DownloadProgress>();
    private string _emptyText = string.Empty;
    private bool _pinned;

    /// <summary>Pixels scrolled from the top.</summary>
    private int _offset;

    /// <summary>
    /// Set while the rows are laid out: showing or hiding the scroll bar
    /// resizes the list in the middle of it, and the nested layout that would
    /// start then is left to the one already running.
    /// </summary>
    private bool _laying;

    internal event EventHandler<Repo>? ActionClicked;
    internal event EventHandler<Repo>? EditRequested;
    internal event EventHandler<(Repo Repo, Point ScreenPoint)>? MenuRequested;

    internal RepoList()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);

        BackColor = Theme.Background;
        TabStop = false;
        DarkControls.UseDarkScrollbars(this);

        // Takes the focus when clicked, as a list box does. A panel never
        // takes it by itself, so after typing in the search box a click on the
        // list left the caret there, and Ctrl+V pasted the copied link into
        // the search instead of adding it. Only another control that takes
        // the focus, such as the tree, got it out.
        SetStyle(ControlStyles.Selectable, true);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.Style |= WS_VSCROLL;
            return parameters;
        }
    }

    private int LineHeight => LogicalToDeviceUnits(RepoRow.BaseHeight);

    /// <summary>The row showing this repo, when it is on screen.</summary>
    internal RepoRow? RowOf(long id) => _rows.FirstOrDefault(r => r.Visible && r.Repo.Id == id);

    /// <summary>
    /// Replaces the list. Downloads in progress keep showing their percentage:
    /// <paramref name="progress"/> is the main window's own table, read again
    /// whenever a row is handed a repo.
    /// </summary>
    internal void ShowRepos(IReadOnlyList<Repo> repos, IReadOnlyDictionary<long, DownloadProgress> progress, string emptyText, bool keepScroll)
    {
        _repos = [.. repos];
        _progress = progress;
        _emptyText = emptyText;
        _pinned = _repos.Any(r => r.Selected is not null);

        if (!keepScroll)
        {
            _offset = 0;
        }

        LayoutRows(rebind: true);
    }

    /// <summary>Takes one repo's new data (check, download, pin) and redraws its row if it is on screen.</summary>
    internal void UpdateRepo(Repo repo)
    {
        int index = _repos.FindIndex(r => r.Id == repo.Id);

        if (index < 0)
        {
            return;
        }

        _repos[index] = repo;
        _pinned = _repos.Any(r => r.Selected is not null);

        foreach (RepoRow row in _rows)
        {
            row.ShowSelectedColumn = _pinned;

            if (row.Visible && row.Repo.Id == repo.Id)
            {
                row.SetRepo(repo);
            }
        }
    }

    // ---------- layout ----------

    /// <summary>Places the rows for the current scroll position and hands each the repo of its line.</summary>
    /// <param name="rebind">Every row takes its repo again, even the same one: the list was replaced.</param>
    private void LayoutRows(bool rebind = false)
    {
        if (_laying || !IsHandleCreated)
        {
            return;
        }

        _laying = true;

        try
        {
            int lineHeight = LineHeight;
            _offset = ListWindow.Clamp(_offset, _repos.Count, lineHeight, ClientSize.Height);

            // First: it may show or hide the scroll bar, which changes the width.
            UpdateScrollBar(lineHeight);

            int width = ClientSize.Width;
            int needed = ListWindow.RowsNeeded(lineHeight, ClientSize.Height);
            (int first, int shift) = ListWindow.FirstVisible(_offset, lineHeight);

            // Moving a dozen rows one by one would draw each in between.
            SendMessage(Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);

            try
            {
                while (_rows.Count < needed)
                {
                    AddRow();
                }

                for (int i = 0; i < _rows.Count; i++)
                {
                    RepoRow row = _rows[i];
                    int index = first + i;

                    if (i >= needed || index >= _repos.Count)
                    {
                        row.Visible = false;
                        continue;
                    }

                    Repo repo = _repos[index];
                    DownloadProgress? progress = _progress.TryGetValue(repo.Id, out DownloadProgress value) ? value : null;

                    if (rebind || !ReferenceEquals(row.Repo, repo) || row.Progress != progress)
                    {
                        row.Bind(repo, progress);
                    }

                    row.ShowSelectedColumn = _pinned;
                    row.SetBounds(0, (i * lineHeight) - shift, width, lineHeight);
                    row.Visible = true;
                }
            }
            finally
            {
                SendMessage(Handle, WM_SETREDRAW, new IntPtr(1), IntPtr.Zero);
            }

            IntPtr underPointer = WindowFromPoint(Cursor.Position);

            foreach (RepoRow row in _rows)
            {
                row.SyncHover(underPointer);
            }

            RedrawWindow(Handle, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN | RDW_FRAME | RDW_UPDATENOW);
        }
        finally
        {
            _laying = false;
        }
    }

    private void AddRow()
    {
        var row = new RepoRow(new Repo(), _tips) { Visible = false };

        // Each row reports the repo it shows at the moment of the click.
        row.ActionClicked += (_, _) => ActionClicked?.Invoke(this, row.Repo);
        row.EditRequested += (_, _) => EditRequested?.Invoke(this, row.Repo);
        row.MenuRequested += (_, point) => MenuRequested?.Invoke(this, (row.Repo, point));

        // The rows themselves do not take the focus: the list does.
        row.MouseDown += (_, _) => Focus();

        Controls.Add(row);
        _rows.Add(row);
    }

    // ---------- scrolling ----------

    /// <summary>
    /// The range is in pixels and can pass 65,535 (1,368 repos make 84,816):
    /// SetScrollInfo and the 32-bit track position take that, unlike the
    /// 16-bit position that WM_VSCROLL carries.
    /// </summary>
    private void UpdateScrollBar(int lineHeight)
    {
        var info = new ScrollInfo
        {
            Size = Marshal.SizeOf<ScrollInfo>(),
            Mask = SIF_RANGE | SIF_PAGE | SIF_POS,
            Min = 0,
            Max = (int)Math.Clamp(((long)_repos.Count * lineHeight) - 1, 0, int.MaxValue),
            Page = Math.Max(0, ClientSize.Height),
            Pos = _offset
        };

        // A range no bigger than the page hides the bar, as AutoScroll did.
        SetScrollInfo(Handle, SB_VERT, ref info, true);
    }

    private void ScrollTo(int offset)
    {
        int clamped = ListWindow.Clamp(offset, _repos.Count, LineHeight, ClientSize.Height);

        if (clamped != _offset)
        {
            _offset = clamped;
            LayoutRows();
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_VSCROLL)
        {
            OnScrollBar((int)(m.WParam.ToInt64() & 0xFFFF));
            return;
        }

        base.WndProc(ref m);
    }

    private void OnScrollBar(int request)
    {
        int line = LineHeight;
        int page = Math.Max(line, ClientSize.Height);

        switch (request)
        {
            case 0: // SB_LINEUP
                ScrollTo(_offset - line);
                break;
            case 1: // SB_LINEDOWN
                ScrollTo(_offset + line);
                break;
            case 2: // SB_PAGEUP
                ScrollTo(_offset - page);
                break;
            case 3: // SB_PAGEDOWN
                ScrollTo(_offset + page);
                break;
            case 4: // SB_THUMBPOSITION
            case 5: // SB_THUMBTRACK
                var info = new ScrollInfo { Size = Marshal.SizeOf<ScrollInfo>(), Mask = SIF_TRACKPOS };

                if (GetScrollInfo(Handle, SB_VERT, ref info))
                {
                    ScrollTo(info.TrackPos);
                }

                break;
            case 6: // SB_TOP
                ScrollTo(0);
                break;
            case 7: // SB_BOTTOM
                ScrollTo(int.MaxValue);
                break;
        }
    }

    /// <summary>120 pixels a notch, as the panel the list used to be scrolled.</summary>
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        ScrollTo(_offset - e.Delta);

        if (e is HandledMouseEventArgs handled)
        {
            handled.Handled = true;
        }
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End
        || base.IsInputKey(keyData);

    /// <summary>With the list focused, the keys scroll it, as in a list box.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        int? target = e.KeyCode switch
        {
            Keys.Up => _offset - LineHeight,
            Keys.Down => _offset + LineHeight,
            Keys.PageUp => _offset - ClientSize.Height,
            Keys.PageDown => _offset + ClientSize.Height,
            Keys.Home => 0,
            Keys.End => int.MaxValue,
            _ => null
        };

        if (target is { } offset && e.Modifiers == Keys.None)
        {
            ScrollTo(offset);
            e.Handled = true;
        }
    }

    // ---------- the rest ----------

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        LayoutRows(rebind: true);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutRows();
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

        if (_repos.Count == 0 && _emptyText.Length > 0)
        {
            TextRenderer.DrawText(e.Graphics, _emptyText, Font, ClientRectangle, Theme.DimText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
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
