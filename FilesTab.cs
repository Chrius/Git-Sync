using System.Drawing;
using System.Windows.Forms;

namespace GitSync;

/// <summary>
/// "Files" tab: which files of a release are offered when choosing what to
/// download. Names to hide (see <see cref="FileFilters"/>), in one box with
/// one pattern per line — quick to type several, and to paste a list into —
/// and whether the source archives are listed too. Saved as it changes, like
/// the other tabs; the next download or Update All window uses it.
/// </summary>
internal sealed class FilesTab : UserControl
{
    private const int BoxTop = 28;
    private const int BoxHeight = 200;
    private const int Indent = 20;

    private readonly Panel _frame;
    private readonly TextBox _patterns;
    private readonly Label _hint;
    private readonly Panel _separator;
    private readonly CheckBox _showSource;
    private readonly Label _sourceHint;

    internal FilesTab()
    {
        // Held until everything exists: adding the first label lays the tab
        // out, and OnLayout reached the box before it was made (a crash on
        // opening Settings).
        SuspendLayout();
        Dock = DockStyle.Fill;

        Controls.Add(DarkControls.CreateLabel("Ignored files", 0, 6));

        _frame = new Panel { BackColor = Theme.Field, Padding = new Padding(6, 4, 2, 4) };

        _patterns = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            AcceptsReturn = true,
            WordWrap = false,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Field,
            ForeColor = Theme.Text,
            TabIndex = 0,

            // Saved with \n; the box needs \r\n to break lines.
            Text = PreferenceStore.IgnoredFiles().Replace("\n", "\r\n", StringComparison.Ordinal)
        };
        _patterns.TextChanged += (_, _) => PreferenceStore.SetIgnoredFiles(_patterns.Text);
        DarkControls.UseDarkScrollbars(_patterns);

        _frame.Controls.Add(_patterns);
        Controls.Add(_frame);

        _hint = new Label
        {
            AutoSize = false,
            ForeColor = Theme.DimText,
            Text = "One pattern per line. * stands for any text and ? for any one character, matched against "
                 + "the whole file name, in any case: *.tar.gz hides every .tar.gz file, *-linux-* every file "
                 + "with \"-linux-\" in its name.\n\n"
                 + "Matching files are hidden when choosing what to download or update; "
                 + "\"Show All Files\" in those windows shows them for that time."
        };
        Controls.Add(_hint);

        // Moved here from the Automation tab: like the ignored files, it
        // decides what the download windows list.
        _separator = new Panel { BackColor = Theme.Separator, Height = 1 };
        Controls.Add(_separator);

        _showSource = new CheckBox
        {
            Text = "Show source code when downloading",
            AutoSize = true,
            TabIndex = 1,
            Checked = PreferenceStore.ShowSource()
        };
        _showSource.CheckedChanged += (_, _) => PreferenceStore.SetShowSource(_showSource.Checked);
        Controls.Add(_showSource);

        _sourceHint = new Label
        {
            AutoSize = false,
            ForeColor = Theme.DimText,
            Text = "Lists the Source code (zip) and Source code (tar.gz) of a release with its "
                 + "other files, so they can be downloaded too. When off, only a release with no "
                 + "files of its own offers its source, as a zip."
        };
        Controls.Add(_sourceHint);

        ResumeLayout(performLayout: true);
    }

    /// <summary>
    /// Top to bottom, with each text's height measured at the tab's width
    /// (as in the Automation tab), so a wrapped line never runs into what follows it.
    /// </summary>
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);

        int width = ClientSize.Width;

        _frame.SetBounds(0, BoxTop, width, BoxHeight);
        _hint.SetBounds(0, _frame.Bottom + 10, width, TextHeight(_hint, width));
        _separator.SetBounds(0, _hint.Bottom + 14, width, 1);
        _showSource.Location = new Point(0, _separator.Bottom + 16);
        _sourceHint.SetBounds(Indent, _showSource.Bottom + 6, width - Indent, TextHeight(_sourceHint, width - Indent));
    }

    private static int TextHeight(Label label, int width) =>
        TextRenderer.MeasureText(label.Text, label.Font, new Size(Math.Max(1, width), int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height + 4;
}
