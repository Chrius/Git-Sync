using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace GitSync;

/// <summary>
/// Goes through every repo with a newer version, one at a time: for each, the
/// user picks the files and clicks Update, or clicks Ignore to skip it this
/// time (it comes back on the next Update All).
/// </summary>
internal sealed class UpdateAllForm : Form
{
    private enum Step { Pending, Choosing, Downloading, Updated, Ignored, Failed }

    private sealed class Item(Repo repo, Site? site)
    {
        public Repo Repo { get; } = repo;
        public Site? Site { get; } = site;
        public Step Step { get; set; } = Step.Pending;
        public string Error { get; set; } = string.Empty;
    }

    private const int Edge = 16;

    private readonly List<Item> _items;
    private readonly IReadOnlyList<FilterFolder> _filters;
    private readonly ListBox _queue;
    private readonly Label _title;
    private readonly FileChecklist _files;
    private readonly ProgressStrip _progress;
    private readonly Label _note;
    private readonly FlatButton _update;
    private readonly Button _ignore;
    private readonly Button _close;
    private readonly CancellationTokenSource _cancel = new();

    private int _index = -1;
    private Release? _release;
    private bool _busy;

    /// <summary>True when at least one repo was updated, so the main list reloads.</summary>
    internal bool AnyUpdated { get; private set; }

    internal UpdateAllForm(IReadOnlyList<Repo> repos, IReadOnlyList<Site> sites, IReadOnlyList<FilterFolder> filters)
    {
        _filters = filters;
        _items = [.. repos.Select(r => new Item(r, sites.FirstOrDefault(s => s.Id == r.SiteId)))];

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

        _note = new Label
        {
            AutoSize = false,
            Bounds = new Rectangle(Edge, 468, width, 36),
            ForeColor = Theme.DimText
        };
        Controls.Add(_note);

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

        _files.SelectionChanged += (_, _) => _update.Enabled = !_busy && _release is not null && _files.CheckedFiles.Count > 0;
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
            Finish();
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

        try
        {
            _release = await ApiClient.GetReleaseAsync(item.Site, item.Repo, target, _cancel.Token);
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

        if (_release.Files.Count == 0)
        {
            Fail(item, "This version has no files to download.");
            return;
        }

        _files.SetFiles(_release.Files, DownloadPaths.PreChecked(
            _release.Files, _release.ToVersion().DisplayTag, item.Repo.CurrentFiles, item.Repo.Current?.DisplayTag));

        _ignore.Enabled = true;
        _update.Enabled = _files.CheckedFiles.Count > 0;
    }

    /// <summary>The item cannot be updated; only Ignore (moving on) is left.</summary>
    private void Fail(Item item, string error)
    {
        item.Step = Step.Failed;
        item.Error = error;
        _files.SetMessage(error);
        _note.Text = $"{item.Repo.Name}: {error}";
        _note.ForeColor = Theme.Error;
        _ignore.Enabled = true;
        _queue.Invalidate();
    }

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

        _busy = true;
        _update.Enabled = false;
        _ignore.Enabled = false;
        _files.Enabled = false;
        item.Step = Step.Downloading;
        _queue.Invalidate();
        _note.Text = string.Empty;

        // Reports arrive late through the message queue; the last one must not
        // refill the bar after it was reset for the next item.
        bool finished = false;
        var progress = new Progress<DownloadProgress>(value =>
        {
            if (finished)
            {
                return;
            }

            _progress.Value = value.Fraction ?? 0;

            if (value.Fraction is null && value.Bytes > 0)
            {
                _note.ForeColor = Theme.DimText;
                _note.Text = $"Downloading… {FileChecklist.FormatSize(value.Bytes)}";
            }
        });

        try
        {
            // Read again: Current and its files may have changed since the list was built.
            Repo fresh = RepoStore.Get(item.Repo.Id) ?? item.Repo;
            DownloadOutcome outcome = await Downloader.DownloadAsync(
                item.Site, fresh, _filters, _release, chosen, progress, _cancel.Token);

            item.Step = Step.Updated;
            AnyUpdated = true;
            _note.Text = string.Empty;

            if (outcome.Warnings.Count > 0)
            {
                _note.Text = string.Join("\n", outcome.Warnings);
                _note.ForeColor = Theme.Amber;
            }
        }
        catch (ApiException error)
        {
            item.Step = Step.Failed;
            item.Error = error.Message;
            _note.Text = $"{item.Repo.Name}: {error.Message}";
            _note.ForeColor = Theme.Error;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            finished = true;
            _busy = false;
            _files.Enabled = true;
            _progress.Value = 0;
        }

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

    private void Finish()
    {
        int updated = _items.Count(i => i.Step == Step.Updated);
        int ignored = _items.Count(i => i.Step == Step.Ignored);
        int failed = _items.Count(i => i.Step == Step.Failed);

        _title.Text = $"Done: {updated} updated, {ignored} ignored, {failed} failed.";
        _files.SetMessage("Nothing left to go through.");
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

        int statusWidth = 110;
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
            Step.Downloading => ("Downloading", Theme.AccentHover),
            Step.Updated => ("Updated", Theme.Success),
            Step.Ignored => ("Ignored", Theme.DimText),
            Step.Failed => ("Failed", Theme.Error),
            _ => ("Pending", Theme.DimText)
        };

        TextRenderer.DrawText(e.Graphics, text, _queue.Font, status, color, Flags | TextFormatFlags.Right);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_busy)
        {
            DialogResult answer = MessageBox.Show(this,
                "A download is in progress. Stop it and close?", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);

            if (answer != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        _cancel.Cancel();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cancel.Dispose();
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
