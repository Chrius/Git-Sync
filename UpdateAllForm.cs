using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace GitSync;

/// <summary>
/// Goes through every repo with a newer version: for each, the user picks the
/// files and clicks Update, or clicks Ignore to skip it this time (it comes
/// back on the next Update All).
///
/// Update does not wait for the download: the files join a queue that
/// downloads them one repo at a time (see <see cref="DownloadQueue{T}"/>), and
/// the window moves on to the next repo at once. Big releases took minutes
/// each, and the user had to sit through every one of them before choosing
/// the next repo's files.
/// </summary>
internal sealed class UpdateAllForm : Form
{
    private enum Step { Pending, Choosing, Waiting, Downloading, Updated, Ignored, Failed }

    private sealed class Item(Repo repo, Site? site)
    {
        public Repo Repo { get; } = repo;
        public Site? Site { get; } = site;
        public Step Step { get; set; } = Step.Pending;

        /// <summary>Why it failed, or the warnings of a download that worked (old files in use).</summary>
        public string Problem { get; set; } = string.Empty;

        // What Update chose, kept for when its turn in the queue comes.
        public Release? Release { get; set; }
        public List<ReleaseFile> Chosen { get; set; } = [];

        public DownloadProgress? Progress { get; set; }
    }

    private const int Edge = 16;

    private readonly List<Item> _items;
    private readonly IReadOnlyList<FilterFolder> _filters;
    private readonly ListBox _queue;
    private readonly Label _title;
    private readonly FileChecklist _files;
    private readonly ProgressStrip _progress;
    private readonly Label _status;
    private readonly Label _problem;
    private readonly FlatButton _update;
    private readonly Button _ignore;
    private readonly Button _close;
    private readonly ToolTip _tips = new();
    private readonly CancellationTokenSource _cancel = new();
    private readonly DownloadQueue<Item> _downloads;

    private int _index = -1;
    private Release? _release;
    private Item? _downloading;
    private int _tipIndex = -1;

    /// <summary>True when at least one repo was updated, so the main list reloads.</summary>
    internal bool AnyUpdated { get; private set; }

    internal UpdateAllForm(IReadOnlyList<Repo> repos, IReadOnlyList<Site> sites, IReadOnlyList<FilterFolder> filters)
    {
        _filters = filters;
        _items = [.. repos.Select(r => new Item(r, sites.FirstOrDefault(s => s.Id == r.SiteId)))];
        _downloads = new DownloadQueue<Item>(DownloadAsync);
        _downloads.Drained += (_, _) => OnDownloadsDone();

        Text = "Update All";
        ShowIcon = false;
        ClientSize = new Size(620, 560);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 9F);

        int width = ClientSize.Width - (2 * Edge);

        Controls.Add(DarkControls.CreateLabel(
            _items.Count == 1 ? "1 repository has a new version:" : $"{_items.Count} repositories have a new version:",
            Edge, 12));

        var queueFrame = new Panel
        {
            BackColor = Theme.Field,
            Padding = new Padding(2),
            Bounds = new Rectangle(Edge, 34, width, 160)
        };

