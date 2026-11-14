using System.Globalization;
using System.Windows.Forms;

namespace GitSync;

/// <summary>
/// Right-click menu of a row: the four actions, then a "Versions" submenu
/// with every version on the site.
///
/// The versions used to fill this menu itself as they arrived, and a long
/// list made the menu grow and jump — under the mouse of someone about to
/// click Edit. In their own submenu they fill a separate window, so this one
/// never moves. And the site is asked only when Versions is opened, not on
/// every right click.
/// </summary>
internal sealed class VersionMenu : ContextMenuStrip
{
    private readonly Repo _repo;
    private readonly Site _site;
    private readonly ToolStripMenuItem _versions;
    private readonly ToolStripMenuItem _loading;
    private readonly CancellationTokenSource _cancel = new();
    private bool _loadStarted;

    /// <summary>A version was clicked; the flag says whether it is the one shown as Latest.</summary>
    internal event Action<Release, bool>? VersionPicked;

    /// <summary>
    /// The fetch also says which version is Latest, so the row is updated
    /// with it — the menu and the row must agree on "(Latest)".
    /// </summary>
    internal event Action<Release?>? LatestFound;

    internal VersionMenu(Repo repo, Site site, Action edit, Action delete, Action openFolder, Action openPage)
    {
        _repo = repo;
        _site = site;

        Renderer = new DarkMenuRenderer();
        BackColor = Theme.Surface;
        ForeColor = Theme.Text;
        ShowImageMargin = true;
        ShowCheckMargin = false;

        Items.Add(new ToolStripMenuItem("Edit…", null, (_, _) => edit()));
        Items.Add(new ToolStripMenuItem("Delete", null, (_, _) => delete()));
        Items.Add(new ToolStripMenuItem("Open Folder", null, (_, _) => openFolder()));
        Items.Add(new ToolStripMenuItem("Open Releases Page", null, (_, _) => openPage()));
        Items.Add(new ToolStripSeparator());

        _versions = new ToolStripMenuItem("Versions");

        // A submenu has a window of its own, with the default (light) look
        // unless told otherwise.
        if (_versions.DropDown is ToolStripDropDownMenu dropDown)
        {
            dropDown.Renderer = new DarkMenuRenderer();
            dropDown.BackColor = Theme.Surface;
            dropDown.ForeColor = Theme.Text;
            dropDown.ShowImageMargin = true;
            dropDown.ShowCheckMargin = false;
        }

        // The placeholder is also what gives "Versions" its arrow before anything is loaded.
        _loading = new ToolStripMenuItem("Loading versions…") { Enabled = false };
        _versions.DropDownItems.Add(_loading);
        _versions.DropDownOpening += (_, _) =>
        {
            if (!_loadStarted)
            {
                _loadStarted = true;
                LoadVersions();
            }
        };

        Items.Add(_versions);
    }

    private async void LoadVersions()
    {
        List<Release> versions;

        try
        {
            versions = await ApiClient.ListVersionsAsync(_site, _repo, _cancel.Token);
        }
        catch (ApiException error)
        {
            if (!IsDisposed)
            {
                _loading.Text = error.Message;
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

        Release? latest = ReleaseParser.PickLatest(versions) ?? versions.FirstOrDefault();

        ToolStripDropDown list = _versions.DropDown;
        list.SuspendLayout();
        _versions.DropDownItems.Remove(_loading);
        _loading.Dispose();

        if (versions.Count == 0)
        {
            _versions.DropDownItems.Add(new ToolStripMenuItem("No versions on the site") { Enabled = false });
        }

        foreach (Release version in versions)
        {
            bool isLatest = ReferenceEquals(version, latest);
            var item = new ToolStripMenuItem(Label(version, isLatest))
            {
                Checked = _repo.Selected is { } pinned && VersionState.SameVersion(pinned, version.ToVersion())
            };

            item.Click += (_, _) => VersionPicked?.Invoke(version, isLatest);
            _versions.DropDownItems.Add(item);
        }

        list.ResumeLayout();

        LatestFound?.Invoke(latest);
    }

    /// <summary>"v1.4.0 — 2026-11-10 (Latest) (Current)".</summary>
    private string Label(Release version, bool isLatest)
    {
        string name = version.Kind == VersionKind.Commit ? version.Title : version.Tag;
        string text = version.Date is { } date
            ? $"{name} — {date.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}"
            : name;

        if (isLatest)
        {
            text += " (Latest)";
        }

        if (_repo.Current is { } current && VersionState.SameVersion(current, version.ToVersion()))
        {
            text += " (Current)";
        }

        if (version.Prerelease)
        {
            text += " (Pre-release)";
        }

        if (version.Kind == VersionKind.ReleaseSource)
        {
            text += " (Source)";
        }

        // An "&" in a tag would otherwise turn into a keyboard shortcut underline.
        return text.Replace("&", "&&", StringComparison.Ordinal);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cancel.Cancel();
            _cancel.Dispose();
        }

        base.Dispose(disposing);
    }
}
