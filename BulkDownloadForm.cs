using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GitSync;

/// <summary>
/// "Bulk Download": every version of a repository on the site, newest first,
/// each with its files under it, to tick as many as wanted in one go. What is
/// ticked is handed to <see cref="BulkDownload"/>, which decides where each
/// version goes.
///
/// Files matching the ignored files of Settings > Files are left out, as in
/// the other download windows, until "Show All Files"; so are pre-releases
/// with "Ignore pre-releases". Files already on disk are shown ticked and
/// greyed: they are not downloaded again.
/// </summary>
internal sealed class BulkDownloadForm : Form
{
    /// <summary>A version and the files it offers.</summary>
    private sealed class VersionGroup(Release release, List<ReleaseFile> files, HashSet<ReleaseFile> saved, string marks)
    {
        public Release Release { get; } = release;
        public List<ReleaseFile> Files { get; } = files;

        /// <summary>Files of this version already on disk.</summary>
        public HashSet<ReleaseFile> Saved { get; } = saved;

        /// <summary>"Latest · Current", after the date.</summary>
        public string Marks { get; } = marks;

        public string Name => Release.Kind == VersionKind.Commit ? Release.Title : Release.Tag;
    }

    private abstract record Row(VersionGroup Group);

    private sealed record HeaderRow(VersionGroup Group) : Row(Group);

    /// <param name="Ignored">Matches an ignored-file pattern, shown only because Show All Files is on.</param>
    private sealed record FileRow(VersionGroup Group, ReleaseFile File, bool Ignored) : Row(Group);

    private readonly Repo _repo;
    private readonly Site _site;
    private readonly List<PreviousVersion> _kept;
    private readonly Label _keepNote;
    private readonly Panel _frame;
    private readonly ListBox _list;
    private readonly Label _message;
    private readonly FlatButton _none;
    private readonly FlatButton _showAll;
    private readonly Label _summary;
    private readonly Button _download;
    private readonly Font _bold;
    private readonly CancellationTokenSource _cancel = new();

    private List<VersionGroup> _groups = [];
    private readonly HashSet<ReleaseFile> _ticked = [];
    private FileFilters _filters = FileFilters.None;
    private bool _showingAll;
    private IntPtr _checkTheme;

    /// <summary>The ticked files, by version, newest first. Files already on disk are left out.</summary>
    internal List<BulkChoice> Chosen { get; private set; } = [];

    internal BulkDownloadForm(Repo repo, Site site)
    {
        _repo = repo;
        _site = site;
        _kept = RepoStore.PreviousVersions(repo.Id);

        Text = "Bulk Download";
        ShowIcon = false;
        ClientSize = new Size(640, 560);
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(480, 400);
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 9F);
        _bold = new Font(Font, FontStyle.Bold);

        var heading = new Label
        {
            Text = $"{repo.Name}\nTick the files to download, from as many versions as you like. The newest one "
                 + "goes in the folder itself, the others with the previous versions.",
            Location = new Point(16, 12),
            AutoSize = true,
            MaximumSize = new Size(ClientSize.Width - 32, 0),
            ForeColor = Theme.Text
        };
        Controls.Add(heading);

        _keepNote = new Label
        {
            Text = KeepText(hiddenPrereleases: 0),
            Location = new Point(16, heading.Top + heading.PreferredHeight + 4),
            AutoSize = true,
            MaximumSize = new Size(ClientSize.Width - 32, 0),
            ForeColor = Theme.DimText
        };
        Controls.Add(_keepNote);

        int listTop = _keepNote.Top + _keepNote.PreferredHeight + 10;

        var cancel = new Button
        {
            Text = "Cancel",
            Size = new Size(96, 30),
            DialogResult = DialogResult.Cancel,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right
        };
        cancel.Location = new Point(ClientSize.Width - 16 - 96, ClientSize.Height - 16 - 30);

