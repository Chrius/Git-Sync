using System.Drawing;
using System.Windows.Forms;

namespace GitSync;

/// <summary>
/// "Filters" tab: the folder tree the repos are sorted into. Every change is
/// saved immediately — a tree edited in several steps would be easy to lose
/// with a forgotten Save.
/// </summary>
internal sealed class FiltersTab : UserControl
{
    private const int ButtonWidth = 130;
    private const int ButtonHeight = 28;
    private const int Gap = 12;

    private readonly FilterTree _tree;
    private readonly Panel _treeFrame;
    private readonly Label _hint;
    private readonly List<Button> _buttons = [];
    private readonly Button _addSub;
    private readonly Button _rename;
    private readonly Button _delete;
    private readonly Button _up;
    private readonly Button _down;

    private List<FilterFolder> _filters = [];

    internal FiltersTab()
    {
        Dock = DockStyle.Fill;

        _tree = new FilterTree { Dock = DockStyle.Fill, AllowDrop = true, BackColor = Theme.Field };
        _tree.ExpandedChanged += (folder, expanded) => FilterStore.SetExpanded(folder.Id, expanded);
        _tree.AfterSelect += (_, _) => UpdateButtons();
        _tree.KeyDown += TreeKeyDown;
        _tree.ItemDrag += (_, e) =>
        {
            if (e.Item is TreeNode node)
            {
                _tree.EndRename(commit: true);
                _tree.DoDragDrop(node, DragDropEffects.Move);
            }
        };
        _tree.DragOver += DragOverTree;
        _tree.DragDrop += DropOnTree;
        _tree.DragLeave += (_, _) => _tree.DropTarget = null;

        _treeFrame = new Panel { BackColor = Theme.Field, Padding = new Padding(2, 4, 2, 4) };
        _treeFrame.Controls.Add(_tree);
        Controls.Add(_treeFrame);

        AddButton("Add Folder", (_, _) => AddFolder(subfolder: false));
        _addSub = AddButton("Add Subfolder", (_, _) => AddFolder(subfolder: true));
        _rename = AddButton("Rename", (_, _) => StartRename());
        _delete = AddButton("Delete", (_, _) => DeleteSelected());
        _up = AddButton("Move Up", (_, _) => Shift(-1));
        _down = AddButton("Move Down", (_, _) => Shift(1));

        _hint = new Label
        {
            Text = "Drag a folder onto another to move it inside. Drop it on the empty space below the folders to move it to the top level.",
            AutoSize = false,
            ForeColor = Theme.DimText
        };
        Controls.Add(_hint);

        Reload(null);
    }

    private Button AddButton(string text, EventHandler click)
    {
        var button = new Button { Text = text, Size = new Size(ButtonWidth, ButtonHeight) };
        button.Click += click;
        Controls.Add(button);
        _buttons.Add(button);
        return button;
    }

