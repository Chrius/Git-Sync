using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace GitSync;

internal sealed class MainForm : Form
{
    private const int TreeWidth = 250;
    private const int FooterHeight = 60;
    private const int ButtonGap = 6;
    private const int RefreshParallel = 4;

    private readonly SplitContainer _split;
    private readonly FilterTree _tree;
    private readonly IconButton _settings;
    private readonly IconButton _add;
    private readonly IconButton _refresh;
    private readonly IconButton _updateAll;
    private readonly RepoList _list;
    private readonly ComboBox _sort;
    private readonly SearchBox _search;
    private readonly Label _status;

    /// <summary>
    /// Waits for a pause in the typing before filtering: every change rebuilds
    /// the rows, and with a long list one rebuild per key would lag behind the
    /// keyboard.
    /// </summary>
    private readonly System.Windows.Forms.Timer _searchTimer = new() { Interval = 200 };

    /// <summary>The words the list was last filtered by, to skip rebuilding it for the same ones.</summary>
    private string _appliedSearch = string.Empty;

    /// <summary>The "Sort by" choices, in the order of the drop down.</summary>
    private static readonly (RepoOrder Order, string Text)[] Orders =
    [
        (RepoOrder.Name, "Name"),
        (RepoOrder.NotDownloadedFirst, "Not Downloaded on Top"),
        (RepoOrder.UpdatesFirst, "Updates on Top")
    ];

    private RepoOrder _order = RepoOrder.Name;
    private readonly ToolTip _tips = new();

    /// <summary>Cancelled when the window closes, stopping checks and downloads.</summary>
    private readonly CancellationTokenSource _closing = new();

    /// <summary>Downloads running from the rows, with their progress. Survives the list being rebuilt.</summary>
    private readonly Dictionary<long, DownloadProgress> _downloads = [];

    private List<Site> _sites = [];
    private List<FilterFolder> _filters = [];
    private List<Repo> _repos = [];
    private VersionMenu? _menu;
    private ContextMenuStrip? _refreshMenu;
    private bool _refreshing;

    /// <summary>Cancels the check that is running: what the Refresh button does while it shows Stop.</summary>
    private CancellationTokenSource? _refreshCancel;

    /// <summary>
    /// After the user stops a check, the automatic one waits a whole period
    /// instead of starting again on the next tick — the stopped check did not
    /// complete the period, so it would otherwise be due at once. Only in
    /// memory: after a restart an overdue check runs as usual.
    /// </summary>
    private DateTimeOffset _autoHeldUntil = DateTimeOffset.MinValue;

    /// <summary>
    /// Looks every 30 seconds whether the automatic check is due. A short
    /// tick instead of one long timer: the period counts from the last full
    /// check, which a manual Refresh can move at any moment.
    /// </summary>
    private readonly System.Windows.Forms.Timer _autoTimer = new() { Interval = 30_000 };

    private AutoCheckSettings _auto = new(false, AutoCheck.DefaultMinutes, null);

    /// <summary>True while a dropped or pasted link's server is being checked.</summary>
    private bool _addingLink;

    /// <summary>True while the tree is rebuilt: its selection events are not the user's.</summary>
    private bool _loadingTree;

    /// <summary>A newer Git-Sync found at launch, waiting for this window to be the active one.</summary>
    private AppRelease? _appUpdate;

    internal MainForm()
    {
        // The version of this exe, the same one the update check compares:
        // which copy is open shows at a glance when several are around.
        Text = "Git-Sync v" + AppUpdate.Display(AppUpdate.Running);
        Icon = ExeIcon();
        ClientSize = new Size(1180, 700);
        MinimumSize = new Size(1000, 480);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 9F);

        _split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel1,
            SplitterWidth = 1,

