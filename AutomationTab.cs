using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace GitSync;

/// <summary>
/// "Automation" tab: checking every repository by itself on a schedule.
/// Saved as soon as it changes, like the Filters tab; the main window picks
/// the new settings up when Settings closes.
/// </summary>
internal sealed class AutomationTab : UserControl
{
    private const int TopCheck = 6;
    private const int TopInterval = 38;
    private const int TopHint = 84;
    private const int Indent = 20;

    private readonly CheckBox _enabled;
    private readonly Label _everyLabel;
    private readonly ComboBox _interval;
    private readonly Label _hint;
    private readonly Label _last;

    /// <summary>True while the screen fills itself, so it is not taken for user changes.</summary>
    private bool _filling;

    internal AutomationTab()
    {
        Dock = DockStyle.Fill;

        _enabled = new CheckBox
        {
            Text = "Check new updates automatically",
            AutoSize = true,
            Location = new Point(0, TopCheck),
            TabIndex = 0
        };
        _enabled.CheckedChanged += (_, _) => AutoCheckToggled();
        Controls.Add(_enabled);

        _everyLabel = DarkControls.CreateLabel("Every", Indent, TopInterval + 4);
        Controls.Add(_everyLabel);

        _interval = DarkControls.CreateList(1);
        _interval.Items.AddRange([.. AutoCheck.Intervals.Select(i => i.Text)]);
        _interval.SelectedIndexChanged += (_, _) =>
        {
            if (!_filling && _interval.SelectedIndex >= 0)
            {
                AutoCheck.SetMinutes(AutoCheck.Intervals[_interval.SelectedIndex].Minutes);
                ShowLastCheck();
            }
        };
        Controls.Add(_interval);

        _hint = new Label
        {
            AutoSize = false,
            ForeColor = Theme.DimText,
            Location = new Point(0, TopHint),
            Text = "Every repository is checked, counting from the last time all of them were "
                 + "checked — by hand or automatically. A check that falls due while a window such "
                 + "as this one is open waits for it to close.\n\n"
                 + "Without a GitHub token, GitHub allows 60 requests per hour: a long list checked "
                 + "every 30 minutes can run out. A token in the Sites tab raises that to 5,000."
        };
        Controls.Add(_hint);

        _last = new Label { AutoSize = false, ForeColor = Theme.DimText };
        Controls.Add(_last);

        Fill();
    }

    /// <summary>Laid out from the real size (see FiltersTab: Anchor computes from the wrong width here).</summary>
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);

        int width = ClientSize.Width;

        if (width <= 100)
        {
            return;
        }

        _interval.SetBounds(_everyLabel.Right + 8, TopInterval, 180, _interval.Height);
        _hint.SetBounds(0, TopHint, width, 110);
        _last.SetBounds(0, TopHint + 120, width, 40);
    }

    private void Fill()
    {
        AutoCheckSettings settings = AutoCheck.Load();

        _filling = true;
        _enabled.Checked = settings.Enabled;
        _interval.SelectedIndex = Array.FindIndex(AutoCheck.Intervals, i => i.Minutes == settings.Minutes);
        _filling = false;

        ShowInterval(settings.Enabled);
        ShowLastCheck();
    }

    private void AutoCheckToggled()
    {
        if (_filling)
        {
            return;
        }

        AutoCheck.SetEnabled(_enabled.Checked);

        // The period is saved too, so the main window reads a complete setting
        // even if the drop down was never touched.
        AutoCheck.SetMinutes(AutoCheck.Intervals[Math.Max(0, _interval.SelectedIndex)].Minutes);

        ShowInterval(_enabled.Checked);
        ShowLastCheck();
    }

    /// <summary>The period only appears while automatic checking is on.</summary>
    private void ShowInterval(bool show)
    {
        _everyLabel.Visible = show;
        _interval.Visible = show;
    }

    private void ShowLastCheck()
    {
        AutoCheckSettings settings = AutoCheck.Load();

        string last = settings.LastFullCheck is { } when
            ? when.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : "never";

        string text = $"All repositories last checked: {last}.";

        if (settings.Enabled)
        {
            DateTimeOffset next = AutoCheck.NextCheck(settings.LastFullCheck, settings.Minutes, DateTimeOffset.Now);
            text += next <= DateTimeOffset.Now
                ? "\nNext automatic check: as soon as Settings closes."
                : $"\nNext automatic check: {next.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}.";
        }

        _last.Text = text;
    }
}
