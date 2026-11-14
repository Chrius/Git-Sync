using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GitSync;

/// <summary>
/// The folder tree of filters, used by the main window and by the Filters tab.
///
/// Every row is painted here (OwnerDrawAll): the + / − box, a folder icon and
/// the name. The themed tree draws arrows instead of + / −, and its glyphs and
/// rename box stay light in dark mode. Clicks on the box are caught before the
/// native control sees them, so opening a folder does not also select it.
/// </summary>
internal sealed class FilterTree : TreeView
{
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonDblClk = 0x0203;
    private const int TvmSetExtendedStyle = 0x1100 + 44;
    private static readonly IntPtr TvsExDoubleBuffer = 0x0004;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    private TreeNode? _hover;
    private TreeNode? _dropTarget;
    private bool _loading;

    private Panel? _renameFrame;
    private TextBox? _renameBox;
    private TreeNode? _renameNode;
    private Action<TreeNode, string>? _renameCommit;

    /// <summary>Whether the fixed "All" node goes on top (main window) or not (Settings).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool IncludeAll { get; init; }

    /// <summary>Raised when the user opens or closes a folder, to save the state.</summary>
    internal event Action<FilterFolder, bool>? ExpandedChanged;

    private int Scaled(int value) => LogicalToDeviceUnits(value);
    private int RowHeight => Scaled(24);
    private int IndentWidth => Scaled(18);
    private int LeftPad => Scaled(8);
    private int BoxSize => Scaled(9);
    private int BoxSlot => Scaled(15);
    private int IconWidth => Scaled(16);
    private int IconHeight => Scaled(13);
    private int IconGap => Scaled(6);

    internal FilterTree()
    {
        DrawMode = TreeViewDrawMode.OwnerDrawAll;
        BorderStyle = BorderStyle.None;
        BackColor = Theme.Surface;
        ForeColor = Theme.Text;
        ShowLines = false;
        ShowPlusMinus = false;
        ShowRootLines = false;
        FullRowSelect = true;
        HideSelection = false;
        HotTracking = false;
        ItemHeight = RowHeight;
        Indent = IndentWidth;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        // Without the native double buffer every hover repaint flickers.
        SendMessage(Handle, TvmSetExtendedStyle, TvsExDoubleBuffer, TvsExDoubleBuffer);
        DarkControls.UseDarkScrollbars(this);
    }

    // ---------- content ----------

    internal static FilterFolder? FolderOf(TreeNode? node) => node?.Tag as FilterFolder;

    /// <summary>
    /// Rebuilds the tree from the list, restoring each folder's saved
    /// open/closed state, and selects <paramref name="selectId"/> (or "All").
    /// </summary>
    internal void Load(IReadOnlyList<FilterFolder> filters, long? selectId)
    {
        EndRename(commit: false);

        _loading = true;
        BeginUpdate();

        Nodes.Clear();
        _hover = null;
        _dropTarget = null;

        if (IncludeAll)
        {
            Nodes.Add(new TreeNode("All") { Tag = null });
        }

        AddChildren(Nodes, null, filters);

        TreeNode? selected = selectId is { } id ? FindNode(id) : null;
        SelectedNode = selected ?? (IncludeAll ? Nodes[0] : null);
        SelectedNode?.EnsureVisible();

        EndUpdate();
        _loading = false;
    }

    private static void AddChildren(TreeNodeCollection target, long? parentId, IReadOnlyList<FilterFolder> filters)
    {
        foreach (FilterFolder folder in FilterStore.Children(parentId, filters))
        {
            var node = new TreeNode(folder.Name)
            {
                Tag = folder,
                Name = folder.Id.ToString(CultureInfo.InvariantCulture)
            };

            target.Add(node);
            AddChildren(node.Nodes, folder.Id, filters);

            if (folder.Expanded)
            {
                node.Expand();
            }
        }
    }

    internal TreeNode? FindNode(long id) =>
        Nodes.Find(id.ToString(CultureInfo.InvariantCulture), searchAllChildren: true).FirstOrDefault();

    protected override void OnAfterExpand(TreeViewEventArgs e)
    {
        base.OnAfterExpand(e);
        ReportExpanded(e.Node, true);
    }

    protected override void OnAfterCollapse(TreeViewEventArgs e)
    {
        base.OnAfterCollapse(e);
        ReportExpanded(e.Node, false);
    }

    private void ReportExpanded(TreeNode? node, bool expanded)
    {
        if (!_loading && FolderOf(node) is { } folder && folder.Expanded != expanded)
        {
            folder.Expanded = expanded;
            ExpandedChanged?.Invoke(folder, expanded);
        }
    }

