using System.Drawing;
using System.Globalization;
using System.Text;
using System.Windows.Forms;
using Microsoft.Data.Sqlite;

namespace GitSync;

/// <summary>
/// "Data" tab: export the list to a file and import it into another install
/// (or this one), check the downloaded files against the records, and choose
/// where downloads go. Exporting only reads; importing only adds.
/// </summary>
internal sealed class DataTab : UserControl
{
    private const int ButtonWidth = 130;
    private const int ButtonHeight = 28;
    private const int Indent = 20;

    private readonly Button _export;
    private readonly Label _exportHint;
    private readonly Button _import;
    private readonly Label _importHint;
    private readonly Button _check;
    private readonly Label _checkHint;
    private readonly Panel _separator;
    private readonly CheckBox _custom;
    private readonly Button _choose;
    private readonly Label _folder;
    private readonly Label _result;
    private readonly ProgressStrip _bar;

    /// <summary>Whether the main window is downloading: the folder cannot change under a download.</summary>
    private readonly Func<bool> _downloadsRunning;

    /// <summary>True while the checkbox is set from the saved setting, so it is not taken for a click.</summary>
    private bool _filling;

    private readonly ToolTip _tips = new();

    /// <summary>The custom folder shown under the checkbox (shortened to fit), or empty.</summary>
    private string _folderPath = string.Empty;

    private string _folderNote = string.Empty;

    /// <summary>Something was imported; Settings refreshes its other tabs, and the main window checks the new repos.</summary>
    internal event Action<ImportResult>? Imported;

    /// <summary>An import or a file check is running; Settings stays open until it ends.</summary>
    internal bool Busy { get; private set; }

    /// <summary>What is running, for the message when Settings is closed meanwhile.</summary>
    internal string BusyWith { get; private set; } = string.Empty;

    internal DataTab(Func<bool> downloadsRunning)
    {
        Dock = DockStyle.Fill;
        _downloadsRunning = downloadsRunning;

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
                        + "and what is in the download folder without being part of any repository.");

        _separator = new Panel { BackColor = Theme.Separator, Height = 1 };
        Controls.Add(_separator);

        _custom = new CheckBox { Text = "Use custom download folder", AutoSize = true, TabIndex = 3 };
        _custom.CheckedChanged += (_, _) => CustomToggled();
        Controls.Add(_custom);

        _choose = new Button { Text = "Choose Folder…", Size = new Size(ButtonWidth, ButtonHeight), TabIndex = 4 };
        _choose.Click += (_, _) => ChooseFolder();
        Controls.Add(_choose);

        _folder = new Label { AutoSize = false, ForeColor = Theme.DimText, UseMnemonic = false };
        Controls.Add(_folder);

        _result = new Label { AutoSize = false, ForeColor = Theme.DimText };
        Controls.Add(_result);

        _bar = new ProgressStrip { Visible = false };
        Controls.Add(_bar);

        // The result label runs to the bottom of the tab, over the bar; added
        // earlier, it would sit on top of it and hide it.
        _bar.BringToFront();

