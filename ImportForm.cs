using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace GitSync;

/// <summary>
/// The import window: what the file holds, and one line per folder of the
/// file with repositories to import, each with where they go.
///
/// Every "Goes into" list starts with the file's own folder — marked
/// "(Create)" when it is not here yet — then "Do not import", then
/// "(No folder)" and the folders of this install. The first entry is the
/// default, so importing without touching anything rebuilds the file's
/// folders here.
/// </summary>
internal sealed class ImportForm : Form
{
    private sealed record Choice(ImportTarget Target, string Text)
    {
        public override string ToString() => Text;
    }

    private const int Edge = 16;
    private const int RowHeight = 34;
    private const int ListLeft = 280;
    private const int RowsTop = 120;

    private readonly List<(IReadOnlyList<string> Path, ComboBox List)> _rows = [];

    /// <summary>Where each folder of the file goes, by <see cref="DataTransfer.Key"/>.</summary>
    internal Dictionary<string, ImportTarget> Targets { get; } = [];

    internal ImportForm(
        string fileName, ExportData data, int newRepos,
        List<(IReadOnlyList<string> Path, int NewRepos)> sources, IReadOnlyList<FilterFolder> filters)
    {
        Text = "Import Data";
        ShowIcon = false;
        ClientSize = new Size(580, 460);
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

        int already = data.Repos.Count - newRepos;
        string summary = $"{Count(data.Repos.Count, "repository", "repositories")} in "
                       + $"{Count(data.Filters.Count, "folder", "folders")}. New here: {Count(newRepos, "repository", "repositories")}";
        summary += already > 0 ? $"; {Count(already, "is", "are")} already in your list." : ".";

        Controls.Add(new Label
        {
            Text = summary,
            AutoSize = false,
            Location = new Point(Edge, 36),
            Size = new Size(width, 20),
            ForeColor = Theme.DimText
        });

        Controls.Add(new Label
        {
            Text = "Choose where the repositories of each folder go:",
            AutoSize = false,
            Location = new Point(Edge, 68),
            Size = new Size(width, 20),
            ForeColor = Theme.Text
        });

        Controls.Add(new Label { Text = "In the file", AutoSize = true, Location = new Point(Edge, 96), ForeColor = Theme.DimText });
        Controls.Add(new Label { Text = "Goes into", AutoSize = true, Location = new Point(Edge + ListLeft, 96), ForeColor = Theme.DimText });

        var import = new Button { Text = "Import", Size = new Size(96, 30), TabIndex = 1000 };
        var cancel = new Button { Text = "Cancel", Size = new Size(96, 30), TabIndex = 1001, DialogResult = DialogResult.Cancel };

        int buttonsTop = ClientSize.Height - Edge - 30;
        cancel.Location = new Point(ClientSize.Width - Edge - 96, buttonsTop);
        import.Location = new Point(cancel.Left - 8 - 96, buttonsTop);

        // A long list scrolls inside its own panel; the buttons stay where they are.
        var rows = new Panel
        {
            Location = new Point(Edge, RowsTop),
            Size = new Size(width, buttonsTop - 12 - RowsTop),
            AutoScroll = true,
            BackColor = Theme.Background
        };
        DarkControls.UseDarkScrollbars(rows);
        Controls.Add(rows);

        List<(FilterFolder Folder, string FullPath)> existing = FilterStore.Flatten(filters);

        // Room for the scrollbar on the right, whether it shows or not.
        int listWidth = width - ListLeft - SystemInformation.VerticalScrollBarWidth - 4;

        for (int i = 0; i < sources.Count; i++)
        {
            (IReadOnlyList<string> path, int count) = sources[i];
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
                Text = Count(count, "repo", "repos"),
                AutoSize = false,
                Location = new Point(ListLeft - 78, top + 4),
                Size = new Size(70, 20),
                ForeColor = Theme.DimText
            });

            ComboBox list = DarkControls.CreateList(i);
            list.Items.AddRange([.. ChoicesFor(path, existing, filters)]);
            list.SetBounds(ListLeft, top, listWidth, list.Height);

            // Paths can be longer than the box: the open list is as wide as its longest entry.
            list.DropDownWidth = Math.Max(listWidth, WidestChoice(list) + 24);
            list.SelectedIndex = 0;

            rows.Controls.Add(list);
            _rows.Add((path, list));
        }

        import.Click += (_, _) =>
        {
            foreach ((IReadOnlyList<string> path, ComboBox list) in _rows)
            {
                Targets[DataTransfer.Key(path)] = (list.SelectedItem as Choice)?.Target ?? ImportTarget.SameAs(path);
            }

            DialogResult = DialogResult.OK;
        };

        Controls.Add(import);
        Controls.Add(cancel);
        AcceptButton = import;
        CancelButton = cancel;
    }

    /// <summary>The file's own folder first (the default), "Do not import", then where else it can go.</summary>
    private static List<Choice> ChoicesFor(
        IReadOnlyList<string> path, List<(FilterFolder Folder, string FullPath)> existing, IReadOnlyList<FilterFolder> filters)
    {
        var choices = new List<Choice>();

        if (path.Count == 0)
        {
            choices.Add(new Choice(ImportTarget.SameAs(path), "(No folder)"));
        }
        else
        {
            string text = string.Join("\\", path);
            bool missing = DataTransfer.FindFolder(path, filters) is null;
            choices.Add(new Choice(ImportTarget.SameAs(path), missing ? text + " (Create)" : text));
        }

        choices.Add(new Choice(ImportTarget.DoNotImport, "Do not import"));

        if (path.Count > 0)
        {
            choices.Add(new Choice(ImportTarget.Into(null), "(No folder)"));
        }

        choices.AddRange(existing.Select(f => new Choice(ImportTarget.Into(f.Folder.Id), f.FullPath)));
        return choices;
    }

    private static int WidestChoice(ComboBox list) =>
        list.Items.Cast<object>().Max(item => TextRenderer.MeasureText(item.ToString(), list.Font).Width);

    internal static string Count(int count, string one, string many) =>
        count.ToString(CultureInfo.InvariantCulture) + " " + (count == 1 ? one : many);
}
