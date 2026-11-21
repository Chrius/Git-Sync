using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace GitSync;

/// <summary>
/// Checked list of a release's files, with All / None. Shared by the file
/// picker and the Update All window. Shows a message instead of the list
/// while the files load or when they cannot be loaded.
///
/// Files matching the ignored files of Settings > Files are left out, until
/// "Show All Files" brings them back for this list; every new list starts
/// filtered again. A file left out is never downloaded, ticked or not.
/// </summary>
internal sealed class FileChecklist : UserControl
{
    /// <param name="Ignored">Matches an ignored-file pattern, shown only because Show All Files is on.</param>
    private sealed record Entry(ReleaseFile File, bool Ignored)
    {
        // Source archives are generated on request and have no size; the note
        // says what they are, since their name is Git-Sync's, not the site's.
        public override string ToString() =>
            (File.Size > 0 ? $"{File.Name}    ({FormatSize(File.Size)})"
            : File.IsSource ? $"{File.Name}    (source code)"
            : File.Name)
            + (Ignored ? "    \u00b7 ignored" : string.Empty);
    }

    private readonly Panel _frame;
    private readonly CheckedListBox _list;
    private readonly Label _message;
    private readonly FlatButton _all;
    private readonly FlatButton _none;
    private readonly FlatButton _showAll;

    /// <summary>Every file of the release, shown or not.</summary>
    private IReadOnlyList<ReleaseFile> _files = [];

    /// <summary>Ticked files, kept apart from the list so a tick survives hiding and showing again.</summary>
    private readonly HashSet<ReleaseFile> _ticked = [];

    private FileFilters _filters = FileFilters.None;
    private bool _showingAll;

    /// <summary>
    /// True while the list is checked by code (filling it, All / None): those
    /// changes are reported once at the end instead of item by item.
    /// </summary>
    private bool _filling;

    internal event EventHandler? SelectionChanged;

    internal FileChecklist()
    {
        BackColor = Theme.Background;

        _frame = new Panel { BackColor = Theme.Field, Padding = new Padding(6, 4, 2, 4) };

        _list = new CheckedListBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Field,
            ForeColor = Theme.Text,
            CheckOnClick = true,
            IntegralHeight = false
        };

        // ItemCheck fires before the state changes, so the count is right only
        // after a BeginInvoke. But BeginInvoke throws while the window has no
        // handle yet — which is the case when the file picker pre-checks last
        // time's files from its constructor (the crash on updating a release
        // with several files). Checks made by code are reported by the code
        // itself, once, when it is done.
        _list.ItemCheck += (_, e) =>
        {
            if (_filling)
            {
                return;
            }

            if (e.Index >= 0 && e.Index < _list.Items.Count && _list.Items[e.Index] is Entry entry)
            {
                if (e.NewValue == CheckState.Checked)
                {
                    _ticked.Add(entry.File);
                }
                else
                {
                    _ticked.Remove(entry.File);
                }
            }

            if (IsHandleCreated)
            {
                BeginInvoke(() => SelectionChanged?.Invoke(this, EventArgs.Empty));
            }
        };
        DarkControls.UseDarkScrollbars(_list);