        _queue = new ListBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Field,
            ForeColor = Theme.Text,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 24,
            IntegralHeight = false,
            SelectionMode = SelectionMode.None,
            TabStop = false
        };
        _queue.DrawItem += DrawQueueItem;
        _queue.MouseMove += (_, e) => ShowRowTip(_queue.IndexFromPoint(e.Location));
        _queue.MouseLeave += (_, _) => ShowRowTip(-1);
        DarkControls.UseDarkScrollbars(_queue);

        foreach (Item item in _items)
        {
            _queue.Items.Add(item);
        }

        queueFrame.Controls.Add(_queue);
        Controls.Add(queueFrame);

        _title = new Label
        {
            AutoSize = false,
            Bounds = new Rectangle(Edge, 206, width, 22),
            Font = new Font("Segoe UI Semibold", 10F),
            ForeColor = Theme.Text,
            AutoEllipsis = true
        };
        Controls.Add(_title);

        _files = new FileChecklist { Bounds = new Rectangle(Edge, 232, width, 214) };
        Controls.Add(_files);

        _progress = new ProgressStrip { Bounds = new Rectangle(Edge, 456, width, 6) };
        Controls.Add(_progress);

        // Two lines: what is downloading now, and the last thing that went
        // wrong. One line for both would let the next download's progress
        // wipe a failure before it was read.
        _status = new Label
        {
            AutoSize = false,
            Bounds = new Rectangle(Edge, 466, width, 18),
            ForeColor = Theme.DimText,
            AutoEllipsis = true
        };
        Controls.Add(_status);

        _problem = new Label
        {
            AutoSize = false,
            Bounds = new Rectangle(Edge, 486, width, 18),
            ForeColor = Theme.Error,
            AutoEllipsis = true
        };
        Controls.Add(_problem);

        int buttonsTop = ClientSize.Height - Edge - 30;

        _close = new Button { Text = "Close", Size = new Size(96, 30), DialogResult = DialogResult.Cancel };
        _close.Location = new Point(ClientSize.Width - Edge - 96, buttonsTop);

        _ignore = new Button { Text = "Ignore", Size = new Size(96, 30), Enabled = false };
        _ignore.Location = new Point(_close.Left - 8 - 96, buttonsTop);
        _ignore.Click += async (_, _) => await IgnoreAsync();

        _update = FlatButton.Accent("Update");
        _update.Size = new Size(96, 30);
        _update.Enabled = false;
        _update.Location = new Point(_ignore.Left - 8 - 96, buttonsTop);
        _update.Click += async (_, _) => await UpdateAsync();

        Controls.Add(_update);
        Controls.Add(_ignore);
        Controls.Add(_close);

        CancelButton = _close;

        _files.SelectionChanged += (_, _) => _update.Enabled = _release is not null && _files.CheckedFiles.Count > 0;
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        await ShowItemAsync(0);
    }

    private static string Describe(VersionRef? version) => version is null ? "—" : version.DisplayTag;

    private async Task ShowItemAsync(int index)
    {
        _index = index;
        _release = null;
        _update.Enabled = false;
        _ignore.Enabled = false;
        _queue.Invalidate();

        if (index >= _items.Count)
        {
            if (_downloads.Remaining > 0)
            {
                ShowWaiting();
            }
            else
            {
                Finish();
            }

            return;
        }

        Item item = _items[index];
        item.Step = Step.Choosing;

        // Keeps the current line in view, with the one before it for context.
        _queue.TopIndex = Math.Max(0, index - 1);
        _queue.Invalidate();

        VersionRef? target = VersionState.Target(item.Repo);
        _title.Text = $"{item.Repo.Name}:  {Describe(item.Repo.Current)}  →  {Describe(target)}";

        if (item.Site is null || target is null)
        {
            Fail(item, "Its site was deleted in Settings.");
            return;
        }

        _files.SetMessage("Loading files…");

        Release release;

        try
        {
            release = await ApiClient.GetReleaseAsync(item.Site, item.Repo, target, _cancel.Token);
        }
        catch (ApiException error)
        {
            Fail(item, error.Message);
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        List<ReleaseFile> files = release.Offered(PreferenceStore.ShowSource());

        if (files.Count == 0)
        {
            Fail(item, "This version has no files to download.");
            return;
        }

        _release = release;
        _files.SetFiles(files, DownloadPaths.PreChecked(
            files, release.ToVersion().DisplayTag, item.Repo.CurrentFiles, item.Repo.Current?.DisplayTag));

        _ignore.Enabled = true;
        _update.Enabled = _files.CheckedFiles.Count > 0;
    }

    /// <summary>The item cannot be updated; only Ignore (moving on) is left.</summary>
    private void Fail(Item item, string error)
    {
        item.Step = Step.Failed;
        item.Problem = error;
        _files.SetMessage(error);
        ShowProblem(item, Theme.Error);
        _ignore.Enabled = true;
        _queue.Invalidate();
    }

    /// <summary>Puts the chosen files in the download queue and moves on to the next repo at once.</summary>
    private async Task UpdateAsync()
    {
        if (_index < 0 || _index >= _items.Count || _release is null)
        {
            return;
        }

        Item item = _items[_index];
        List<ReleaseFile> chosen = _files.CheckedFiles;

        if (chosen.Count == 0 || item.Site is null)
        {
            return;
        }

        item.Release = _release;
        item.Chosen = chosen;
        item.Step = Step.Waiting;
        _downloads.Add(item, _cancel.Token);
        ShowDownloadStatus();

        await ShowItemAsync(_index + 1);
    }

    private async Task IgnoreAsync()
    {
        if (_index < 0 || _index >= _items.Count)
        {
            return;
        }

        Item item = _items[_index];

        if (item.Step != Step.Failed)
        {
            item.Step = Step.Ignored;
        }

        await ShowItemAsync(_index + 1);
    }

    /// <summary>One repo's turn in the queue.</summary>
    private async Task DownloadAsync(Item item, CancellationToken cancel)
    {
        item.Step = Step.Downloading;
        item.Progress = new DownloadProgress(0, 0);
        _downloading = item;
        ShowDownloadStatus();

        // Reports arrive late through the message queue; the last one must not
        // bring back a download that has already ended.
        bool finished = false;
        var progress = new Progress<DownloadProgress>(value =>
        {
            if (!finished)
            {
                item.Progress = value;
                ShowDownloadStatus();
            }
        });

        try
        {
            // Read now, not when Update was clicked: Current and its files may
            // have changed since the list was built.
            Repo fresh = RepoStore.Get(item.Repo.Id) ?? item.Repo;
            DownloadOutcome outcome = await Downloader.DownloadAsync(
                item.Site!, fresh, _filters, item.Release!, item.Chosen, progress, cancel);

            item.Step = Step.Updated;
            AnyUpdated = true;

            if (outcome.Warnings.Count > 0)
            {
                item.Problem = string.Join(" ", outcome.Warnings);
                ShowProblem(item, Theme.Amber);
            }
        }
        catch (ApiException error)
        {
            item.Step = Step.Failed;
            item.Problem = error.Message;
            ShowProblem(item, Theme.Error);
        }
        finally
        {
            finished = true;
            item.Progress = null;
            _downloading = null;
            ShowDownloadStatus();
        }
    }

    private void OnDownloadsDone()
    {
        // Every repo already went through: the downloads were all that was left.
        if (!IsDisposed && _index >= _items.Count)
        {
            Finish();
        }
    }

    private void ShowWaiting()
    {
        _title.Text = "Every repository has been gone through.";
        _files.SetMessage("The chosen files are still downloading.\nThe summary appears here when they are done.");
    }

    /// <summary>The progress line and bar follow the download running now.</summary>
    private void ShowDownloadStatus()
    {
        if (IsDisposed)
        {
            return;
        }

        int waiting = _downloads.Waiting;
        string more = waiting switch
        {
            0 => string.Empty,
            1 => "  ·  1 more waiting",
            _ => $"  ·  {waiting} more waiting"
        };

        if (_downloading is { Progress: var progress } item)
        {
            _status.Text = $"Downloading {item.Repo.Name}: {Amount(progress)}{more}";
            _progress.Value = progress?.Fraction ?? 0;
        }
        else
        {
            _status.Text = string.Empty;
            _progress.Value = 0;
        }

        _queue.Invalidate();
    }

    /// <summary>"45%", or how much has arrived when the server does not say the size.</summary>
    private static string Amount(DownloadProgress? progress) => progress switch
    {
        { Fraction: { } fraction } when fraction > 0 => string.Create(CultureInfo.InvariantCulture, $"{(int)(fraction * 100)}%"),
        { Bytes: > 0 } => FileChecklist.FormatSize(progress.Value.Bytes),
        _ => "starting…"
    };

    private void ShowProblem(Item item, Color color)
    {
        if (IsDisposed)
        {
            return;
        }

        string text = $"{item.Repo.Name}: {item.Problem}";
        _problem.Text = text;
        _problem.ForeColor = color;
        _tips.SetToolTip(_problem, text);
    }

    /// <summary>Hovering a failed row, or one with warnings, shows what happened.</summary>
    private void ShowRowTip(int index)
    {
        if (index == _tipIndex)
        {
            return;
        }

        _tipIndex = index;
        string? text = index >= 0 && index < _items.Count && _items[index].Problem.Length > 0
            ? _items[index].Problem
            : null;

        _tips.SetToolTip(_queue, text);
    }

    private void Finish()
    {
        int updated = _items.Count(i => i.Step == Step.Updated);
        int ignored = _items.Count(i => i.Step == Step.Ignored);
        List<Item> failed = [.. _items.Where(i => i.Step == Step.Failed)];

        _title.Text = $"Done: {updated} updated, {ignored} ignored, {failed.Count} failed.";

        // The failures collected in one place: the problem line only kept the last one.
        const int Shown = 8;
        _files.SetMessage(failed.Count == 0
            ? "Nothing left to go through."
            : "Failed:\n" + string.Join("\n", failed.Take(Shown).Select(i => $"{i.Repo.Name}: {i.Problem}"))
              + (failed.Count > Shown ? $"\n…and {failed.Count - Shown} more (hover their lines above)." : string.Empty));
        _queue.Invalidate();

        AcceptButton = _close;
        _close.Focus();
    }

    private void DrawQueueItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _items.Count)
        {
            return;
        }

        Item item = _items[e.Index];
        bool current = e.Index == _index;

        using (var back = new SolidBrush(current ? Theme.Selection : Theme.Field))
        {
            e.Graphics.FillRectangle(back, e.Bounds);
        }

        const TextFormatFlags Flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                                      TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

        // Wide enough for "Downloading 100%".
        int statusWidth = 130;
        int versionsWidth = 210;
        Rectangle b = e.Bounds;

        var name = new Rectangle(b.X + 8, b.Y, b.Width - statusWidth - versionsWidth - 16, b.Height);
        var versions = new Rectangle(name.Right + 8, b.Y, versionsWidth - 8, b.Height);
        var status = new Rectangle(b.Right - statusWidth, b.Y, statusWidth - 8, b.Height);

        TextRenderer.DrawText(e.Graphics, item.Repo.Name, _queue.Font, name, Theme.Text, Flags);
        TextRenderer.DrawText(e.Graphics,
            $"{Describe(item.Repo.Current)}  →  {Describe(VersionState.Target(item.Repo))}",
            _queue.Font, versions, Theme.DimText, Flags);

        (string text, Color color) = item.Step switch
        {
            Step.Choosing => ("Choose files", Theme.Text),
            Step.Waiting => ("Waiting", Theme.Text),
            Step.Downloading => ("Downloading " + Amount(item.Progress), Theme.AccentHover),
            // Old files that could not be deleted: the line's tooltip says which.
            Step.Updated when item.Problem.Length > 0 => ("Updated", Theme.Amber),
            Step.Updated => ("Updated", Theme.Success),
            Step.Ignored => ("Ignored", Theme.DimText),
            Step.Failed => ("Failed", Theme.Error),
            _ => ("Pending", Theme.DimText)
        };

        TextRenderer.DrawText(e.Graphics, text, _queue.Font, status, color, Flags | TextFormatFlags.Right);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        int unfinished = _downloads.Remaining;

        if (unfinished > 0)
        {
            DialogResult answer = DarkMessageBox.Show(this,
                (unfinished == 1 ? "A download is not finished yet." : $"{unfinished} downloads are not finished yet.")
                + " Stop and close?\n\nRepositories already updated stay updated.",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);

            if (answer != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        // The download running stops and removes its .part files; the waiting ones never start.
        _cancel.Cancel();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cancel.Dispose();
            _tips.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>
/// Thin progress bar in the accent color. The native ProgressBar keeps its
/// green, light theme in dark mode.
/// </summary>
internal sealed class ProgressStrip : Control
{
    private double _value;

    internal ProgressStrip()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Theme.Control;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal double Value
    {
        get => _value;
        set
        {
            _value = Math.Clamp(value, 0, 1);
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Theme.Control);

        using var fill = new SolidBrush(Theme.Accent);
        e.Graphics.FillRectangle(fill, 0, 0, (int)(Width * _value), Height);
    }
}
