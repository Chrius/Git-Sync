using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace GitSync;

/// <summary>
/// First step of an import: the folders of the file too, or only its
/// repositories (whose folders the user then maps with <see cref="FolderMapForm"/>).
/// </summary>
internal sealed class ImportChoiceForm : Form
{
    private const int Edge = 16;

    private readonly RadioButton _both;
    private readonly RadioButton _reposOnly;
    private readonly Button _ok;

    internal bool ReposOnly => _reposOnly.Checked;

    internal ImportChoiceForm(string fileName, ExportData data, int newRepos, int newFolders)
    {
        Text = "Import Data";
        ShowIcon = false;
        ClientSize = new Size(480, 280);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 9F);

        int width = ClientSize.Width - (2 * Edge);

        Controls.Add(new Label
        {
            Text = fileName,
            AutoSize = false,
            AutoEllipsis = true,
            Location = new Point(Edge, 14),
            Size = new Size(width, 20),
            ForeColor = Theme.Text,
            UseMnemonic = false
        });

        Controls.Add(new Label
        {
            Text = $"{Count(data.Repos.Count, "repository", "repositories")} in {Count(data.Filters.Count, "folder", "folders")}. "
                 + $"New here: {Count(newRepos, "repository", "repositories")} and {Count(newFolders, "folder", "folders")}.",
            AutoSize = false,
            Location = new Point(Edge, 36),
            Size = new Size(width, 36),
            ForeColor = Theme.DimText
        });

        _both = new RadioButton
        {
            Text = "Folders and repositories",
            AutoSize = true,
            Checked = true,
            Location = new Point(Edge, 82),
            TabIndex = 0
        };
        Controls.Add(_both);
        Controls.Add(Hint("Missing folders are added, and each repository goes into the folder it was in.", 104));

        _reposOnly = new RadioButton
        {
            Text = "Repositories only",
            AutoSize = true,
            Location = new Point(Edge, 148),
            TabIndex = 1
        };
        Controls.Add(_reposOnly);
        Controls.Add(Hint("No folders are added: you choose which of your folders the repositories of each folder go into.", 170));

        _ok = new Button { Text = "Import", Size = new Size(96, 30), TabIndex = 10, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Size = new Size(96, 30), TabIndex = 11, DialogResult = DialogResult.Cancel };

        int buttonsTop = ClientSize.Height - Edge - 30;
        cancel.Location = new Point(ClientSize.Width - Edge - 96, buttonsTop);
        _ok.Location = new Point(cancel.Left - 8 - 96, buttonsTop);
        Controls.Add(_ok);
        Controls.Add(cancel);

        AcceptButton = _ok;
        CancelButton = cancel;

        // "Repositories only" opens a second window, so the button says there is one more step.
        _reposOnly.CheckedChanged += (_, _) => _ok.Text = _reposOnly.Checked ? "Next" : "Import";
    }

    private Label Hint(string text, int top) => new()
    {
        Text = text,
        AutoSize = false,
        Location = new Point(Edge + 18, top),
        Size = new Size(ClientSize.Width - (2 * Edge) - 18, 36),
        ForeColor = Theme.DimText
    };

    internal static string Count(int count, string one, string many) =>
        count.ToString(CultureInfo.InvariantCulture) + " " + (count == 1 ? one : many);
}

/// <summary>
/// "Repositories only": one line per folder of the file that holds
/// repositories to import, each with a drop down of this install's folders.
/// Every repository of that folder goes into the folder picked.
/// </summary>
internal sealed class FolderMapForm : Form
{
    private sealed record Target(long? Id, string Text)
    {
        public override string ToString() => Text;
    }

    private const int Edge = 16;
    private const int RowHeight = 34;
    private const int ListLeft = 280;

    private readonly List<(IReadOnlyList<string> Path, ComboBox List)> _rows = [];

    /// <summary>The local folder for each folder of the file, by <see cref="DataTransfer.Key"/>.</summary>
    internal Dictionary<string, long?> Destinations { get; } = [];

