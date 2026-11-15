using System.Drawing;
using System.Globalization;
using System.Text;
using System.Windows.Forms;
using Microsoft.Data.Sqlite;

namespace GitSync;

/// <summary>
/// "Data" tab: export the list to a file and import it into another install
/// (or this one), and check the downloaded files against the records.
/// Exporting only reads; importing only adds.
/// </summary>
internal sealed class DataTab : UserControl
{
    private const int ButtonWidth = 130;
    private const int ButtonHeight = 28;

    private readonly Button _export;
    private readonly Label _exportHint;
    private readonly Button _import;
    private readonly Label _importHint;
    private readonly Button _check;
    private readonly Label _checkHint;
    private readonly Label _result;
    private readonly ProgressStrip _bar;

    /// <summary>Something was imported; Settings refreshes its other tabs, and the main window checks the new repos.</summary>
    internal event Action<ImportResult>? Imported;

    /// <summary>An import or a file check is running; Settings stays open until it ends.</summary>
    internal bool Busy { get; private set; }

    /// <summary>What is running, for the message when Settings is closed meanwhile.</summary>
    internal string BusyWith { get; private set; } = string.Empty;

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

        _importHint = Hint("Adds the repositories of an exported file. For each of its folders you choose where "
                         + "they go: the same folder, one of yours, or not at all. Repositories already in your "
                         + "list are skipped, so importing another file only adds what is missing.");

        _check = new Button { Text = "Check Files", Size = new Size(ButtonWidth, ButtonHeight), TabIndex = 2 };
        _check.Click += (_, _) => CheckFiles();
        Controls.Add(_check);

        _checkHint = Hint("Looks for the files recorded for each repository and lists the ones that are gone, "
                        + "and what is in Apps without being part of any repository.");

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

        _check.Location = new Point(0, _importHint.Bottom + 24);
        _checkHint.SetBounds(0, _check.Bottom + 8, width, TextHeight(_checkHint, width));

        _result.SetBounds(0, _checkHint.Bottom + 24, width, Math.Max(0, ClientSize.Height - _checkHint.Bottom - 24));

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

        // Folders only come along with repositories, so with none new there is nothing to do.
        if (newRepos == 0)
        {
            ShowResult("Nothing to import: every repository in this file is already in your list.", Theme.DimText);
            return;
        }

        using var window = new ImportForm(
            Path.GetFileName(dialog.FileName), data, newRepos, DataTransfer.SourceFolders(data, repos, sites), filters);

        if (window.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        Dictionary<string, ImportTarget> targets = window.Targets;

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
            result = await Task.Run(() => DataTransfer.Import(data, targets, progress));
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

    private void SetBusy(bool busy, string with = "The import", bool showBar = true)
    {
        Busy = busy;
        BusyWith = with;
        _export.Enabled = !busy;
        _import.Enabled = !busy;
        _check.Enabled = !busy;
        _bar.Visible = busy && showBar;
    }

    // ---------- check files ----------

    /// <summary>
    /// Off the window's thread: a large Apps folder takes a moment to walk.
    /// All in order shows a line here; anything to look at opens the list.
    /// </summary>
    private async void CheckFiles()
    {
        SetBusy(true, "The file check", showBar: false);
        ShowResult("Checking files…", Theme.Text);

        FileCheckResult result;

        try
        {
            result = await Task.Run(FileCheck.Run);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SqliteException)
        {
            ShowResult("Could not check the files: " + error.Message, Theme.Error);
            return;
        }
        finally
        {
            SetBusy(false);
        }

        if (result.Missing.Count == 0 && result.Loose.Count == 0)
        {
            string adopted = result.Adopted switch
            {
                0 => string.Empty,
                1 => " 1 earlier download was found in a repository's folder and recorded.",
                _ => $" {result.Adopted} earlier downloads were found in repositories' folders and recorded."
            };

            ShowResult($"All {Count(result.Checked, "recorded file is", "recorded files are")} in place, "
                     + "and everything in Apps belongs to a repository." + adopted, Theme.Success);
            return;
        }

        ShowResult($"{Count(result.Missing.Count, "missing file", "missing files")}, "
                 + $"{Count(result.Loose.Count, "item", "items")} in Apps part of no repository.", Theme.Amber);

        using var window = new FileCheckForm(result);
        window.ShowDialog(this);

        if (window.Forgot)
        {
            ShowResult($"Missing files forgotten. {Count(result.Loose.Count, "item", "items")} in Apps part of no repository.", Theme.DimText);
        }
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

        if (result.LeftOut > 0)
        {
            text.Append(result.LeftOut == 1
                ? " 1 repository was left out (Do not import)."
                : $" {result.LeftOut} repositories were left out (Do not import).");
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