        _message = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Theme.DimText,
            BackColor = Theme.Field,
            Visible = false
        };

        _frame.Controls.Add(_list);
        _frame.Controls.Add(_message);
        Controls.Add(_frame);

        _all = new FlatButton { Text = "All", Size = new Size(64, 24) };
        _all.Click += (_, _) => SetAll(true);
        _none = new FlatButton { Text = "None", Size = new Size(64, 24) };
        _none.Click += (_, _) => SetAll(false);
        Controls.Add(_all);
        Controls.Add(_none);

        // Wide enough for "Show All Files (12 hidden)".
        _showAll = new FlatButton { Text = "Show All Files", Size = new Size(176, 24), Enabled = false };
        _showAll.Click += (_, _) =>
        {
            _showingAll = !_showingAll;
            Fill();
        };
        Controls.Add(_showAll);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);

        int buttonsTop = Height - _all.Height;
        _frame.SetBounds(0, 0, Width, Math.Max(20, buttonsTop - 8));
        _all.Location = new Point(0, buttonsTop);
        _none.Location = new Point(_all.Right + 6, buttonsTop);
        _showAll.Location = new Point(Width - _showAll.Width, buttonsTop);
    }

    internal void SetFiles(IReadOnlyList<ReleaseFile> files, ISet<string> preChecked)
    {
        _files = files;
        _filters = FileFilters.Saved();
        _showingAll = false;
        _ticked.Clear();

        // Ticked: what matched last time, and a single file shown, the obvious choice.
        foreach (ReleaseFile file in files.Where(f => preChecked.Contains(f.Name)))
        {
            _ticked.Add(file);
        }

        if (Shown() is [var only])
        {
            _ticked.Add(only);
        }

        Fill();
    }

    internal void SetMessage(string text)
    {
        _files = [];
        _ticked.Clear();
        _list.Items.Clear();
        _message.Text = text;
        _message.Visible = true;
        _list.Visible = false;
        SetButtonsEnabled(false);
        _showAll.Enabled = false;
        _showAll.Text = "Show All Files";
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private List<ReleaseFile> Shown() => _showingAll ? [.. _files] : [.. _files.Where(f => !_filters.Hides(f.Name))];

    /// <summary>The list again, after a new release or Show All Files.</summary>
    private void Fill()
    {
        List<ReleaseFile> shown = Shown();
        int hidden = _files.Count(f => _filters.Hides(f.Name));

        _filling = true;
        _list.BeginUpdate();
        _list.Items.Clear();

        foreach (ReleaseFile file in shown)
        {
            _list.Items.Add(new Entry(file, _showingAll && _filters.Hides(file.Name)), _ticked.Contains(file));
        }

        _list.EndUpdate();
        _filling = false;

        bool allHidden = shown.Count == 0 && hidden > 0;
        _message.Text = allHidden
            ? (hidden == 1 ? "The only file" : $"All {hidden} files") + " of this version match the ignored files "
              + "in Settings > Files.\nShow All Files lists them."
            : string.Empty;
        _message.Visible = allHidden;
        _list.Visible = !allHidden;

        SetButtonsEnabled(shown.Count > 1);
        _showAll.Enabled = hidden > 0;
        _showAll.Text = _showingAll ? "Hide Ignored Files"
            : hidden > 0 ? $"Show All Files ({hidden} hidden)"
            : "Show All Files";

        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetButtonsEnabled(bool enabled)
    {
        _all.Enabled = enabled;
        _none.Enabled = enabled;
    }

    /// <summary>The ticked files among those shown: a file left out by the ignored files is not downloaded.</summary>
    internal List<ReleaseFile> CheckedFiles =>
        [.. _list.CheckedItems.Cast<Entry>().Select(entry => entry.File)];

    private void SetAll(bool check)
    {
        _filling = true;

        for (int i = 0; i < _list.Items.Count; i++)
        {
            _list.SetItemChecked(i, check);

            if (_list.Items[i] is Entry entry)
            {
                if (check)
                {
                    _ticked.Add(entry.File);
                }
                else
                {
                    _ticked.Remove(entry.File);
                }
            }
        }

        _filling = false;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Always with a dot and in English units, whatever the machine's culture.</summary>
    internal static string FormatSize(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB"];
        double value = bytes;
        int unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes} bytes")
            : string.Create(CultureInfo.InvariantCulture, $"{value:0.0} {units[unit]}");
    }
}

/// <summary>
/// Picks which files of a release to download. Shown for every download, one
/// file or many, so what a new version holds is seen before it replaces the
/// one on disk.
/// </summary>
internal sealed class FilePickerForm : Form
{
    private readonly FileChecklist _files;
    private readonly Button _download;

    internal List<ReleaseFile> Chosen { get; private set; } = [];

    /// <param name="redownload">The files of the version already on disk: unticked ones will be deleted, and the heading says so.</param>
    internal FilePickerForm(
        Repo repo, Release release, IReadOnlyList<ReleaseFile> files, ISet<string> preChecked, bool redownload = false)
    {
        Text = "Choose Files";
        ShowIcon = false;
        ClientSize = new Size(560, 420);
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(420, 320);
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 9F);

        string version = release.Kind == VersionKind.Commit ? release.Title : release.Tag;
        string date = release.Date is { } when
            ? " (" + when.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ")"
            : string.Empty;

        // The release's own title too, when it says more than the tag: it is
        // often what tells an unrelated release apart from the real one.
        string title = release.Kind != VersionKind.Commit && release.Title.Trim().Length > 0
            && !string.Equals(release.Title.Trim(), release.Tag, StringComparison.OrdinalIgnoreCase)
                ? $"\n\"{release.Title.Trim()}\""
                : string.Empty;

        var heading = new Label
        {
            Text = $"{repo.Name} — {version}{date}{title}\n"
                 + (redownload ? "Choose the files to download again. Unticked files are deleted." : "Choose the files to download:"),
            Location = new Point(16, 12),
            AutoSize = true,
            MaximumSize = new Size(ClientSize.Width - 32, 0),
            ForeColor = Theme.Text
        };
        Controls.Add(heading);

        int listTop = heading.Top + heading.PreferredHeight + 10;

        _files = new FileChecklist
        {
            Location = new Point(16, listTop),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
        };
        _download = new Button { Text = "Download", Size = new Size(96, 30), Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
        _download.Click += (_, _) =>
        {
            Chosen = _files.CheckedFiles;
            DialogResult = DialogResult.OK;
        };

        var cancel = new Button
        {
            Text = "Cancel",
            Size = new Size(96, 30),
            DialogResult = DialogResult.Cancel,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right
        };

        cancel.Location = new Point(ClientSize.Width - 16 - 96, ClientSize.Height - 16 - 30);
        _download.Location = new Point(cancel.Left - 8 - 96, cancel.Top);
        _files.Size = new Size(ClientSize.Width - 32, cancel.Top - 12 - listTop);

        Controls.Add(_files);
        Controls.Add(_download);
        Controls.Add(cancel);

        AcceptButton = _download;
        CancelButton = cancel;

        _files.SelectionChanged += (_, _) => _download.Enabled = _files.CheckedFiles.Count > 0;
        _files.SetFiles(files, preChecked);
    }
}