    internal FolderMapForm(List<(IReadOnlyList<string> Path, int NewRepos)> sources, IReadOnlyList<FilterFolder> filters)
    {
        Text = "Choose Folders";
        ShowIcon = false;
        ClientSize = new Size(580, 420);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 9F);

        int width = ClientSize.Width - (2 * Edge);

        Controls.Add(new Label
        {
            Text = "Choose which of your folders the repositories of each folder in the file go into.",
            AutoSize = false,
            Location = new Point(Edge, 14),
            Size = new Size(width, 20),
            ForeColor = Theme.Text
        });

        Controls.Add(new Label { Text = "In the file", AutoSize = true, Location = new Point(Edge, 46), ForeColor = Theme.DimText });
        Controls.Add(new Label { Text = "Goes into", AutoSize = true, Location = new Point(Edge + ListLeft, 46), ForeColor = Theme.DimText });

        var import = new Button { Text = "Import", Size = new Size(96, 30), TabIndex = 100 };
        var cancel = new Button { Text = "Cancel", Size = new Size(96, 30), TabIndex = 101, DialogResult = DialogResult.Cancel };

        int buttonsTop = ClientSize.Height - Edge - 30;
        cancel.Location = new Point(ClientSize.Width - Edge - 96, buttonsTop);
        import.Location = new Point(cancel.Left - 8 - 96, buttonsTop);

        // A long list scrolls inside its own panel; the buttons stay where they are.
        var rows = new Panel
        {
            Location = new Point(Edge, 70),
            Size = new Size(width, buttonsTop - 12 - 70),
            AutoScroll = true,
            BackColor = Theme.Background
        };
        DarkControls.UseDarkScrollbars(rows);
        Controls.Add(rows);

        var targets = new List<Target> { new(null, "(None)") };
        targets.AddRange(FilterStore.Flatten(filters).Select(f => new Target(f.Folder.Id, f.FullPath)));

        // Room for the scrollbar on the right, whether it shows or not.
        int listWidth = width - ListLeft - SystemInformation.VerticalScrollBarWidth - 4;

        for (int i = 0; i < sources.Count; i++)
        {
            (IReadOnlyList<string> path, int newRepos) = sources[i];
            int top = i * RowHeight;

            rows.Controls.Add(new Label
            {
                Text = path.Count == 0 ? "(No folder)" : string.Join("\\", path),
                AutoSize = false,
                AutoEllipsis = true,
                Location = new Point(0, top + 4),
                Size = new Size(ListLeft - 82, 20),
                ForeColor = path.Count == 0 ? Theme.DimText : Theme.Text,
                UseMnemonic = false
            });

            rows.Controls.Add(new Label
            {
                Text = ImportChoiceForm.Count(newRepos, "repo", "repos"),
                AutoSize = false,
                Location = new Point(ListLeft - 78, top + 4),
                Size = new Size(70, 20),
                ForeColor = Theme.DimText
            });

            ComboBox list = DarkControls.CreateList(i);
            list.Items.AddRange([.. targets]);
            list.SetBounds(ListLeft, top, listWidth, list.Height);

            // The folder of the same name here, when there is one; otherwise no folder.
            long? same = path.Count == 0 ? null : DataTransfer.FindFolder(path, filters);
            list.SelectedIndex = Math.Max(0, targets.FindIndex(t => t.Id == same));

            rows.Controls.Add(list);
            _rows.Add((path, list));
        }

        import.Click += (_, _) =>
        {
            foreach ((IReadOnlyList<string> path, ComboBox list) in _rows)
            {
                Destinations[DataTransfer.Key(path)] = (list.SelectedItem as Target)?.Id;
            }

            DialogResult = DialogResult.OK;
        };

        Controls.Add(import);
        Controls.Add(cancel);
        AcceptButton = import;
        CancelButton = cancel;
    }
}
