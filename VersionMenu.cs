using System.Globalization;
using System.Windows.Forms;

namespace GitSync;

/// <summary>
/// Right-click menu of a row: the four actions, then "Bulk Download",
/// "Redownload" and a "Versions" submenu with every version on the site.
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

    /// <param name="bulkDownload">Null greys the item out: the repo does not keep previous versions, or a download of it is running.</param>
    /// <param name="redownload">Null greys the item out: nothing downloaded yet, or a download of this repo is running.</param>
    internal VersionMenu(
        Repo repo, Site site, Action edit, Action delete, Action openFolder, Action openPage, Action? bulkDownload, Action? redownload)
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

        // Files of many versions at once, kept side by side: only for a repo
        // that keeps previous versions, where more than one can stay on disk.
        Items.Add(new ToolStripMenuItem("Bulk Download", null, (_, _) => bulkDownload?.Invoke())
        {
            Enabled = bulkDownload is not null
        });

        // Picks the files of the Current version again: a wrong file chosen
        // the first time is otherwise stuck until the next release.
        Items.Add(new ToolStripMenuItem("Redownload", null, (_, _) => redownload?.Invoke())
        {
            Enabled = redownload is not null
        });

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

        // "Ignore pre-releases": left out, except the one on disk or pinned,
        // so what the row shows can still be found here.
        int hidden = 0;

        if (_repo.IgnorePrereleases)
        {
            bool Kept(Release version) =>
                (_repo.Current is { } current && VersionState.SameVersion(current, version.ToVersion()))
                || (_repo.Selected is { } pinned && VersionState.SameVersion(pinned, version.ToVersion()));

            hidden = versions.Count(v => v.Prerelease && !Kept(v));
            versions = [.. versions.Where(v => !v.Prerelease || Kept(v))];
        }

        Release? latest = ReleaseParser.PickLatest(versions, _repo.IgnorePrereleases)
            ?? (_repo.IgnorePrereleases ? null : versions.FirstOrDefault());

        ToolStripDropDown list = _versions.DropDown;
        list.SuspendLayout();
        _versions.DropDownItems.Remove(_loading);
        _loading.Dispose();

        if (versions.Count == 0)
        {
            _versions.DropDownItems.Add(new ToolStripMenuItem(
                hidden > 0 ? "No stable versions on the site" : "No versions on the site") { Enabled = false });
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

        if (hidden > 0)
        {
            _versions.DropDownItems.Add(new ToolStripSeparator());
            _versions.DropDownItems.Add(new ToolStripMenuItem(
                hidden == 1 ? "1 pre-release hidden" : $"{hidden} pre-releases hidden") { Enabled = false });
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
