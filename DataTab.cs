using System.Drawing;
using System.Globalization;
using System.Text;
using System.Windows.Forms;
using Microsoft.Data.Sqlite;

namespace GitSync;

/// <summary>
/// "Data" tab: export the list to a file and import it into another install
/// (or this one). Exporting only reads; importing only adds.
/// </summary>
internal sealed class DataTab : UserControl
{
    private const int ButtonWidth = 130;
    private const int ButtonHeight = 28;

    private readonly Button _export;
    private readonly Label _exportHint;
    private readonly Button _import;
    private readonly Label _importHint;
    private readonly Label _result;
    private readonly ProgressStrip _bar;

    /// <summary>Something was imported; Settings refreshes its other tabs, and the main window checks the new repos.</summary>
    internal event Action<ImportResult>? Imported;

    /// <summary>An import is running; Settings stays open until it ends.</summary>
    internal bool Busy { get; private set; }

    internal DataTab()
    {
        Dock = DockStyle.Fill;

        _export = new Button { Text = "Export Data…", Size = new Size(ButtonWidth, ButtonHeight), TabIndex = 0 };
        _export.Click += (_, _) => Export();
        Controls.Add(_export);

        _exportHint = Hint("Saves every repository with its settings, and the folders that hold them, to a file "
                         + "another Git-Sync can import. Tokens and downloaded files are not included.");

        _import = new Button { Text = "Import Data…", Size = new Size(ButtonWidth, ButtonHeight), TabIndex = 1 };
        _import.Click += (_, _) => Import();
        Controls.Add(_import);

        _importHint = Hint("Adds the repositories of an exported file, with or without its folders. Repositories "
                         + "already in your list are skipped, so importing another file only adds what is missing.");

        _result = new Label { AutoSize = false, ForeColor = Theme.DimText };
        Controls.Add(_result);

        _bar = new ProgressStrip { Visible = false };
        Controls.Add(_bar);

        // The result label runs to the bottom of the tab, over the bar; added
        // earlier, it would sit on top of it and hide it.
        _bar.BringToFront();
    }

    private Label Hint(string text)
    {
        var label = new Label { AutoSize = false, ForeColor = Theme.DimText, Text = text };
        Controls.Add(label);
        return label;
    }