    /// <summary>Highlight of the folder a drag would drop into (Filters tab).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal TreeNode? DropTarget
    {
        get => _dropTarget;
        set
        {
            if (_dropTarget != value)
            {
                _dropTarget = value;
                Invalidate();
            }
        }
    }

    // ---------- painting ----------

    private Rectangle BoxBounds(TreeNode node)
    {
        int x = LeftPad + (node.Level * IndentWidth);
        int middle = node.Bounds.Y + (ItemHeight / 2);
        return new Rectangle(x + ((BoxSlot - BoxSize) / 2), middle - (BoxSize / 2), BoxSize, BoxSize);
    }

    private Rectangle TextBounds(TreeNode node)
    {
        int x = LeftPad + (node.Level * IndentWidth) + BoxSlot + IconWidth + IconGap;
        return new Rectangle(x, node.Bounds.Y, Math.Max(10, ClientSize.Width - x - Scaled(4)), ItemHeight);
    }

    protected override void OnDrawNode(DrawTreeNodeEventArgs e)
    {
        // The native control also asks for rows that are not on screen, with
        // empty bounds; drawing those would paint at the top of the control.
        if (e.Node is not { } node || e.Bounds.Height <= 0 || e.Bounds.Width <= 0)
        {
            return;
        }

        Graphics g = e.Graphics;
        var row = new Rectangle(0, e.Bounds.Y, ClientSize.Width, e.Bounds.Height);

        Color back = node == _dropTarget ? Theme.Accent
            : node.IsSelected ? Theme.Selection
            : node == _hover ? Theme.Hover
            : BackColor;

        using (var brush = new SolidBrush(back))
        {
            g.FillRectangle(brush, row);
        }

        g.SmoothingMode = SmoothingMode.AntiAlias;

        int x = LeftPad + (node.Level * IndentWidth);
        int middle = row.Y + (row.Height / 2);

        if (node.Nodes.Count > 0)
        {
            DrawBox(g, BoxBounds(node), node.IsExpanded);
        }

        var icon = new Rectangle(x + BoxSlot, middle - (IconHeight / 2), IconWidth, IconHeight);

        if (node.Tag is null)
        {
            DrawAllIcon(g, icon);
        }
        else
        {
            DrawFolder(g, icon, open: node.IsExpanded && node.Nodes.Count > 0);
        }

        if (node != _renameNode)
        {
            TextRenderer.DrawText(
                g, node.Text, Font, TextBounds(node), Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }
    }

    /// <summary>The classic + / − box, in the dim text color so it does not compete with the names.</summary>
    private void DrawBox(Graphics g, Rectangle box, bool expanded)
    {
        g.SmoothingMode = SmoothingMode.None;

        using var pen = new Pen(Theme.DimText, 1f);
        g.DrawRectangle(pen, box.X, box.Y, box.Width - 1, box.Height - 1);

        int middleY = box.Y + ((box.Height - 1) / 2);
        int middleX = box.X + ((box.Width - 1) / 2);
        int inset = Math.Max(2, box.Width / 4);

        using var mark = new Pen(Theme.Text, 1f);
        g.DrawLine(mark, box.X + inset, middleY, box.Right - 1 - inset, middleY);

        if (!expanded)
        {
            g.DrawLine(mark, middleX, box.Y + inset, middleX, box.Bottom - 1 - inset);
        }

        g.SmoothingMode = SmoothingMode.AntiAlias;
    }

    private static readonly Color FolderColor = Color.FromArgb(214, 170, 82);
    private static readonly Color FolderDark = Color.FromArgb(160, 122, 52);

    /// <summary>A closed folder, or an open one with its front flap tilted.</summary>
    private static void DrawFolder(Graphics g, Rectangle r, bool open)
    {
        float tabWidth = r.Width * 0.42f;
        float tabHeight = r.Height * 0.22f;

        using var back = new SolidBrush(open ? FolderDark : FolderColor);
        using (GraphicsPath shape = new())
        {
            // Back of the folder with its tab.
            shape.AddLines(new[]
            {
                new PointF(r.X, r.Y + tabHeight),
                new PointF(r.X, r.Y + 1),
                new PointF(r.X + 1, r.Y),
                new PointF(r.X + tabWidth - 1, r.Y),
                new PointF(r.X + tabWidth + 1, r.Y + tabHeight),
                new PointF(r.Right, r.Y + tabHeight),
                new PointF(r.Right, r.Bottom),
                new PointF(r.X, r.Bottom)
            });
            shape.CloseFigure();
            g.FillPath(back, shape);
        }

        if (open)
        {
            using var front = new SolidBrush(FolderColor);
            g.FillPolygon(front, new[]
            {
                new PointF(r.X + (r.Width * 0.18f), r.Y + (r.Height * 0.42f)),
                new PointF(r.Right + 1, r.Y + (r.Height * 0.42f)),
                new PointF(r.Right - (r.Width * 0.14f), r.Bottom),
                new PointF(r.X, r.Bottom)
            });
        }
    }

    /// <summary>"All" is not a folder: three stacked lines, like a list.</summary>
    private static void DrawAllIcon(Graphics g, Rectangle r)
    {
        using var brush = new SolidBrush(Theme.DimText);
        float bar = Math.Max(2f, r.Height * 0.18f);

        for (int i = 0; i < 3; i++)
        {
            float y = r.Y + (i * (r.Height - bar) / 2f);
            using GraphicsPath line = DarkControls.RoundedRectangle(new RectangleF(r.X + 1, y, r.Width - 2, bar), bar / 2.2f);
            g.FillPath(brush, line);
        }
    }

    // ---------- mouse ----------

    protected override void WndProc(ref Message m)
    {
        if (m.Msg is WmLButtonDown or WmLButtonDblClk)
        {
            long packed = m.LParam.ToInt64();
            var point = new Point(unchecked((short)(packed & 0xFFFF)), unchecked((short)((packed >> 16) & 0xFFFF)));
            TreeNode? node = GetNodeAt(point);

            if (node is not null && node.Nodes.Count > 0 && Rectangle.Inflate(BoxBounds(node), 4, 4).Contains(point))
            {
                EndRename(commit: true);

                if (node.IsExpanded)
                {
                    node.Collapse();
                }
                else
                {
                    node.Expand();
                }

                // Swallowed: the native control would also select the folder.
                return;
            }
        }

        base.WndProc(ref m);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        SetHover(GetNodeAt(e.Location));
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        SetHover(null);
    }

    private void SetHover(TreeNode? node)
    {
        if (node == _hover)
        {
            return;
        }

        TreeNode? previous = _hover;
        _hover = node;

        if (previous?.TreeView == this)
        {
            Invalidate(new Rectangle(0, previous.Bounds.Y, ClientSize.Width, ItemHeight));
        }

        if (node is not null)
        {
            Invalidate(new Rectangle(0, node.Bounds.Y, ClientSize.Width, ItemHeight));
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        // The rename box does not scroll with the rows.
        EndRename(commit: true);
        base.OnMouseWheel(e);
    }

    // ---------- renaming in place ----------

    /// <summary>
    /// Opens a dark text box over the folder's name. The native label editor
    /// is not used: it draws a light box and positions itself by the native
    /// layout, not by what is painted here.
    /// </summary>
    internal void BeginRename(TreeNode node, Action<TreeNode, string> commit, string? initialText = null)
    {
        EndRename(commit: false);

        SelectedNode = node;
        node.EnsureVisible();

        _renameNode = node;
        _renameCommit = commit;

        (Panel frame, TextBox box) = DarkControls.CreateField(0);
        Rectangle text = TextBounds(node);
        frame.Padding = new Padding(3, Scaled(3), 3, 0);
        frame.SetBounds(text.X - 4, node.Bounds.Y, ClientSize.Width - text.X + 2, ItemHeight);

        box.Text = initialText ?? node.Text;
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.KeyCode is Keys.Enter or Keys.Escape)
            {
                e.IsInputKey = true;
            }
        };
        box.KeyDown += (_, e) =>
        {
            if (e.KeyCode is Keys.Enter or Keys.Escape)
            {
                e.SuppressKeyPress = true;
                e.Handled = true;
                EndRename(commit: e.KeyCode == Keys.Enter);
            }
        };
        box.LostFocus += (_, _) => EndRename(commit: true);

        _renameFrame = frame;
        _renameBox = box;

        Controls.Add(frame);
        Invalidate();
        box.Focus();
        box.SelectAll();
    }

    internal void EndRename(bool commit)
    {
        if (_renameFrame is not { } frame || _renameBox is not { } box || _renameNode is not { } node)
        {
            return;
        }

        string text = box.Text;
        Action<TreeNode, string>? callback = _renameCommit;

        // Cleared first: removing the box moves the focus, which fires
        // LostFocus and would come back in here.
        _renameFrame = null;
        _renameBox = null;
        _renameNode = null;
        _renameCommit = null;

        Controls.Remove(frame);
        frame.Dispose();
        Invalidate();

        if (commit && callback is not null)
        {
            callback(node, text);
        }
    }
}