    /// <summary>
    /// Laid out from the real size. Anchor does not work here: the
    /// UserControl is born with its default size and only later stretched by
    /// the TabPage, so anchored margins would be computed from the wrong width.
    /// </summary>
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);

        int width = ClientSize.Width;
        int height = ClientSize.Height;

        if (width <= ButtonWidth + Gap || height <= 0)
        {
            return;
        }

        int hintHeight = 36;
        int treeWidth = width - ButtonWidth - Gap;

        _treeFrame.SetBounds(0, 0, treeWidth, height - hintHeight - 8);
        _hint.SetBounds(0, height - hintHeight, width, hintHeight);

        int y = 0;

        for (int i = 0; i < _buttons.Count; i++)
        {
            // A gap separates creating and deleting from reordering.
            if (_buttons[i] == _up)
            {
                y += 14;
            }

            _buttons[i].Location = new Point(width - ButtonWidth, y);
            y += ButtonHeight + 6;
        }
    }

    /// <summary>Picks up folders added by an import, keeping the selection.</summary>
    internal void ReloadFolders()
    {
        _tree.EndRename(commit: true);
        Reload(Selected?.Id);
    }

    private void Reload(long? selectId)
    {
        _filters = FilterStore.List();
        _tree.Load(_filters, selectId);
        UpdateButtons();
    }

    private FilterFolder? Selected => FilterTree.FolderOf(_tree.SelectedNode);

    private void UpdateButtons()
    {
        bool any = Selected is not null;
        _addSub.Enabled = any;
        _rename.Enabled = any;
        _delete.Enabled = any;
        _up.Enabled = any;
        _down.Enabled = any;
    }

    private void TreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F2)
        {
            StartRename();
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Delete)
        {
            DeleteSelected();
            e.Handled = true;
        }
    }

    // ---------- actions ----------

    /// <summary>Creates "New Folder" and opens it for renaming right away.</summary>
    private void AddFolder(bool subfolder)
    {
        long? parentId = subfolder ? Selected?.Id : null;

        if (subfolder && parentId is null)
        {
            Warn("Select the folder that will hold the new one.");
            return;
        }

        string name = FilterStore.UniqueName("New Folder", parentId, _filters);
        long id = FilterStore.Add(parentId, name);

        // A folder that gets a child is shown open, so the child is visible.
        if (parentId is { } parent)
        {
            FilterStore.SetExpanded(parent, true);
        }

        Reload(id);

        if (_tree.FindNode(id) is { } node)
        {
            _tree.BeginRename(node, CommitRename);
        }
    }

    private void StartRename()
    {
        if (_tree.SelectedNode is { } node && Selected is not null)
        {
            _tree.BeginRename(node, CommitRename);
        }
    }

    private void CommitRename(TreeNode node, string text)
    {
        if (FilterTree.FolderOf(node) is not { } folder)
        {
            return;
        }

        string name = text.Trim();

        if (name == folder.Name)
        {
            return;
        }

        if (FilterStore.NameProblem(name, folder.ParentId, folder.Id, _filters) is { } problem)
        {
            Warn(problem);

            // Back into the box with what was typed, so it can be fixed instead of retyped.
            BeginInvoke(() =>
            {
                if (_tree.FindNode(folder.Id) is { } again)
                {
                    _tree.BeginRename(again, CommitRename, text);
                }
            });
            return;
        }

        FilterStore.Rename(folder.Id, name);
        Reload(folder.Id);
    }

    private void DeleteSelected()
    {
        if (Selected is not { } folder)
        {
            return;
        }

        HashSet<long> subtree = FilterStore.SubtreeIds(folder.Id, _filters);
        int repos = FilterStore.CountRepos(subtree);

        if (repos > 0)
        {
            Warn($"{(repos == 1 ? "1 repository uses" : $"{repos} repositories use")} \"{folder.Name}\" or one of its subfolders.\n\n"
                 + "Move them to another folder first (Edit, on the main window).");
            return;
        }

        int subfolders = subtree.Count - 1;
        string question = subfolders == 0
            ? $"Delete the folder \"{folder.Name}\"?"
            : $"Delete the folder \"{folder.Name}\" and its {(subfolders == 1 ? "subfolder" : $"{subfolders} subfolders")}?";

        DialogResult answer = DarkMessageBox.Show(this, question, "Settings",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning,

            // "No" by default: a delete is never confirmed by an Enter pressed by mistake.
            MessageBoxDefaultButton.Button2);

        if (answer != DialogResult.Yes)
        {
            return;
        }

        FilterStore.Delete(folder.Id);
        Reload(folder.ParentId);
    }

    private void Shift(int direction)
    {
        if (Selected is { } folder)
        {
            FilterStore.Shift(folder.Id, direction);
            Reload(folder.Id);
        }
    }

    // ---------- drag and drop ----------

    /// <summary>The folder under the pointer (null for the empty space, meaning the top level).</summary>
    private TreeNode? NodeUnder(DragEventArgs e) => _tree.GetNodeAt(_tree.PointToClient(new Point(e.X, e.Y)));

    private static FilterFolder? Dragged(DragEventArgs e) =>
        FilterTree.FolderOf(e.Data?.GetData(typeof(TreeNode)) as TreeNode);

    private bool CanDrop(FilterFolder dragged, FilterFolder? target) =>
        target is null
            ? dragged.ParentId is not null
            : target.Id != dragged.ParentId && !FilterStore.SubtreeIds(dragged.Id, _filters).Contains(target.Id);

    private void DragOverTree(object? sender, DragEventArgs e)
    {
        if (Dragged(e) is not { } dragged)
        {
            e.Effect = DragDropEffects.None;
            return;
        }

        TreeNode? node = NodeUnder(e);
        bool allowed = CanDrop(dragged, FilterTree.FolderOf(node));

        e.Effect = allowed ? DragDropEffects.Move : DragDropEffects.None;
        _tree.DropTarget = allowed ? node : null;
    }

    private void DropOnTree(object? sender, DragEventArgs e)
    {
        _tree.DropTarget = null;

        if (Dragged(e) is not { } dragged)
        {
            return;
        }

        FilterFolder? target = FilterTree.FolderOf(NodeUnder(e));

        if (!CanDrop(dragged, target))
        {
            return;
        }

        // Two folders with the same name side by side would download into the same place.
        if (FilterStore.NameProblem(dragged.Name, target?.Id, dragged.Id, _filters) is { } problem)
        {
            Warn(problem);
            return;
        }

        try
        {
            FilterStore.MoveTo(dragged.Id, target?.Id);
        }
        catch (InvalidOperationException error)
        {
            Warn(error.Message);
            return;
        }

        if (target is not null)
        {
            FilterStore.SetExpanded(target.Id, true);
        }

        Reload(dragged.Id);
    }

    private void Warn(string message) =>
        DarkMessageBox.Show(this, message, "Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
}