        ShowFolderSetting();
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
        Arrange();
    }

    /// <summary>Top to bottom; the folder's button and text take room only while the option is ticked.</summary>
    private void Arrange()
    {
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

        _separator.SetBounds(0, _checkHint.Bottom + 20, width, 1);
        _custom.Location = new Point(0, _separator.Bottom + 16);
        _choose.Location = new Point(Indent, _custom.Bottom + 8);
        _folder.Text = _folderPath.Length == 0 ? _folderNote : _folderNote + "\n" + FitPath(_folderPath, _folder.Font, width - Indent);
        _folder.SetBounds(Indent, _choose.Bottom + 6, width - Indent, TextHeight(_folder, width - Indent));

        int resultTop = (_custom.Checked ? _folder.Bottom : _custom.Bottom) + 20;
        _result.SetBounds(0, resultTop, width, Math.Max(0, ClientSize.Height - resultTop));

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
        _custom.Enabled = !busy;
        _choose.Enabled = !busy;
        _bar.Visible = busy && showBar;
    }

    // ---------- download folder ----------

    private void ShowFolderSetting()
    {
        _filling = true;
        _custom.Checked = DownloadRoot.Custom() is not null;
        _filling = false;
        ShowFolder();
    }

    private void ShowFolder()
    {
        _choose.Visible = _custom.Checked;
        _folder.Visible = _custom.Checked;

        if (DownloadRoot.Custom() is { } custom)
        {
            bool there = Directory.Exists(custom);
            _folderNote = there ? "Downloads go to:" : "Downloads go to this folder, which cannot be found. Is its drive connected?";
            _folderPath = custom;
            _folder.ForeColor = there ? Theme.DimText : Theme.Amber;
            _tips.SetToolTip(_folder, custom);
        }
        else
        {
            _folderNote = "No folder chosen yet: downloads still go to Apps, beside Git-Sync.";
            _folderPath = string.Empty;
            _folder.ForeColor = Theme.DimText;
            _tips.SetToolTip(_folder, null);
        }

        Arrange();
    }

    /// <summary>
    /// A path that fits the width on one line: folders taken out of the middle,
    /// "D:\…\Games\Downloads". A path has no spaces to wrap at, so a long one
    /// would otherwise be cut off at the edge. The whole path is in the tooltip.
    /// </summary>
    private static string FitPath(string path, Font font, int width)
    {
        bool Fits(string text) => TextRenderer.MeasureText(text, font, Size.Empty, TextFormatFlags.NoPadding).Width <= width;

        if (Fits(path))
        {
            return path;
        }

        string[] parts = path.Split(Path.DirectorySeparatorChar);

        for (int skip = 1; skip < parts.Length - 1; skip++)
        {
            string shorter = string.Join(Path.DirectorySeparatorChar, [parts[0], "…", .. parts[(1 + skip)..]]);

            if (Fits(shorter))
            {
                return shorter;
            }
        }

        // Even the last folder alone is too long: its end goes.
        string last = parts[0] + Path.DirectorySeparatorChar + "…" + Path.DirectorySeparatorChar + parts[^1];

        while (last.Length > 4 && !Fits(last + "…"))
        {
            last = last[..^1];
        }

        return last + "…";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tips.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Ticked, nothing changes until a folder is chosen. Unticked while a
    /// custom folder is in use, downloads go back to Apps — through the same
    /// questions as any other change of folder, and if those are declined
    /// the box stays ticked.
    /// </summary>
    private async void CustomToggled()
    {
        if (_filling)
        {
            return;
        }

        if (!_custom.Checked && DownloadRoot.Custom() is not null)
        {
            await SwitchTo(null);
            ShowFolderSetting();
            return;
        }

        ShowFolder();
    }

    private async void ChooseFolder()
    {
        string? custom = DownloadRoot.Custom();

        using var dialog = new FolderBrowserDialog
        {
            Description = "Folder to download the repositories into",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            InitialDirectory = custom is not null && Directory.Exists(custom) ? custom : string.Empty
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        await SwitchTo(dialog.SelectedPath);
        ShowFolderSetting();

        // A choice declined leaves the box ticked, for another folder.
        if (DownloadRoot.Custom() is null)
        {
            _filling = true;
            _custom.Checked = true;
            _filling = false;
            ShowFolder();
        }
    }

    /// <summary>
    /// Changes the download folder, to <paramref name="folder"/> or to Apps
    /// with null. When the current one holds anything, asks first: move it
    /// all along, or leave it behind and forget it, or (closing either
    /// question) change nothing. Returns whether the folder changed.
    /// </summary>
    private async Task<bool> SwitchTo(string? folder)
    {
        string from = DownloadRoot.Current;
        string to = folder is null ? AppPaths.AppsDir : Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);

        if (DownloadRoot.Problem(to, from) is { } problem)
        {
            Warn(problem);
            return false;
        }

        if (_downloadsRunning())
        {
            Warn("Downloads are running. Change the download folder when they are done.");
            return false;
        }

        if (FolderTransfer.HasContent(from))
        {
            DialogResult move = DarkMessageBox.Show(this,
                $"Move everything in the current download folder\n{from}\ninto\n{to}?\n\n"
                + "Every folder keeps its name and place inside it.",
                "Settings", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

            if (move == DialogResult.Yes)
            {
                return await MoveTo(from, to, folder);
            }

            // Closed without an answer: nothing changes.
            if (move != DialogResult.No)
            {
                return false;
            }

            DialogResult leave = DarkMessageBox.Show(this,
                $"Use the new folder without the current files?\n\nThey stay in\n{from}\n"
                + "and Git-Sync forgets them: it will not move, delete or count them any more. "
                + "Each repository keeps its version; Redownload… on its menu fetches it into the new folder.",
                "Settings", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,

                // "No" by default: leaving every file behind is not done with an Enter pressed by mistake.
                MessageBoxDefaultButton.Button2);

            if (leave != DialogResult.Yes)
            {
                return false;
            }

            RepoStore.ForgetFilesUnder(from);
            DownloadRoot.SetCustom(folder);
            ShowResult(Destination(folder) + " The files of the old folder were left there, and forgotten.", Theme.Success);
            return true;
        }

        DownloadRoot.SetCustom(folder);
        ShowResult(Destination(folder), Theme.Success);
        return true;
    }

    /// <summary>
    /// Moves the whole download folder's content, off the window's thread: to
    /// another drive every byte is copied, which takes a while. The records
    /// follow the files, and the setting changes only once all of it moved.
    /// </summary>
    private async Task<bool> MoveTo(string from, string to, string? folder)
    {
        List<string> taken = FolderTransfer.Conflicts(from, to);

        if (taken.Count > 0)
        {
            string names = string.Join("\n", taken.Take(8)) + (taken.Count > 8 ? $"\n… and {taken.Count - 8} more" : string.Empty);
            Warn($"{to} already has folders or files with the same names:\n\n{names}\n\n"
                 + "Move them away first, or choose another folder. Nothing was changed.");
            return false;
        }

        SetBusy(true, "Moving the downloads");
        ShowResult("Moving the downloads…", Theme.Text);
        _bar.Value = 0;

        // Progress<T> reports arrive later, through the message queue: one
        // could land after the end and write "Moving…" over the result.
        bool finished = false;
        var progress = new Progress<(long Done, long Total)>(p =>
        {
            if (!finished && p.Total > 0)
            {
                _bar.Value = (double)p.Done / p.Total;
                ShowResult($"Moving the downloads… {ByteSize(p.Done)} of {ByteSize(p.Total)}", Theme.Text);
            }
        });

        List<string> left;

        try
        {
            left = await Task.Run(() => FolderTransfer.MoveContents(from, to, progress));
        }
        catch (IOException error)
        {
            ShowResult("Could not move the downloads, so nothing changed: " + error.Message, Theme.Error);
            return false;
        }
        finally
        {
            finished = true;
            SetBusy(false);
        }

        var moves = new PathMoves();
        moves.Add(from, to);
        RepoStore.ApplyMoves(moves);
        DownloadRoot.SetCustom(folder);

        // Apps is Git-Sync's own folder: emptied, it goes. A custom folder
        // was the user's choice, maybe there before, and stays.
        if (string.Equals(from, AppPaths.AppsDir, StringComparison.OrdinalIgnoreCase) && !FolderTransfer.HasContent(from))
        {
            try
            {
                Directory.Delete(from);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // An empty Apps folder left behind is harmless.
            }
        }

        ShowResult(left.Count == 0
            ? "Everything moved. " + Destination(folder)
            : "Everything was copied to the new folder, and downloads go there now, but "
              + $"{Count(left.Count, "item", "items")} could not be removed from {from} (in use?). Delete them there by hand.",
            left.Count == 0 ? Theme.Success : Theme.Amber);
        return true;
    }

    /// <summary>Said after a change; the folder itself is shown above, in full in its tooltip.</summary>
    private static string Destination(string? folder) =>
        folder is null ? "Downloads go to Apps again, beside Git-Sync." : "Downloads now go to the new folder.";

    private static string ByteSize(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
        _ => (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB"
    };

    private void Warn(string message) =>
        DarkMessageBox.Show(this, message, "Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    // ---------- check files ----------

    /// <summary>
    /// Off the window's thread: a large download folder takes a moment to walk.
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

            if (result.RootMissing)
            {
                ShowResult($"The download folder {DownloadRoot.Current} cannot be found, so nothing was checked. "
                         + "Connect its drive, or choose another folder below.", Theme.Amber);
                return;
            }
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
                     + "and everything in the download folder belongs to a repository." + adopted, Theme.Success);
            return;
        }

        ShowResult($"{Count(result.Missing.Count, "missing file", "missing files")}, "
                 + $"{Count(result.Loose.Count, "item", "items")} in the download folder part of no repository.", Theme.Amber);

        using var window = new FileCheckForm(result);
        window.ShowDialog(this);

        if (window.Forgot)
        {
            ShowResult($"Missing files forgotten. {Count(result.Loose.Count, "item", "items")} in the download folder part of no repository.", Theme.DimText);
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
