using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace GitSync;

/// <summary>
/// Checked list of a release's files, with All / None. Shared by the file
/// picker and the Update All window. Shows a message instead of the list
/// while the files load or when they cannot be loaded.
/// </summary>
internal sealed class FileChecklist : UserControl
{
    private sealed record Entry(ReleaseFile File)
    {
        public override string ToString() =>
            File.Size > 0 ? $"{File.Name}    ({FormatSize(File.Size)})" : File.Name;
    }

    private readonly Panel _frame;
    private readonly CheckedListBox _list;
    private readonly Label _message;
    private readonly FlatButton _all;
    private readonly FlatButton _none;

    /// <summary>
    /// True while the list is checked by code (filling it, All / None): those
    /// changes are reported once at the end instead of item by item.
    /// </summary>
    private bool _filling;

    internal event EventHandler? SelectionChanged;

    internal FileChecklist()
    {
        BackColor = Theme.Background;

        _frame = new Panel { BackColor = Theme.Field, Padding = new Padding(6, 4, 2, 4) };

        _list = new CheckedListBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Field,
            ForeColor = Theme.Text,
            CheckOnClick = true,
            IntegralHeight = false
        };

        // ItemCheck fires before the state changes, so the count is right only
        // after a BeginInvoke. But BeginInvoke throws while the window has no
        // handle yet — which is the case when the file picker pre-checks last
        // time's files from its constructor (the crash on updating a release
        // with several files). Checks made by code are reported by the code
        // itself, once, when it is done.
        _list.ItemCheck += (_, _) =>
        {
            if (!_filling && IsHandleCreated)
            {
                BeginInvoke(() => SelectionChanged?.Invoke(this, EventArgs.Empty));
            }
        };
        DarkControls.UseDarkScrollbars(_list);

        _message = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Theme.DimText,
            BackColor = Theme.Field,
            Visible = false
        };

        _frame.Controls.Add(_list);
        _frame.Controls.Add(_message);
        Controls.Add(_frame);

        _all = new FlatButton { Text = "All", Size = new Size(64, 24) };
        _all.Click += (_, _) => SetAll(true);
        _none = new FlatButton { Text = "None", Size = new Size(64, 24) };
        _none.Click += (_, _) => SetAll(false);
        Controls.Add(_all);
        Controls.Add(_none);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);

        int buttonsTop = Height - _all.Height;
        _frame.SetBounds(0, 0, Width, Math.Max(20, buttonsTop - 8));
        _all.Location = new Point(0, buttonsTop);
        _none.Location = new Point(_all.Right + 6, buttonsTop);
    }

    internal void SetFiles(IReadOnlyList<ReleaseFile> files, ISet<string> preChecked)
    {
        _filling = true;
        _list.BeginUpdate();
        _list.Items.Clear();

        foreach (ReleaseFile file in files)
        {
            // A single file is the obvious choice; otherwise only what matched last time.
            bool check = files.Count == 1 || preChecked.Contains(file.Name);
            _list.Items.Add(new Entry(file), check);
        }

        _list.EndUpdate();
        _filling = false;

        _message.Visible = false;
        _list.Visible = true;
        SetButtonsEnabled(files.Count > 1);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void SetMessage(string text)
    {
        _list.Items.Clear();
        _message.Text = text;
        _message.Visible = true;
        _list.Visible = false;
        SetButtonsEnabled(false);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetButtonsEnabled(bool enabled)
    {
        _all.Enabled = enabled;
        _none.Enabled = enabled;
    }

    internal List<ReleaseFile> CheckedFiles =>
        [.. _list.CheckedItems.Cast<Entry>().Select(entry => entry.File)];

    private void SetAll(bool check)
    {
        _filling = true;

        for (int i = 0; i < _list.Items.Count; i++)
        {
            _list.SetItemChecked(i, check);
        }

        _filling = false;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Always with a dot and in English units, whatever the machine's culture.</summary>
    internal static string FormatSize(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB"];
        double value = bytes;
        int unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes} bytes")
            : string.Create(CultureInfo.InvariantCulture, $"{value:0.0} {units[unit]}");
    }
}

/// <summary>Picks which files of a release to download, when it has more than one.</summary>
internal sealed class FilePickerForm : Form
{
    private readonly FileChecklist _files;
    private readonly Button _download;

    internal List<ReleaseFile> Chosen { get; private set; } = [];

    internal FilePickerForm(Repo repo, Release release, ISet<string> preChecked)
    {
        Text = "Choose Files";
        ShowIcon = false;
        ClientSize = new Size(560, 420);
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(420, 320);
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 9F);

        string version = release.Kind == VersionKind.Commit ? release.Title : release.Tag;

        var heading = new Label
        {
            Text = $"{repo.Name} — {version}\nChoose the files to download:",
            Location = new Point(16, 12),
            AutoSize = true,
            ForeColor = Theme.Text
        };
        Controls.Add(heading);

        _files = new FileChecklist
        {
            Location = new Point(16, 54),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
        };
        _download = new Button { Text = "Download", Size = new Size(96, 30), Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
        _download.Click += (_, _) =>
        {
            Chosen = _files.CheckedFiles;
            DialogResult = DialogResult.OK;
        };

        var cancel = new Button
        {
            Text = "Cancel",
            Size = new Size(96, 30),
            DialogResult = DialogResult.Cancel,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right
        };

        cancel.Location = new Point(ClientSize.Width - 16 - 96, ClientSize.Height - 16 - 30);
        _download.Location = new Point(cancel.Left - 8 - 96, cancel.Top);
        _files.Size = new Size(ClientSize.Width - 32, cancel.Top - 12 - 54);

        Controls.Add(_files);
        Controls.Add(_download);
        Controls.Add(cancel);

        AcceptButton = _download;
        CancelButton = cancel;

        _files.SelectionChanged += (_, _) => _download.Enabled = _files.CheckedFiles.Count > 0;
        _files.SetFiles(release.Files, preChecked);
    }
}
