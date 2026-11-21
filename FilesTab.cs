using System.Drawing;
using System.Windows.Forms;

namespace GitSync;

/// <summary>
/// "Files" tab: names of release files to hide when choosing what to
/// download (see <see cref="FileFilters"/>). One box, one pattern per line:
/// quick to type several, and to paste a list into. Saved as it is typed,
/// like the other tabs; the next download or Update All window uses it.
/// </summary>
internal sealed class FilesTab : UserControl
{
    private const int BoxTop = 28;
    private const int BoxHeight = 220;

    private readonly Panel _frame;
    private readonly TextBox _patterns;
    private readonly Label _hint;

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

        ResumeLayout(performLayout: true);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);

        _frame.SetBounds(0, BoxTop, ClientSize.Width, BoxHeight);
        _hint.SetBounds(0, _frame.Bottom + 10, ClientSize.Width, 96);
    }
}
