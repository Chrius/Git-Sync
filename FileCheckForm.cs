using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace GitSync;

/// <summary>
/// What Check Files found: the recorded files that are gone, and what in Apps
/// belongs to no repository. Double-click shows an item in Explorer; the
/// records of the missing files can be dropped.
/// </summary>
internal sealed class FileCheckForm : Form
{
    private const int Edge = 16;

    private sealed record Line(string Text, string Path)
    {
        public override string ToString() => Text;
    }

    private readonly ListBox _missing;
    private readonly Label _missingTitle;
    private readonly Button _forget;
    private readonly List<MissingFile> _missingFiles;
    private readonly ToolTip _tips = new();

    /// <summary>"Forget Missing Files" was used: the main window reads the records again.</summary>
    internal bool Forgot { get; private set; }

    internal FileCheckForm(FileCheckResult result)
    {
        Text = "Check Files";
        ShowIcon = false;
        ClientSize = new Size(640, 500);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 9F);

        _missingFiles = [.. result.Missing];
        int width = ClientSize.Width - (2 * Edge);

        Controls.Add(new Label
        {
            Text = Summary(result),
            AutoSize = false,
            Location = new Point(Edge, 14),
            Size = new Size(width, 36),
            ForeColor = Theme.Text
        });

        _missingTitle = Title(string.Empty, 58);
        _missing = AddList(78, 140);
        ShowMissing();

        Label looseTitle = Title($"In Apps, but part of no repository ({Number(result.Loose.Count)})", 232);
        ListBox loose = AddList(252, 168);
        string apps = AppPaths.AppsDir + Path.DirectorySeparatorChar;

        foreach (string path in result.Loose)
        {
            string shown = path.StartsWith(apps, StringComparison.OrdinalIgnoreCase) ? path[apps.Length..] : path;
            loose.Items.Add(new Line(Directory.Exists(path) ? shown + Path.DirectorySeparatorChar : shown, path));
        }

        if (result.Loose.Count == 0)
        {
            loose.Items.Add(new Line("Nothing: everything in Apps belongs to a repository.", string.Empty));
        }

        Controls.Add(new Label
        {
            Text = "Double-click an item to show it in Explorer.",
            AutoSize = false,
            Location = new Point(Edge, 426),
            Size = new Size(width, 18),
            ForeColor = Theme.DimText
        });

        var close = new Button { Text = "Close", Size = new Size(96, 30), DialogResult = DialogResult.Cancel };
        close.Location = new Point(ClientSize.Width - Edge - close.Width, ClientSize.Height - Edge - close.Height);
        Controls.Add(close);
        CancelButton = close;

        _forget = new Button { Text = "Forget Missing Files", Size = new Size(150, 30), Enabled = _missingFiles.Count > 0 };
        _forget.Location = new Point(Edge, close.Top);
        _forget.Click += (_, _) => Forget();
        Controls.Add(_forget);

        _tips.SetToolTip(_forget,
            "Stops listing the missing files in the records. The repository keeps its current version.");

        AcceptButton = close;
    }

    private static string Summary(FileCheckResult result)
    {
        string text = result.Missing.Count == 0
            ? $"All {Number(result.Checked)} recorded {(result.Checked == 1 ? "file is" : "files are")} in place."
            : $"{Number(result.Missing.Count)} of the {Number(result.Checked)} recorded files {(result.Missing.Count == 1 ? "is" : "are")} missing.";

        if (result.Adopted > 0)
        {
            text += result.Adopted == 1
                ? " 1 earlier download was found in a repository's folder and recorded."
                : $" {Number(result.Adopted)} earlier downloads were found in repositories' folders and recorded.";
        }

        return text;
    }

    private static string Number(int count) => count.ToString("N0", CultureInfo.InvariantCulture);

    private Label Title(string text, int top)
    {
        var label = DarkControls.CreateLabel(text, Edge, top);
        label.AutoSize = false;
        label.Size = new Size(ClientSize.Width - (2 * Edge), 18);
        Controls.Add(label);
        return label;
    }

    private ListBox AddList(int top, int height)
    {
        var frame = new Panel
        {
            BackColor = Theme.Field,
            Padding = new Padding(2),
            Bounds = new Rectangle(Edge, top, ClientSize.Width - (2 * Edge), height)
        };

        var list = new ListBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Field,
            ForeColor = Theme.Text,
            IntegralHeight = false,
            HorizontalScrollbar = true
        };

        list.DoubleClick += (_, _) =>
        {
            if (list.SelectedItem is Line { Path.Length: > 0 } line)
            {
                ShowInExplorer(line.Path);
            }
        };

        DarkControls.UseDarkScrollbars(list);
        frame.Controls.Add(list);
        Controls.Add(frame);
        return list;
    }

    private void ShowMissing()
    {
        _missingTitle.Text = $"Recorded, but missing ({Number(_missingFiles.Count)})";
        _missing.Items.Clear();

        foreach (MissingFile file in _missingFiles)
        {
            string version = file.Version.Length > 0 ? $" {file.Version}" : string.Empty;
            _missing.Items.Add(new Line($"{file.RepoName}{version}: {file.Path}", DownloadPaths.ToAbsolute(file.Path)));
        }

        if (_missingFiles.Count == 0)
        {
            _missing.Items.Add(new Line("Nothing: every recorded file is there.", string.Empty));
        }
    }

    /// <summary>
    /// The item selected in Explorer. A missing file opens the nearest folder
    /// that is still there, so its neighbours can be looked at.
    /// </summary>
    private void ShowInExplorer(string path)
    {
        try
        {
            if (File.Exists(path) || Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                return;
            }

            string? folder = Path.GetDirectoryName(path);

            while (folder is not null && !Directory.Exists(folder))
            {
                folder = Path.GetDirectoryName(folder);
            }

            if (folder is not null)
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
            }
        }
        catch (Exception error)
        {
            DarkMessageBox.Show(this, "Could not open Explorer.\n" + error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
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

    private void Forget()
    {
        string question = _missingFiles.Count == 1
            ? "Stop listing the missing file in the records?"
            : $"Stop listing the {Number(_missingFiles.Count)} missing files in the records?";

        if (DarkMessageBox.Show(this, question + "\n\nNothing on disk changes.", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        FileCheck.ForgetMissing(_missingFiles);
        _missingFiles.Clear();
        Forgot = true;
        _forget.Enabled = false;
        ShowMissing();
    }
}