            // The splitter shows the container's own color: a one-pixel line.
            BackColor = Theme.Separator,
            TabStop = false
        };
        _split.Panel1.BackColor = Theme.Surface;
        _split.Panel2.BackColor = Theme.Background;

        // ----- left: tree and buttons -----

        _tree = new FilterTree { IncludeAll = true, Dock = DockStyle.Fill };
        _tree.AfterSelect += (_, _) =>
        {
            if (!_loadingTree)
            {
                ShowRepos(keepScroll: false);
            }
        };
        _tree.ExpandedChanged += (folder, expanded) => FilterStore.SetExpanded(folder.Id, expanded);

        var treeBox = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Surface,
            Padding = new Padding(0, 8, 0, 0)
        };
        treeBox.Controls.Add(_tree);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = FooterHeight, BackColor = Theme.Surface };

        _settings = AddIconButton(footer, IconButton.GearIcon, "Settings", 0, OpenSettings);
        _add = AddIconButton(footer, IconButton.PlusIcon, "Add repository", 1, (_, _) => AddRepo());
        _refresh = AddIconButton(footer, IconButton.RefreshIcon, "Refresh: check for new versions", 2, (_, _) => RefreshClicked());
        _updateAll = AddIconButton(footer, IconButton.UpdateAllIcon, "Update All", 3, (_, _) => OpenUpdateAll());

        // Fill first: with docking, whoever is added later sits closer to the edge.
        _split.Panel1.Controls.Add(treeBox);
        _split.Panel1.Controls.Add(footer);

        // ----- right: list and status line -----

        _list = new RepoList { Dock = DockStyle.Fill };
        _list.ActionClicked += (_, repo) => DownloadRepo(repo);
        _list.EditRequested += (_, repo) => EditRepo(repo);
        _list.MenuRequested += (_, request) => ShowMenu(request.Repo, request.ScreenPoint);

        _status = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 28,
            AutoSize = false,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(14, 0, 14, 0),
            BackColor = Theme.Background,
            ForeColor = Theme.DimText
        };

        var statusLine = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Theme.Separator };

        // ----- above the list: search and Sort by -----

        var header = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Theme.Background };
        var headerLine = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Theme.Separator };

        _sort = DarkControls.CreateList(0);
        _sort.Width = 200;
        _sort.TabStop = false;
        _sort.Items.AddRange([.. Orders.Select(o => o.Text)]);
        _sort.SelectedIndexChanged += (_, _) => OrderChanged();

        Label sortLabel = DarkControls.CreateLabel("Sort by", 0, 0);
        sortLabel.ForeColor = Theme.DimText;

        _search = new SearchBox("Search name or repository");
        _search.QueryChanged += (_, _) => SearchChanged();
        _search.Submitted += (_, _) => ApplySearch();
        _searchTimer.Tick += (_, _) => ApplySearch();

        // Sort by right-aligned, over the version columns and the buttons it
        // reorders; the search takes the rest, from the names' edge.
        header.Resize += (_, _) =>
        {
            _sort.Location = new Point(header.ClientSize.Width - 14 - _sort.Width, (header.ClientSize.Height - _sort.Height) / 2);
            sortLabel.Location = new Point(_sort.Left - 8 - sortLabel.Width, (header.ClientSize.Height - sortLabel.Height) / 2);
            _search.SetBounds(16, _sort.Top, Math.Max(0, sortLabel.Left - 20 - 16), _sort.Height);
        };

        header.Controls.Add(_search);
        header.Controls.Add(_sort);
        header.Controls.Add(sortLabel);

        // Fill first, then the edges; the later one of two Top panels sits higher.
        _split.Panel2.Controls.Add(_list);
        _split.Panel2.Controls.Add(statusLine);
        _split.Panel2.Controls.Add(_status);
        _split.Panel2.Controls.Add(headerLine);
        _split.Panel2.Controls.Add(header);

        Controls.Add(_split);

        // Only the form registers for drops. Its children do not, and Windows
        // hands a drop over a child that does not take drops to the nearest
        // parent that does — so the whole window, rows included, accepts a link.
        AllowDrop = true;
    }

    private IconButton AddIconButton(Panel footer, Image icon, string tip, int index, EventHandler click)
    {
        var button = new IconButton(icon) { BackColor = Theme.Surface };
        button.Location = new Point(12 + (index * (button.Width + ButtonGap)), (FooterHeight - button.Height) / 2);
        button.Click += click;
        _tips.SetToolTip(button, tip);
        footer.Controls.Add(button);
        return button;
    }

    /// <summary>The exe's own icon, so the title bar always matches what Explorer shows.</summary>
    private static Icon? ExeIcon()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            return string.IsNullOrEmpty(exe) ? null : Icon.ExtractAssociatedIcon(exe);
        }
        catch
        {
            return null;
        }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        // Set here and not in the constructor: SplitContainer checks the
        // distance against its size, which is only real once the form is laid out.
        _split.Panel1MinSize = 200;
        _split.SplitterDistance = TreeWidth;

        // The order chosen last time. Set before the event can fire a reload:
        // the lists are still empty here.
        _order = RepoSorting.Parse(PreferenceStore.Get(PreferenceStore.OrderKey));
        _loadingTree = true;
        _sort.SelectedIndex = Array.FindIndex(Orders, o => o.Order == _order);
        _loadingTree = false;

        _auto = AutoCheck.Load();
        ReloadAll(keepScroll: false);

        _autoTimer.Tick += (_, _) => AutoTick();
        _autoTimer.Start();
    }

    /// <summary>A check that fell due while the app was closed runs as soon as the window is up.</summary>
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        AutoTick();
        CheckAppUpdate();
    }

    // ---------- Git-Sync's own updates ----------

    /// <summary>
    /// "Check for updates on launch": asks GitHub once, in the background, and
    /// says nothing when it cannot be reached — being offline is no reason to
    /// interrupt the start, and the next launch asks again.
    /// </summary>
    private async void CheckAppUpdate()
    {
        if (!AppUpdate.Enabled())
        {
            return;
        }

        AppRelease? latest;

        try
        {
            latest = await AppUpdate.LatestAsync(_closing.Token);
        }
        catch (Exception error) when (error is ApiException or OperationCanceledException)
        {
            return;
        }

        if (IsDisposed || latest is null || !AppUpdate.ShouldOffer(latest, AppUpdate.Running, AppUpdate.Skipped()))
        {
            return;
        }

        _appUpdate = latest;
        OfferAppUpdate();
    }

    /// <summary>The window became active again: a dialog over it closed, or it was restored.</summary>
    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);

        // After the activation is over: a modal box opened from inside it
        // would start while Windows is still moving the focus.
        if (_appUpdate is not null)
        {
            BeginInvoke(OfferAppUpdate);
        }
    }

    /// <summary>
    /// Not while a dialog or message box is open (the window is disabled
    /// then, so CanFocus is false): the answer can arrive after Settings or
    /// Add was opened, and a second box would pile on top of it. Not while
    /// minimized either, where the box would open centered on nothing. It
    /// then waits for the window to be activated again.
    /// </summary>
    private void OfferAppUpdate()
    {
        if (_appUpdate is not { } update || IsDisposed || !CanFocus || WindowState == FormWindowState.Minimized)
        {
            return;
        }

        _appUpdate = null;

        DialogResult answer = DarkMessageBox.Show(this,
            $"A new version of Git-Sync is available: {AppUpdate.Display(update.Version)}.\n"
            + $"You have {AppUpdate.Display(AppUpdate.Running)}.\n\n"
            + "Do you want to download it?",
            "Git-Sync", [("Yes", DialogResult.Yes), ("No", DialogResult.No), ("Skip update", DialogResult.Ignore)],
            MessageBoxIcon.Information);

        // No (or Escape, or the X) asks again on the next launch.
        if (answer == DialogResult.Yes)
        {
            try
            {
                Process.Start(new ProcessStartInfo(AppUpdate.ReleasesPage) { UseShellExecute = true });
            }
            catch (Exception error)
            {
                Warn("Could not open the browser.\n" + error.Message);
            }
        }
        else if (answer == DialogResult.Ignore)
        {
            AppUpdate.Skip(update);
        }
    }

    /// <summary>
    /// Starts the automatic check when it is on and due. Not while a dialog or
    /// message box is open (the window is disabled then, so CanFocus is
    /// false): that check waits for the next tick after it closes.
    /// </summary>
    private void AutoTick()
    {
        if (!_auto.Enabled || _refreshing || IsDisposed || !CanFocus || DateTimeOffset.UtcNow < _autoHeldUntil)
        {
            return;
        }

        if (AutoCheck.IsDue(_auto.LastFullCheck, _auto.Minutes, DateTimeOffset.UtcNow))
        {
            RefreshRepos(RepoStore.List(), everything: true);
        }
    }

    // ---------- loading ----------

    /// <summary>Reads everything again. The way back from Settings, Edit and Update All.</summary>
    private void ReloadAll(bool keepScroll)
    {
        long? selected = FilterTree.FolderOf(_tree.SelectedNode)?.Id;

        // A detected server whose last repo was deleted or moved goes away here.
        SiteStore.RemoveUnused();

        _sites = SiteStore.List();
        _filters = FilterStore.List();
        _repos = RepoStore.List();

        _loadingTree = true;
        _tree.Load(_filters, selected);
        _loadingTree = false;

        ShowRepos(keepScroll);
    }

    /// <summary>
    /// Lists the repos of the selected folder and of every folder below it, so
    /// "Games" also shows what is filed under Nintendo › NES — and of those,
    /// only what the search box matches. The search never reaches outside the
    /// selected folder; with "All" selected it covers everything.
    /// </summary>
    private void ShowRepos(bool keepScroll)
    {
        FilterFolder? folder = FilterTree.FolderOf(_tree.SelectedNode);
        _appliedSearch = string.Join(' ', RepoSearch.Words(_search.Query));
        List<Repo> inFolder = VisibleRepos(_repos);
        List<Repo> shown = RepoSearch.Filter(inFolder, _appliedSearch);

        string empty = inFolder.Count > 0 && shown.Count == 0
            ? $"No repositories{InFolder(folder)} match \"{_appliedSearch}\"."
            : folder is not null
                ? $"No repositories in \"{folder.Name}\"."
                : "No repositories yet. Click + to add one.";

        _list.ShowRepos(RepoSorting.Sort(shown, _order), _downloads, empty, keepScroll);
        UpdateSummary();
    }

    private static string InFolder(FilterFolder? folder) => folder is null ? string.Empty : $" in \"{folder.Name}\"";

    private static string Repositories(int count) => count == 1 ? "1 repository" : $"{count} repositories";

    /// <summary>
    /// Filters once the typing pauses. Clearing the box shows everything again
    /// at once: there is nothing more to type then.
    /// </summary>
    private void SearchChanged()
    {
        _searchTimer.Stop();

        if (RepoSearch.Words(_search.Query).Length == 0)
        {
            ApplySearch();
        }
        else
        {
            _searchTimer.Start();
        }
    }

    private void ApplySearch()
    {
        _searchTimer.Stop();

        // A space, or a change undone before the pause, leaves the same words:
        // the list stays as it is, scroll position included.
        string words = string.Join(' ', RepoSearch.Words(_search.Query));

        if (!IsDisposed && words != _appliedSearch)
        {
            ShowRepos(keepScroll: false);
        }
    }

    private void OrderChanged()
    {
        if (_loadingTree || _sort.SelectedIndex < 0)
        {
            return;
        }

        _order = Orders[_sort.SelectedIndex].Order;
        PreferenceStore.Set(PreferenceStore.OrderKey, _order.ToString());
        ShowRepos(keepScroll: false);
    }

    /// <summary>
    /// After a refresh, a download or a pin, a repo may now belong to the
    /// other group. Called once the action is over, not row by row, so rows
    /// do not jump around under the mouse while a refresh is still running.
    /// </summary>
    private void Resort()
    {
        if (_order != RepoOrder.Name && !IsDisposed)
        {
            ShowRepos(keepScroll: true);
        }
    }

    /// <summary>The repos of the selected folder and its subfolders, or all of them with "All" selected.</summary>
    private List<Repo> VisibleRepos(List<Repo> repos) =>
        RepoSearch.InFolder(repos, FilterTree.FolderOf(_tree.SelectedNode)?.Id, _filters);

    /// <summary>Reads one repo again after a check, a download or a pin, and redraws only its row.</summary>
    private void ReloadRepo(long id)
    {
        if (IsDisposed)
        {
            return;
        }

        Repo? repo = RepoStore.Get(id);
        int index = _repos.FindIndex(r => r.Id == id);

        if (repo is null)
        {
            return;
        }

        if (index >= 0)
        {
            _repos[index] = repo;
        }

        _list.UpdateRepo(repo);
        UpdateAllButton();
    }

    private Site? SiteOf(Repo repo) => _sites.FirstOrDefault(s => s.Id == repo.SiteId);

    /// <summary>Repos Update All would go through: not the ones already downloading from their row.</summary>
    private List<Repo> PendingUpdates() =>
        [.. VersionState.PendingUpdates(_repos).Where(r => !_downloads.ContainsKey(r.Id))];

    private void UpdateAllButton()
    {
        int count = PendingUpdates().Count;
        _updateAll.Enabled = count > 0;
        _tips.SetToolTip(_updateAll, count switch
        {
            0 => "Update All: nothing to update",
            1 => "Update All: 1 repository",
            _ => $"Update All: {count} repositories"
        });
    }

    /// <param name="note">Said first, before the counts: how a stopped check ended.</param>
    private void UpdateSummary(string? note = null)
    {
        UpdateAllButton();

        if (_refreshing)
        {
            return;
        }

        // Counts what the list shows: the selected folder with its subfolders,
        // or everything under "All", narrowed by the search. (Update All still
        // covers every repo.)
        List<Repo> inFolder = VisibleRepos(_repos);
        List<Repo> shown = RepoSearch.Filter(inFolder, _appliedSearch);
        FilterFolder? folder = FilterTree.FolderOf(_tree.SelectedNode);
        string where = InFolder(folder);

        int updates = VersionState.PendingUpdates(shown).Count;
        int failed = shown.Count(r => r.Error.Length > 0);

        string text = (inFolder.Count, shown.Count) switch
        {
            (0, _) when folder is null => "Click + to add a repository.",
            (0, _) => $"No repositories{where}.",
            (_, 0) => $"No repositories{where} match \"{_appliedSearch}\"",
            _ when _appliedSearch.Length > 0 =>
                $"{shown.Count} of {Repositories(inFolder.Count)}{where} {(shown.Count == 1 ? "matches" : "match")} \"{_appliedSearch}\"",
            (1, _) => $"1 repository{where}",
            _ => $"{inFolder.Count} repositories{where}"
        };

        if (updates > 0)
        {
            text += updates == 1 ? " · 1 update available" : $" · {updates} updates available";
        }

        if (failed > 0)
        {
            text += failed == 1 ? " · 1 could not be checked" : $" · {failed} could not be checked";
        }

        if (_auto.Enabled)
        {
            DateTimeOffset next = AutoCheck.NextCheck(_auto.LastFullCheck, _auto.Minutes, DateTimeOffset.Now).ToLocalTime();

            if (_autoHeldUntil > next)
            {
                next = _autoHeldUntil.ToLocalTime();
            }

            text += next <= DateTimeOffset.Now
                ? " · checking for updates shortly"
                : next.Date == DateTime.Today
                    ? $" · next check at {next.ToString("HH:mm", CultureInfo.InvariantCulture)}"
                    : $" · next check tomorrow at {next.ToString("HH:mm", CultureInfo.InvariantCulture)}";
        }

        SetStatus(note is null ? text : $"{note} · {text}", Theme.DimText);
    }

    private void SetStatus(string text, Color color)
    {
        _status.Text = text;
        _status.ForeColor = color;
    }

    // ---------- refresh ----------

    /// <summary>
    /// With "All" selected everything is checked at once. With a folder
    /// selected, a small menu above the button asks whether to check
    /// everything or only that folder (with its subfolders).
    /// </summary>
    private void RefreshClicked()
    {
        // While a check runs the button shows Stop.
        if (_refreshing)
        {
            if (_refreshCancel is { IsCancellationRequested: false } running)
            {
                running.Cancel();
                SetStatus("Stopping…", Theme.Text);
            }

            return;
        }

        List<Repo> all = RepoStore.List();

        if (FilterTree.FolderOf(_tree.SelectedNode) is not { } folder)
        {
            RefreshRepos(all, everything: true);
            return;
        }

        List<Repo> inFolder = VisibleRepos(all);

        _refreshMenu?.Dispose();
        _refreshMenu = new ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(),
            BackColor = Theme.Surface,
            ForeColor = Theme.Text,
            ShowImageMargin = false
        };

        _refreshMenu.Items.Add(new ToolStripMenuItem(
            $"Refresh everything ({Count(all.Count)})", null, (_, _) => RefreshRepos(all, everything: true)));

        // "&&": a single "&" in a folder name would become a shortcut underline.
        _refreshMenu.Items.Add(new ToolStripMenuItem(
            $"Refresh only \"{folder.Name.Replace("&", "&&", StringComparison.Ordinal)}\" ({Count(inFolder.Count)})",
            null, (_, _) => RefreshRepos(inFolder, everything: false))
        {
            Enabled = inFolder.Count > 0
        });

        // Above the button: it sits at the bottom of the window.
        _refreshMenu.Show(_refresh, new Point(0, -2), ToolStripDropDownDirection.AboveRight);

        static string Count(int n) => n == 1 ? "1 repository" : $"{n} repositories";
    }

    /// <param name="everything">
    /// Every repository is being checked. Only then does the automatic
    /// check's period start over: a folder-only refresh leaves the rest unchecked.
    /// </param>
    private async void RefreshRepos(List<Repo> repos, bool everything)
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;

        // Stop cancels the requests under way and skips the repos still waiting;
        // closing the window does the same.
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        _refreshCancel = cancel;
        ShowRefreshAsStop(true);

        int done = 0;
        SetStatus($"Checking 0/{repos.Count}…", Theme.Text);

        // A few at a time: all at once would trip the sites' abuse limits on a long list.
        using var gate = new SemaphoreSlim(RefreshParallel);

        await Task.WhenAll(repos.Select(async repo =>
        {
            try
            {
                await gate.WaitAsync(cancel.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            bool checkedIt;

            try
            {
                checkedIt = await CheckAsync(repo, cancel.Token);
            }
            finally
            {
                gate.Release();
            }

            if (checkedIt)
            {
                done++;
            }

            if (!IsDisposed && !cancel.IsCancellationRequested)
            {
                SetStatus($"Checking {done}/{repos.Count}…", Theme.Text);
            }
        }));

        if (IsDisposed)
        {
            return;
        }

        bool stopped = cancel.IsCancellationRequested;
        _refreshCancel = null;
        _refreshing = false;
        ShowRefreshAsStop(false);

        if (stopped)
        {
            // Not a full check: the period does not start over, and the
            // automatic check holds off one period rather than starting again.
            _autoHeldUntil = DateTimeOffset.UtcNow.AddMinutes(_auto.Minutes);
            UpdateSummary($"Check stopped after {done} of {repos.Count}");
        }
        else
        {
            if (everything)
            {
                AutoCheck.SetLastFullCheck(DateTimeOffset.UtcNow);
                _auto = AutoCheck.Load();
                _autoHeldUntil = DateTimeOffset.MinValue;
            }

            UpdateSummary();
        }

        Resort();
    }

    private void ShowRefreshAsStop(bool stop)
    {
        _refresh.Picture = stop ? IconButton.StopIcon : IconButton.RefreshIcon;
        _tips.SetToolTip(_refresh, stop ? "Stop checking" : "Refresh: check for new versions");
    }

    /// <summary>
    /// Checks one repo and saves Latest, or the error that the red X shows.
    /// False when it was cancelled first: nothing is saved then, so a stopped
    /// check leaves the row as it was instead of marking it failed.
    /// </summary>
    private async Task<bool> CheckAsync(Repo repo, CancellationToken cancel)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Site? site = SiteOf(repo);

        if (site is null)
        {
            RepoStore.SetError(repo.Id, "Its site was deleted in Settings.", now);
            ReloadRepo(repo.Id);
            return true;
        }

        try
        {
            Release? latest = await ApiClient.CheckLatestAsync(site, repo, cancel);
            RepoStore.SetLatest(repo.Id, latest?.ToVersion(), now);
        }
        catch (ApiException error)
        {
            // A request cut short by Stop can surface as a failure of its own
            // (the connection dropped): that is not the repo's fault.
            if (cancel.IsCancellationRequested)
            {
                return false;
            }

            RepoStore.SetError(repo.Id, error.Message, now);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        ReloadRepo(repo.Id);
        return true;
    }

    // ---------- downloads ----------

    /// <summary>
    /// Update / Download of one row: fetches the target version again, asks
    /// which files when there is more than one, and downloads them.
    /// </summary>
    /// <param name="redownload">
    /// "Redownload…" on the right-click menu: the Current version instead of
    /// the target, and the file picker always, with what is on disk ticked.
    /// </param>
    private async void DownloadRepo(Repo listed, bool redownload = false)
    {
        if (_downloads.ContainsKey(listed.Id))
        {
            return;
        }

        // Read again: Current, its files and the pin may be newer than the row.
        Repo repo = RepoStore.Get(listed.Id) ?? listed;
        Site? site = SiteOf(repo);
        VersionRef? target = redownload ? repo.Current : VersionState.Target(repo);

        if (site is null || target is null)
        {
            return;
        }

        SetProgress(repo.Id, new DownloadProgress(0, 0));

        // Progress<T> delivers its reports later, through the window's message
        // queue: the last one can arrive after the finally below has cleared the
        // row, and would leave it stuck at 100% and marked as downloading.
        bool finished = false;

        try
        {
            Release release = await ApiClient.GetReleaseAsync(site, repo, target, _closing.Token);

            string tag = release.ToVersion().DisplayTag;

            // A redownload offers whatever is on disk, even a source archive
            // picked while "Show source code" was on and since switched off.
            bool showSource = PreferenceStore.ShowSource()
                || (redownload && DownloadPaths.PreChecked(release.Sources, tag, repo.CurrentFiles, repo.Current?.DisplayTag).Count > 0);

            List<ReleaseFile> files = release.Offered(showSource);

            if (files.Count == 0)
            {
                Warn($"{repo.Name}: this version has no files to download.");
                return;
            }

            List<ReleaseFile> chosen = files;

            if (files.Count > 1 || redownload)
            {
                HashSet<string> preChecked = DownloadPaths.PreChecked(
                    files, tag, repo.CurrentFiles, repo.Current?.DisplayTag);

                using var picker = new FilePickerForm(repo, release, files, preChecked, redownload);

                if (picker.ShowDialog(this) != DialogResult.OK || picker.Chosen.Count == 0)
                {
                    return;
                }

                chosen = picker.Chosen;
            }

            var progress = new Progress<DownloadProgress>(value =>
            {
                if (!finished)
                {
                    SetProgress(repo.Id, value);
                }
            });

            DownloadOutcome outcome = await Downloader.DownloadAsync(
                site, repo, _filters, release, chosen, progress, _closing.Token, redownload);

            if (outcome.Warnings.Count > 0)
            {
                SetStatus($"{repo.Name}: " + string.Join(" ", outcome.Warnings), Theme.Amber);
            }
            else
            {
                SetStatus($"{repo.Name}: {tag} downloaded {(redownload ? "again " : "")}"
                          + (chosen.Count == 1 ? "(1 file)." : $"({chosen.Count} files)."), Theme.DimText);
            }
        }
        catch (ApiException error)
        {
            if (!IsDisposed)
            {
                Warn($"{repo.Name}: {error.Message}");
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            finished = true;
            _downloads.Remove(repo.Id);

            if (!IsDisposed)
            {
                _list.RowOf(repo.Id)?.SetProgress(null);
                ReloadRepo(repo.Id);
                Resort();
            }
        }
    }

    private void SetProgress(long id, DownloadProgress value)
    {
        _downloads[id] = value;
        _list.RowOf(id)?.SetProgress(value);
    }

    private void OpenUpdateAll()
    {
        // In name order, by the same rule as the list (articles ignored);
        // the database's own ORDER BY knows nothing about "The".
        List<Repo> pending = RepoSorting.Sort(PendingUpdates(), RepoOrder.Name);

        if (pending.Count == 0)
        {
            return;
        }

        using (var window = new UpdateAllForm(pending, _sites, _filters))
        {
            window.ShowDialog(this);
        }

        ReloadAll(keepScroll: true);
    }

    // ---------- repo actions ----------

    /// <param name="dropped">Filled in from a link dragged onto the window, or null for the + button.</param>
    private void AddRepo(AddressMatch? dropped = null)
    {
        long? folder = FilterTree.FolderOf(_tree.SelectedNode)?.Id;

        using var window = new RepoForm(null, _sites, _filters, folder, dropped);

        if (window.ShowDialog(this) != DialogResult.OK || window.Saved is not { } saved)
        {
            ForgetUnusedSites();
            return;
        }

        ReloadAll(keepScroll: true);
        _ = CheckAsync(saved, _closing.Token);
    }

    /// <summary>
    /// After Add or Edit was cancelled: a server detected inside that window
    /// was saved as a site, and nothing uses it now.
    /// </summary>
    private void ForgetUnusedSites()
    {
        SiteStore.RemoveUnused();
        _sites = SiteStore.List();
    }

    private void EditRepo(Repo repo)
    {
        if (_downloads.ContainsKey(repo.Id))
        {
            Warn("Wait for the download to finish before editing this repository.");
            return;
        }

        using var window = new RepoForm(RepoStore.Get(repo.Id) ?? repo, _sites, _filters, repo.FilterId);

        if (window.ShowDialog(this) != DialogResult.OK || window.Saved is not { } saved)
        {
            ForgetUnusedSites();
            return;
        }

        ReloadAll(keepScroll: true);

        if (window.AddressChanged)
        {
            _ = CheckAsync(saved, _closing.Token);
        }
    }

    private void DeleteRepo(Repo repo)
    {
        if (_downloads.ContainsKey(repo.Id))
        {
            Warn("Wait for the download to finish before deleting this repository.");
            return;
        }

        DialogResult answer = DarkMessageBox.Show(this,
            $"Remove \"{repo.Name}\" from the list?\n\nFiles already downloaded stay in\n{DownloadPaths.RepoFolder(repo, _filters)}",
            "Git-Sync", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,

            // "No" by default: a delete is never confirmed by an Enter pressed by mistake.
            MessageBoxDefaultButton.Button2);

        if (answer != DialogResult.Yes)
        {
            return;
        }

        RepoStore.Delete(repo.Id);
        ReloadAll(keepScroll: true);
    }

    private void OpenFolder(Repo repo)
    {
        string folder = DownloadPaths.RepoFolder(repo, _filters);

        // The files may still be where an older Folder or Filter put them.
        if (!Directory.Exists(folder) && repo.CurrentFiles.Count > 0)
        {
            folder = Path.GetDirectoryName(DownloadPaths.ToAbsolute(repo.CurrentFiles[0])) ?? folder;
        }

        if (!Directory.Exists(folder))
        {
            Warn("Nothing has been downloaded for this repository yet.");
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    private void OpenPage(Repo repo)
    {
        if (SiteOf(repo) is not { } site)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(ApiUrls.ReleasesPage(site, repo.Path)) { UseShellExecute = true });
        }
        catch (Exception error)
        {
            Warn("Could not open the browser.\n" + error.Message);
        }
    }

    private void ShowMenu(Repo repo, Point screenPoint)
    {
        if (SiteOf(repo) is not { } site)
        {
            return;
        }

        _menu?.Dispose();

        var menu = new VersionMenu(repo, site,
            edit: () => EditRepo(repo),
            delete: () => DeleteRepo(repo),
            openFolder: () => OpenFolder(repo),
            openPage: () => OpenPage(repo),
            redownload: repo.Current is null || _downloads.ContainsKey(repo.Id)
                ? null
                : () => DownloadRepo(repo, redownload: true));

        menu.VersionPicked += (release, isLatest) =>
        {
            // The Latest one means "follow Latest again", not a pin on today's tag.
            RepoStore.SetSelected(repo.Id, isLatest ? null : release.ToVersion());
            ReloadRepo(repo.Id);
            UpdateSummary();
            Resort();
        };

        menu.LatestFound += latest =>
        {
            RepoStore.SetLatest(repo.Id, latest?.ToVersion(), DateTimeOffset.UtcNow);
            ReloadRepo(repo.Id);
        };

        // The versions are fetched by the menu itself, only when its Versions submenu opens.
        _menu = menu;
        menu.Show(screenPoint);
    }

    // ---------- links dragged onto the window or pasted ----------

    /// <summary>
    /// Whether this drag can be dropped here. Not while a dialog or message
    /// box is open: the window is disabled then, and a second Add window would
    /// open on top of it.
    /// </summary>
    private DragDropEffects DropEffect(DragEventArgs e)
    {
        if (!CanFocus || LinkReader.Read(e.Data) is null)
        {
            return DragDropEffects.None;
        }

        // Browsers offer Copy and Link for a link; either one shows the right cursor.
        return (e.AllowedEffect & DragDropEffects.Link) != 0 ? DragDropEffects.Link
            : (e.AllowedEffect & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    protected override void OnDragEnter(DragEventArgs drgevent)
    {
        base.OnDragEnter(drgevent);
        drgevent.Effect = DropEffect(drgevent);

        if (drgevent.Effect != DragDropEffects.None && LinkReader.Read(drgevent.Data) is { } link)
        {
            SetStatus("Drop to add " + link, Theme.Text);
        }
    }

    protected override void OnDragOver(DragEventArgs drgevent)
    {
        base.OnDragOver(drgevent);
        drgevent.Effect = DropEffect(drgevent);
    }

    protected override void OnDragLeave(EventArgs e)
    {
        base.OnDragLeave(e);
        UpdateSummary();
    }

    protected override void OnDragDrop(DragEventArgs drgevent)
    {
        base.OnDragDrop(drgevent);

        if (LinkReader.Read(drgevent.Data) is not { } link)
        {
            return;
        }

        // Handled after the drop returns: the browser waits for this event to
        // finish, and a window opened in here would freeze it until closed.
        BeginInvoke(() => AddLink(link));
    }

    /// <summary>
    /// Ctrl+V anywhere in the window does what dropping the link does — except
    /// in the search box, the window's one text field, where it pastes text as
    /// usual. Caught in ProcessCmdKey because the focus may be on the tree or
    /// the drop down, which would otherwise take the keys for themselves.
    /// Ctrl+F goes to the search box.
    /// </summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.V) && !_search.Editing)
        {
            PasteLink();
            return true;
        }

        if (keyData == (Keys.Control | Keys.F))
        {
            _search.FocusAndSelect();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void PasteLink()
    {
        string? link;

        try
        {
            // The same formats as a drag: a link copied from a browser, plain
            // text, or a .url shortcut copied in Explorer.
            link = LinkReader.Read(Clipboard.GetDataObject());
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another program can hold the clipboard open for a moment.
            SetStatus("Could not read the clipboard. Try again.", Theme.Amber);
            return;
        }

        if (link is null)
        {
            SetStatus("The clipboard does not hold a repository link.", Theme.Amber);
            return;
        }

        AddLink(link);
    }

    /// <summary>
    /// Opens Add filled in from a dropped or pasted link, unless the repo is
    /// already in the list.
    /// </summary>
    private async void AddLink(string link)
    {
        // A second link while the first host is still being checked would open two Add windows.
        if (_addingLink)
        {
            return;
        }

        UpdateSummary();

        // After a drop, the browser the link came from still has the focus.
        Activate();

        AddressMatch? match = RepoAddress.Parse(link, _sites);

        if (match is null)
        {
            Warn("This link does not point at a repository:\n" + link);
            return;
        }

        if (match.Site is null)
        {
            _addingLink = true;

            try
            {
                match = await DetectSiteAsync(link, match.Host);
            }
            finally
            {
                _addingLink = false;
            }

            if (match is null || IsDisposed)
            {
                return;
            }
        }

        if (match.Site is not { } site)
        {
            return;
        }

        if (RepoStore.FindDuplicate(site.Id, match.Path, 0) is { } existing)
        {
            string where = existing.FilterId is null
                ? "no filter"
                : string.Join("\\", FilterStore.PathOf(existing.FilterId, _filters));

            DarkMessageBox.Show(this,
                $"{site.Host}/{match.Path} is already in the list as \"{existing.Name}\" ({where}).",
                "Git-Sync", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        AddRepo(match);
    }

    /// <summary>
    /// A link from a host Git-Sync does not know: asks the server what it runs
    /// and adds it as a site when it is a forge Git-Sync reads. Returns the
    /// link read again with the new site, or null after telling the user why not.
    /// </summary>
    private async Task<AddressMatch?> DetectSiteAsync(string link, string host)
    {
        SetStatus($"Checking what {host} runs…", Theme.Text);

        ApiType? type;

        try
        {
            type = await SiteDetection.DetectAsync(host, _closing.Token);
        }
        catch (ApiException error)
        {
            UpdateSummary();
            Warn(error.Message);
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        UpdateSummary();

        if (type is not { } api)
        {
            Warn($"{host} is not a GitHub, GitLab or Forgejo/Gitea server, so Git-Sync cannot read it.");
            return null;
        }

        SiteStore.Add(host, api);
        _sites = SiteStore.List();

        // Read again with the site known: GitLab paths keep their nested groups.
        return RepoAddress.Parse(link, _sites);
    }

    // ---------- settings ----------

    private void OpenSettings(object? sender, EventArgs e)
    {
        List<long> imported;

        using (var settings = new SettingsForm())
        {
            settings.ShowDialog(this);
            imported = settings.ImportedRepoIds;
        }

        _auto = AutoCheck.Load();
        ReloadAll(keepScroll: true);

        // Imported repositories are checked like one just added, so their rows show a Latest.
        if (imported.Count > 0)
        {
            RefreshRepos([.. RepoStore.List().Where(r => imported.Contains(r.Id))], everything: false);
        }

        // Switched on with a period that has already run out: check now, not in 30 seconds.
        AutoTick();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_downloads.Count > 0)
        {
            DialogResult answer = DarkMessageBox.Show(this,
                "Downloads are still running. Stop them and quit?", "Git-Sync",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);

            if (answer != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        _closing.Cancel();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _autoTimer.Stop();
            _autoTimer.Dispose();
            _searchTimer.Stop();
            _searchTimer.Dispose();
            _menu?.Dispose();
            _refreshMenu?.Dispose();
            _tips.Dispose();
            _closing.Dispose();
        }

        base.Dispose(disposing);
    }

    private void Warn(string message) =>
        DarkMessageBox.Show(this, message, "Git-Sync", MessageBoxButtons.OK, MessageBoxIcon.Warning);
}