        _download = new Button
        {
            Text = "Download",
            Size = new Size(96, 30),
            Enabled = false,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right
        };
        _download.Location = new Point(cancel.Left - 8 - 96, cancel.Top);
        _download.Click += (_, _) =>
        {
            Chosen = CurrentChoice();
            DialogResult = DialogResult.OK;
        };

        // Two lines: what will be downloaded, and what the versions to keep leave out.
        _summary = new Label
        {
            AutoSize = false,
            ForeColor = Theme.DimText,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
        };
        _summary.SetBounds(16, cancel.Top - 12 - 2 * Font.Height - 4, ClientSize.Width - 32, 2 * Font.Height + 4);

        _none = new FlatButton
        {
            Text = "None",
            Size = new Size(64, 24),
            Enabled = false,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left
        };
        _none.Location = new Point(16, _summary.Top - 10 - 24);

        // Wide enough for "Show All Files (12 hidden)".
        _showAll = new FlatButton
        {
            Text = "Show All Files",
            Size = new Size(176, 24),
            Enabled = false,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right
        };
        _showAll.Location = new Point(ClientSize.Width - 16 - 176, _none.Top);
        _showAll.Click += (_, _) =>
        {
            _showingAll = !_showingAll;
            Fill();
        };

        _frame = new Panel
        {
            BackColor = Theme.Field,
            Padding = new Padding(4, 4, 2, 4),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
        };
        _frame.SetBounds(16, listTop, ClientSize.Width - 32, _none.Top - 8 - listTop);

