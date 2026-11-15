using System.Drawing;
using System.Windows.Forms;

namespace GitSync;

/// <summary>
/// Settings window.
///
/// Tabs are built by <see cref="AddTab"/>: a new tab is a content class and one
/// line in the constructor.
/// </summary>
internal sealed class SettingsForm : Form
{
    private readonly TabControl _tabs;
    private readonly FiltersTab _filtersTab;
    private readonly SitesTab _sitesTab;
    private readonly DataTab _dataTab;

    /// <summary>Repositories added by imports while the window was open; the main window checks them.</summary>
    internal List<long> ImportedRepoIds { get; } = [];

    /// <param name="downloadsRunning">Whether the main window is downloading: the download folder cannot change meanwhile.</param>
    internal SettingsForm(Func<bool> downloadsRunning)
    {
        Text = "Settings";
        ShowIcon = false;
        // Sized for the Automation tab, the fullest one, with automatic checking
        // on (its "Every" row shown): the last checkbox keeps as much room
        // below it as the first one has above it.
        ClientSize = new Size(600, 536);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9F);

        _tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Padding = new Point(12, 6)
        };

        _filtersTab = new FiltersTab();
        _sitesTab = new SitesTab();

        _dataTab = new DataTab(downloadsRunning);

        // An import can add folders and self-hosted servers, which the other tabs already loaded.
        _dataTab.Imported += result =>
        {
            ImportedRepoIds.AddRange(result.AddedRepoIds);
            _filtersTab.ReloadFolders();
            _sitesTab.ReloadSites();
        };

        AddTab("Filters", _filtersTab);
        AddTab("Sites", _sitesTab);
        AddTab("Automation", new AutomationTab());
        AddTab("Data", _dataTab);

        Controls.Add(_tabs);
    }

    /// <summary>
    /// Stays open while an import runs: closed halfway, the main window would
    /// reload with part of the list and never check the rest. Every way of
    /// closing is refused, not only UserClosing — a WM_CLOSE that does not come
    /// from the X button arrives as TaskManagerClosing, and that is how the
    /// window got closed mid-import in testing. Only a Windows shutdown goes through.
    /// </summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_dataTab.Busy && e.CloseReason != CloseReason.WindowsShutDown)
        {
            e.Cancel = true;
            DarkMessageBox.Show(this, $"{_dataTab.BusyWith} is still running. Settings can be closed when it is done.",
                "Settings", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        base.OnFormClosing(e);
    }

    private void AddTab(string title, Control content)
    {
        var page = new TabPage(title) { Padding = new Padding(12) };
        page.Controls.Add(content);
        _tabs.TabPages.Add(page);
    }
}
