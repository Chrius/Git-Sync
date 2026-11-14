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

    internal SettingsForm()
    {
        Text = "Settings";
        ShowIcon = false;
        ClientSize = new Size(600, 480);
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

        AddTab("Filters", new FiltersTab());
        AddTab("Sites", new SitesTab());
        AddTab("Automation", new AutomationTab());

        Controls.Add(_tabs);
    }

    private void AddTab(string title, Control content)
    {
        var page = new TabPage(title) { Padding = new Padding(12) };
        page.Controls.Add(content);
        _tabs.TabPages.Add(page);
    }
}