        _list = new ListBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Field,
            ForeColor = Theme.Text,
            DrawMode = DrawMode.OwnerDrawVariable,
            IntegralHeight = false,
            Visible = false,
            TabIndex = 0
        };
        _list.MeasureItem += MeasureRow;
        _list.DrawItem += DrawRow;
        _list.MouseDown += (_, e) =>
        {
            int index = _list.IndexFromPoint(e.Location);

            if (e.Button == MouseButtons.Left && index >= 0 && index < _list.Items.Count)
            {
                Toggle(index);
            }
        };
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Space && _list.SelectedIndex >= 0)
            {
                Toggle(_list.SelectedIndex);
                e.SuppressKeyPress = true;
            }
        };

        // The rows have no fixed place to repaint from: a resize redraws them all.
        _list.Resize += (_, _) => _list.Invalidate();
        _list.HandleDestroyed += (_, _) => CloseCheckTheme();
        DarkControls.UseDarkScrollbars(_list);

        _none.Click += (_, _) =>
        {
            _ticked.Clear();
            _list.Invalidate();
            UpdateSummary();
        };

        _message = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Theme.DimText,
            BackColor = Theme.Field,
            Text = "Loading versions…"
        };

        _frame.Controls.Add(_list);
        _frame.Controls.Add(_message);

        Controls.Add(_frame);
        Controls.Add(_none);
        Controls.Add(_showAll);
        Controls.Add(_summary);
        Controls.Add(_download);
        Controls.Add(cancel);

        AcceptButton = _download;
        CancelButton = cancel;
        UpdateSummary();
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);

        List<Release> versions;

        try
        {
            versions = await ApiClient.ListAllVersionsAsync(_site, _repo, _cancel.Token);
        }
        catch (ApiException error)
        {
            if (!IsDisposed)
            {
                _message.Text = error.Message;
            }

            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (IsDisposed)
        {
            return;
        }

        ShowVersions(versions);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _cancel.Cancel();
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cancel.Dispose();
            _bold.Dispose();
        }

        CloseCheckTheme();
        base.Dispose(disposing);
    }

    private string KeepText(int hiddenPrereleases)
    {
        string text = _repo.KeepVersions > 0
            ? $"This repository keeps {_repo.KeepVersions} versions, the one in its folder included: "
              + "picked versions older than that are skipped."
            : "This repository keeps every version.";

        return hiddenPrereleases switch
        {
            0 => text,
            1 => text + "\n1 pre-release is hidden (Ignore pre-releases, in Edit).",
            _ => text + $"\n{hiddenPrereleases} pre-releases are hidden (Ignore pre-releases, in Edit)."
        };
    }

    /// <summary>The versions arrived: grouped with their files, what is on disk marked.</summary>
    private void ShowVersions(List<Release> versions)
    {
        // "Ignore pre-releases" as in the Versions menu: left out, except one
        // on disk or pinned, so what the row shows can still be found here.
        bool OnDiskOrPinned(Release release)
        {
            VersionRef version = release.ToVersion();
            return (_repo.Current is { } current && BulkDownload.SameBuild(current, version))
                || _kept.Any(k => BulkDownload.SameBuild(k.Version, version))
                || (_repo.Selected is { } pinned && VersionState.SameVersion(pinned, version));
        }

        int hidden = 0;

        if (_repo.IgnorePrereleases)
        {
            hidden = versions.Count(v => v.Prerelease && !OnDiskOrPinned(v));
            versions = [.. versions.Where(v => !v.Prerelease || OnDiskOrPinned(v))];
        }

        Release? latest = ReleaseParser.PickLatest(versions, _repo.IgnorePrereleases)
            ?? (_repo.IgnorePrereleases ? null : versions.FirstOrDefault());

        bool showSource = PreferenceStore.ShowSource();
        _groups = [.. versions.Select(release => Group(release, release.Offered(showSource), ReferenceEquals(release, latest)))];
        _filters = FileFilters.Saved();

        _keepNote.Text = KeepText(hidden);
        PlaceList();

        if (_groups.Count == 0)
        {
            _message.Text = hidden > 0 ? "No stable versions on the site." : "No versions on the site.";
            return;
        }

        _message.Visible = false;
        _list.Visible = true;
        Fill();
        _list.Focus();
    }

    /// <summary>
    /// The list starts below the note above it, which gains a line once the
    /// versions arrive with pre-releases hidden: placed for one line, the
    /// second ran into the list.
    /// </summary>
    private void PlaceList()
    {
        int top = _keepNote.Top + _keepNote.PreferredHeight + 10;
        _frame.SetBounds(_frame.Left, top, _frame.Width, _frame.Bottom - top);
    }

    private VersionGroup Group(Release release, List<ReleaseFile> files, bool isLatest)
    {
        VersionRef version = release.ToVersion();
        string tag = version.DisplayTag;
        var marks = new List<string>();
        IReadOnlyList<string> onDisk = [];

        if (isLatest)
        {
            marks.Add("Latest");
        }

        if (_repo.Current is { } current && BulkDownload.SameBuild(current, version))
        {
            marks.Add("Current");
            onDisk = _repo.CurrentFiles;
        }
        else if (_kept.FirstOrDefault(k => BulkDownload.SameBuild(k.Version, version)) is { } kept)
        {
            marks.Add("Kept");
            onDisk = kept.Files;
        }

        if (_repo.Selected is { } pinned && VersionState.SameVersion(pinned, version))
        {
            marks.Add("Selected");
        }

        if (release.Prerelease)
        {
            marks.Add("Pre-release");
        }

        if (release.Kind == VersionKind.ReleaseSource)
        {
            marks.Add("Source");
        }

        // A file recorded but deleted by hand since is not on disk: it can be downloaded again.
        List<string> present = [.. onDisk.Where(f => File.Exists(DownloadPaths.ToAbsolute(f)))];
        var saved = new HashSet<ReleaseFile>(files.Where(file => present.Any(p => BulkDownload.IsSavedAs(p, file.Name, tag))));

        return new VersionGroup(release, files, saved, string.Join(" · ", marks));
    }

    /// <summary>The rows again, after the versions arrived or Show All Files; ticks and scroll stay.</summary>
    private void Fill()
    {
        int top = _list.TopIndex;
        int hidden = 0;

        _list.BeginUpdate();
        _list.Items.Clear();

        foreach (VersionGroup group in _groups)
        {
            _list.Items.Add(new HeaderRow(group));

            foreach (ReleaseFile file in group.Files)
            {
                bool ignored = _filters.Hides(file.Name);
                hidden += ignored ? 1 : 0;

                if (!ignored || _showingAll)
                {
                    _list.Items.Add(new FileRow(group, file, ignored));
                }
            }
        }

        _list.EndUpdate();
        _list.TopIndex = Math.Min(top, Math.Max(0, _list.Items.Count - 1));

        _none.Enabled = true;
        _showAll.Enabled = hidden > 0;
        _showAll.Text = _showingAll ? "Hide Ignored Files"
            : hidden > 0 ? $"Show All Files ({hidden} hidden)"
            : "Show All Files";

        UpdateSummary();
    }

    private List<ReleaseFile> ShownFiles(VersionGroup group) =>
        _showingAll ? group.Files : [.. group.Files.Where(f => !_filters.Hides(f.Name))];

    /// <summary>Files that can be ticked: shown, and not on disk already.</summary>
    private List<ReleaseFile> Tickable(VersionGroup group) => [.. ShownFiles(group).Where(f => !group.Saved.Contains(f))];

    /// <summary>A header ticks or unticks every file of its version; a file only itself.</summary>
    private void Toggle(int index)
    {
        switch (_list.Items[index])
        {
            case HeaderRow header:
                List<ReleaseFile> files = Tickable(header.Group);

                if (files.Count > 0 && files.All(_ticked.Contains))
                {
                    _ticked.ExceptWith(files);
                }
                else
                {
                    _ticked.UnionWith(files);
                }

                break;

            case FileRow row when !row.Group.Saved.Contains(row.File):
                if (!_ticked.Remove(row.File))
                {
                    _ticked.Add(row.File);
                }

                break;

            default:
                return;
        }

        _list.Invalidate();
        UpdateSummary();
    }

    /// <summary>The ticked files that would be downloaded: shown ones only, as in the other download windows.</summary>
    private List<BulkChoice> CurrentChoice() =>
        [.. _groups
            .Select(group => new BulkChoice(group.Release, [.. Tickable(group).Where(_ticked.Contains)]))
            .Where(choice => choice.Files.Count > 0)];

    /// <summary>
    /// What Download would do, worked out as it will be: the versions to keep
    /// can leave picked versions out, and that is said before, not after.
    /// </summary>
    private void UpdateSummary()
    {
        List<BulkChoice> chosen = CurrentChoice();
        BulkPlan plan = BulkDownload.Plan(chosen, _repo.Current, _kept, _repo.KeepVersions);
        int files = plan.Download.Sum(c => c.Files.Count);

        string text;
        Color color = Theme.DimText;

        if (_groups.Count == 0)
        {
            text = string.Empty;
        }
        else if (chosen.Count == 0)
        {
            text = "Nothing ticked. Ticking a version ticks all of its files.";
        }
        else
        {
            text = plan.Download.Count == 0
                ? "Nothing to download."
                : $"{Count(plan.Download.Count, "version")}, {Count(files, "file")} to download.";

            if (plan.Skipped.Count > 0)
            {
                string skipped = string.Join(", ", plan.Skipped.Select(c => c.Release.ToVersion().DisplayTag));
                text += $"\nSkipped, older than the {_repo.KeepVersions} versions kept: {skipped}";
                color = Theme.Amber;
            }
        }

        _summary.Text = text;
        _summary.ForeColor = color;
        _download.Enabled = plan.Download.Count > 0;
    }

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    // ---------- drawing ----------

    private int Scaled(int value) => _list.LogicalToDeviceUnits(value);

    private void MeasureRow(object? sender, MeasureItemEventArgs e)
    {
        // A version's heading has room above it, which sets the groups apart.
        bool header = e.Index >= 0 && e.Index < _list.Items.Count && _list.Items[e.Index] is HeaderRow;
        e.ItemHeight = _list.Font.Height + Scaled(header ? (e.Index == 0 ? 10 : 18) : 6);
    }

    private void DrawRow(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _list.Items.Count)
        {
            return;
        }

        Graphics g = e.Graphics;
        Rectangle bounds = e.Bounds;
        bool selected = (e.State & DrawItemState.Selected) != 0 && _list.Focused;

        using (var back = new SolidBrush(Theme.Field))
        {
            g.FillRectangle(back, bounds);
        }

        Row row = (Row)_list.Items[e.Index];
        bool header = row is HeaderRow;

        // The row's own band, under the room a heading keeps above it.
        int lineHeight = _list.Font.Height + Scaled(6);
        var band = new Rectangle(bounds.X, bounds.Bottom - lineHeight, bounds.Width, lineHeight);

        if (header && e.Index > 0)
        {
            using var line = new Pen(Theme.Separator);
            int y = bounds.Top + (band.Top - bounds.Top) / 2;
            g.DrawLine(line, bounds.Left + Scaled(4), y, bounds.Right - Scaled(4), y);
        }

        if (selected)
        {
            using var highlight = new SolidBrush(Theme.Selection);
            g.FillRectangle(highlight, band);
        }

        int x = band.X + Scaled(header ? 4 : 26);
        Size box = DrawCheck(g, new Point(x, band.Y), band.Height, CheckOf(row));
        x += box.Width + Scaled(6);

        const TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;

        if (row is HeaderRow { Group: var group })
        {
            int width = TextRenderer.MeasureText(g, group.Name, _bold, Size.Empty, flags | TextFormatFlags.NoPadding).Width;
            TextRenderer.DrawText(g, group.Name, _bold, new Rectangle(x, band.Y, Math.Max(0, band.Right - x), band.Height),
                Theme.Text, flags | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

            string details = Details(group);
            int detailsLeft = x + width + Scaled(12);

            if (details.Length > 0 && detailsLeft < band.Right)
            {
                TextRenderer.DrawText(g, details, _list.Font, new Rectangle(detailsLeft, band.Y, band.Right - detailsLeft - Scaled(4), band.Height),
                    Theme.DimText, flags | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            }

            return;
        }

        var file = (FileRow)row;
        bool saved = file.Group.Saved.Contains(file.File);
        string note = (file.File.Size > 0 ? FileChecklist.FormatSize(file.File.Size)
                : file.File.IsSource ? "source code" : string.Empty)
            + (saved ? "    (Downloaded)" : file.Ignored ? "    · ignored" : string.Empty);

        int nameWidth = TextRenderer.MeasureText(g, file.File.Name, _list.Font, Size.Empty, flags | TextFormatFlags.NoPadding).Width;
        TextRenderer.DrawText(g, file.File.Name, _list.Font, new Rectangle(x, band.Y, Math.Max(0, band.Right - x), band.Height),
            saved ? Theme.DimText : Theme.Text, flags | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

        int noteLeft = x + nameWidth + Scaled(16);

        if (note.Length > 0 && noteLeft < band.Right)
        {
            TextRenderer.DrawText(g, note, _list.Font, new Rectangle(noteLeft, band.Y, band.Right - noteLeft - Scaled(4), band.Height),
                Theme.DimText, flags | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }
    }

    /// <summary>"2026-11-10   "Title"   Latest · Current".</summary>
    private static string Details(VersionGroup group)
    {
        Release release = group.Release;
        var parts = new List<string>();

        if (release.Date is { } date)
        {
            parts.Add(date.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        // The release's own title, when it says more than the tag.
        string title = release.Title.Trim();

        if (release.Kind != VersionKind.Commit && title.Length > 0 && !string.Equals(title, release.Tag, StringComparison.OrdinalIgnoreCase))
        {
            parts.Add($"\"{title}\"");
        }

        if (group.Marks.Length > 0)
        {
            parts.Add(group.Marks);
        }

        if (group.Files.Count == 0)
        {
            parts.Add("no files");
        }

        return string.Join("    ", parts);
    }

    private enum Check
    {
        Off,
        On,
        Some,
        OffDisabled,
        OnDisabled
    }

    /// <summary>
    /// A file on disk is ticked and greyed: it is there already. A heading
    /// shows whether all, some or none of its version's files are ticked.
    /// </summary>
    private Check CheckOf(Row row)
    {
        if (row is FileRow file)
        {
            return file.Group.Saved.Contains(file.File) ? Check.OnDisabled
                : _ticked.Contains(file.File) ? Check.On
                : Check.Off;
        }

        List<ReleaseFile> tickable = Tickable(row.Group);

        if (tickable.Count == 0)
        {
            return ShownFiles(row.Group).Count > 0 ? Check.OnDisabled : Check.OffDisabled;
        }

        int ticked = tickable.Count(_ticked.Contains);
        return ticked == 0 ? Check.Off : ticked == tickable.Count ? Check.On : Check.Some;
    }

    // ---------- the theme's own check boxes ----------

    private const int ButtonCheckBox = 3;     // BP_CHECKBOX
    private const int DrawSize = 2;           // TS_DRAW

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect(int left, int top, int right, int bottom)
    {
        public int Left = left, Top = top, Right = right, Bottom = bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Width, Height;
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenThemeData(IntPtr window, string classList);

    [DllImport("uxtheme.dll")]
    private static extern int CloseThemeData(IntPtr theme);

    [DllImport("uxtheme.dll")]
    private static extern int DrawThemeBackground(IntPtr theme, IntPtr dc, int part, int state, ref NativeRect rect, IntPtr clip);

    [DllImport("uxtheme.dll")]
    private static extern int GetThemePartSize(IntPtr theme, IntPtr dc, int part, int state, IntPtr rect, int size, out NativeSize result);

    /// <summary>
    /// The check box as the other lists draw theirs. Asked of the list's own
    /// window, which has the dark Explorer theme (for its scrollbars): the
    /// plain "Button" theme would draw the light Windows box on the dark list.
    /// </summary>
    private Size DrawCheck(Graphics g, Point topLeft, int rowHeight, Check check)
    {
        if (_checkTheme == IntPtr.Zero && _list.IsHandleCreated)
        {
            _checkTheme = OpenThemeData(_list.Handle, "Button");
        }

        int state = check switch
        {
            Check.On => 5,              // CBS_CHECKEDNORMAL
            Check.Some => 9,            // CBS_MIXEDNORMAL
            Check.OffDisabled => 4,     // CBS_UNCHECKEDDISABLED
            Check.OnDisabled => 8,      // CBS_CHECKEDDISABLED
            _ => 1                      // CBS_UNCHECKEDNORMAL
        };

        int side = Scaled(13);

        if (_checkTheme != IntPtr.Zero)
        {
            IntPtr dc = g.GetHdc();

            try
            {
                if (GetThemePartSize(_checkTheme, dc, ButtonCheckBox, state, IntPtr.Zero, DrawSize, out NativeSize size) == 0
                    && size.Width > 0)
                {
                    side = size.Width;
                }

                int top = topLeft.Y + (rowHeight - side) / 2;
                var rect = new NativeRect(topLeft.X, top, topLeft.X + side, top + side);
                DrawThemeBackground(_checkTheme, dc, ButtonCheckBox, state, ref rect, IntPtr.Zero);
            }
            finally
            {
                g.ReleaseHdc(dc);
            }

            return new Size(side, side);
        }

        ButtonState fallback = check switch
        {
            Check.On => ButtonState.Checked,
            Check.Some => ButtonState.Checked | ButtonState.Inactive,
            Check.OnDisabled => ButtonState.Checked | ButtonState.Inactive,
            Check.OffDisabled => ButtonState.Inactive,
            _ => ButtonState.Normal
        } | ButtonState.Flat;

        ControlPaint.DrawCheckBox(g, topLeft.X, topLeft.Y + (rowHeight - side) / 2, side, side, fallback);
        return new Size(side, side);
    }

    private void CloseCheckTheme()
    {
        if (_checkTheme != IntPtr.Zero)
        {
            CloseThemeData(_checkTheme);
            _checkTheme = IntPtr.Zero;
        }
    }
}