    /// <summary>Laid out from the real size (see FiltersTab: Anchor computes from the wrong width here).</summary>
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);

        int width = ClientSize.Width;

        if (width <= ButtonWidth)
        {
            return;
        }

        _export.Location = new Point(0, 0);
        _exportHint.SetBounds(0, _export.Bottom + 8, width, TextHeight(_exportHint, width));

        _import.Location = new Point(0, _exportHint.Bottom + 24);
        _importHint.SetBounds(0, _import.Bottom + 8, width, TextHeight(_importHint, width));

        _result.SetBounds(0, _importHint.Bottom + 24, width, Math.Max(0, ClientSize.Height - _importHint.Bottom - 24));

        // Under the one-line "Importing…" text, the only thing shown while the bar is.
        _bar.SetBounds(0, _result.Top + 26, width, 6);
    }

    private static int TextHeight(Label label, int width) =>
        TextRenderer.MeasureText(label.Text, label.Font, new Size(width, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height + 4;

    private void ShowResult(string text, Color color)
    {
        _result.Text = text;
        _result.ForeColor = color;
    }

    // ---------- export ----------

    private void Export()
    {
        ExportData data = DataTransfer.Collect(FilterStore.List(), RepoStore.List(), SiteStore.List());

        if (data.Repos.Count == 0)
        {
            ShowResult("There are no repositories to export yet.", Theme.Amber);
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Title = "Export Data",
            Filter = DataTransfer.FileFilter,
            FileName = "Git-Sync data " + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".json",
            DefaultExt = "json",
            AddExtension = true,
            OverwritePrompt = true
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            // Without a byte order mark: other tools read plain UTF-8 JSON more reliably.
            File.WriteAllText(dialog.FileName, DataTransfer.ToJson(data, DateTimeOffset.UtcNow), new UTF8Encoding(false));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowResult("Could not save the file: " + error.Message, Theme.Error);
            return;
        }

        ShowResult($"Exported {Count(data.Repos.Count, "repository", "repositories")} and "
                 + $"{Count(data.Filters.Count, "folder", "folders")} to {Path.GetFileName(dialog.FileName)}.", Theme.Success);
    }

    // ---------- import ----------

    private async void Import()
    {
        using var dialog = new OpenFileDialog { Title = "Import Data", Filter = DataTransfer.FileFilter };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        ExportData data;

        try
        {
            data = DataTransfer.Parse(File.ReadAllText(dialog.FileName));
        }
        catch (FormatException error)
        {
            ShowResult(error.Message, Theme.Error);
            return;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowResult("Could not read the file: " + error.Message, Theme.Error);
            return;
        }

        List<FilterFolder> filters = FilterStore.List();
        List<Repo> repos = RepoStore.List();
        List<Site> sites = SiteStore.List();

        int newRepos = DataTransfer.NewRepos(data, repos, sites);
        int newFolders = DataTransfer.MissingFolders(data, filters);

        if (newRepos == 0 && newFolders == 0)
        {
            ShowResult("Nothing to import: everything in this file is already here.", Theme.DimText);
            return;
        }

        using var choice = new ImportChoiceForm(Path.GetFileName(dialog.FileName), data, newRepos, newFolders);

        if (choice.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        Dictionary<string, long?>? destinations = null;

        if (choice.ReposOnly)
        {
            if (newRepos == 0)
            {
                ShowResult("Nothing to import: every repository in this file is already in your list.", Theme.DimText);
                return;
            }

            using var map = new FolderMapForm(DataTransfer.SourceFolders(data, repos, sites), filters);

            if (map.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            destinations = map.Destinations;
        }

        // Off the window's thread, which meanwhile shows how far it got: a long
        // list on a slow drive still takes a moment, and a frozen window looks
        // like a crash.
        int total = data.Repos.Count;
        SetBusy(true);
        ShowProgress(0, total);

        // Progress<T> delivers its reports later, through the message queue:
        // the last one can arrive after the import has ended and would write
        // "Importing…" over the result.
        bool finished = false;
        var progress = new Progress<int>(done =>
        {
            if (!finished)
            {
                ShowProgress(done, total);
            }
        });

        ImportResult result;

        try
        {
            result = await Task.Run(() => DataTransfer.Import(data, destinations, progress));
        }
        catch (SqliteException error)
        {
            // The import is one transaction: nothing of it was kept.
            ShowResult("Could not import the file, nothing was added: " + error.Message, Theme.Error);
            return;
        }
        finally
        {
            finished = true;
            SetBusy(false);
        }

        Imported?.Invoke(result);
        ShowResult(Describe(result), Theme.Success);
    }

    private void ShowProgress(int done, int total)
    {
        _bar.Value = total == 0 ? 0 : (double)done / total;
        ShowResult($"Importing… {done.ToString(CultureInfo.InvariantCulture)} of {Count(total, "repository", "repositories")}", Theme.Text);
    }

    private void SetBusy(bool busy)
    {
        Busy = busy;
        _export.Enabled = !busy;
        _import.Enabled = !busy;
        _bar.Visible = busy;
    }

    private static string Describe(ImportResult result)
    {
        var added = new List<string>();

        if (result.AddedRepoIds.Count > 0)
        {
            added.Add(Count(result.AddedRepoIds.Count, "repository", "repositories"));
        }

        if (result.FoldersAdded > 0)
        {
            added.Add(Count(result.FoldersAdded, "folder", "folders"));
        }

        var text = new StringBuilder(added.Count == 0 ? "Nothing added." : "Added " + string.Join(" and ", added) + ".");

        if (result.AlreadyThere > 0)
        {
            text.Append(result.AlreadyThere == 1
                ? " 1 repository was already in your list."
                : $" {result.AlreadyThere} repositories were already in your list.");
        }

        if (result.AddedRepoIds.Count > 0)
        {
            text.Append(" The new ones are checked when Settings closes.");
        }

        return text.ToString();
    }

    private static string Count(int count, string one, string many) =>
        count.ToString(CultureInfo.InvariantCulture) + " " + (count == 1 ? one : many);
}
