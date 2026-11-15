using System.Drawing;
using System.Windows.Forms;

namespace GitSync;

/// <summary>
/// Add / Edit window of a repository.
///
/// Pasting a whole address in Repository picks the site and cuts the text
/// down to "owner/repo". Folder fills itself from Name and Repository until
/// the user types something else in it.
/// </summary>
internal sealed class RepoForm : Form
{
    private sealed record FilterItem(long? Id, string Text)
    {
        public override string ToString() => Text;
    }

    private const int Edge = 16;
    private const int LabelGap = 18;

    private readonly Repo? _editing;
    private readonly List<Site> _sites;
    private readonly TextBox _name;
    private readonly ComboBox _site;
    private readonly TextBox _repository;
    private readonly Label _hint;
    private readonly TextBox _folder;
    private readonly ComboBox _filter;
    private readonly CheckBox _keepPrevious;
    private readonly Panel _keepFrame;
    private readonly TextBox _keepVersions;
    private readonly Label _keepLabel;
    private readonly ToolTip _tips = new();
    private readonly IReadOnlyList<FilterFolder> _filters;

    /// <summary>Where the files of the repo being edited are, before any change: Save moves them along.</summary>
    private readonly string? _startFolder;

    private readonly List<Panel> _frames = [];
    private readonly Button _save;
    private readonly Button _cancel;

    /// <summary>Last folder name filled in automatically; while the field still holds it, it keeps following.</summary>
    private string _autoFolder = string.Empty;

    /// <summary>True while the window changes its own fields, so those changes do not look like typing.</summary>
    private bool _filling;

    /// <summary>Number of the latest server check; older answers are dropped.</summary>
    private int _detectNumber;

    private const string DefaultHint = "owner/repo — or paste the repository's address";

    internal Repo? Saved { get; private set; }

    /// <summary>New entry, or Edit moved it to another site or repository: it needs a check.</summary>
    internal bool AddressChanged { get; private set; }

    /// <param name="dropped">
    /// A new entry from a link dragged onto the main window: site, repository
    /// and folder come filled in, and only the name is left to type.
    /// </param>
    internal RepoForm(
        Repo? editing, List<Site> sites, IReadOnlyList<FilterFolder> filters, long? defaultFilterId,
        AddressMatch? dropped = null)
    {
        _editing = editing;
        _filters = filters;
        _startFolder = editing is null ? null : DownloadPaths.RepoFolder(editing, filters);

        // A copy: a server detected while typing is added here, not to the main window's list.
        _sites = [.. sites];

        Text = editing is null ? "Add Repository" : "Edit Repository";
        ShowIcon = false;
        ClientSize = new Size(480, 398);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 9F);

        _name = AddField("Name", 14, 0);
        _site = AddList("Site", 66, 1);
        _repository = AddField("Repository", 118, 2);

        _hint = new Label
        {
            Text = DefaultHint,
            AutoSize = false,
            Location = new Point(Edge, 118 + LabelGap + DarkControls.FieldHeight + 4),
            Size = new Size(ClientSize.Width - (2 * Edge), 18),
            ForeColor = Theme.DimText
        };
        Controls.Add(_hint);

        _folder = AddField("Folder", 194, 3);
        _filter = AddList("Filter", 246, 4);

        _keepPrevious = new CheckBox
        {
            Text = "Keep previous versions",
            AutoSize = true,
            Location = new Point(Edge, 304),
            TabIndex = 5
        };
        _keepPrevious.CheckedChanged += (_, _) => ShowKeepVersions();
        Controls.Add(_keepPrevious);

        // How many versions, beside the checkbox and only while it is ticked.
        (_keepFrame, _keepVersions) = DarkControls.CreateField(6);
        _keepVersions.MaxLength = 3;
        _keepVersions.Text = "0";
        _keepFrame.SetBounds(
            _keepPrevious.Left + _keepPrevious.PreferredSize.Width + 10,
            _keepPrevious.Top + ((_keepPrevious.PreferredSize.Height - DarkControls.FieldHeight) / 2),
            44, DarkControls.FieldHeight);
        Controls.Add(_keepFrame);
        _frames.Add(_keepFrame);

        _keepLabel = DarkControls.CreateLabel("versions (0 = all)", _keepFrame.Right + 8, 0);
        _keepLabel.ForeColor = Theme.DimText;
        _keepLabel.Top = _keepFrame.Top + ((DarkControls.FieldHeight - _keepLabel.PreferredHeight) / 2);
        Controls.Add(_keepLabel);

        string keepTip = "How many versions to keep, the current one included. "
                       + "Past that number, the oldest version's files are deleted. 0 keeps every version.";
        _tips.SetToolTip(_keepVersions, keepTip);
        _tips.SetToolTip(_keepLabel, keepTip);

        // Digits only: typed ones through KeyPress, pasted ones cleaned on TextChanged.
        _keepVersions.KeyPress += (_, e) =>
        {
            if (!char.IsControl(e.KeyChar) && !char.IsAsciiDigit(e.KeyChar))
            {
                e.Handled = true;
            }
        };
        _keepVersions.TextChanged += (_, _) =>
        {
            string digits = new([.. _keepVersions.Text.Where(char.IsAsciiDigit)]);

            if (digits != _keepVersions.Text)
            {
                _keepVersions.Text = digits;
                _keepVersions.SelectionStart = digits.Length;
            }
        };

        _save = new Button { Text = "Save", Size = new Size(96, 30), TabIndex = 10 };
        _save.Click += Save;
        _cancel = new Button { Text = "Cancel", Size = new Size(96, 30), TabIndex = 11, DialogResult = DialogResult.Cancel };

        int buttonsTop = ClientSize.Height - Edge - 30;
        _cancel.Location = new Point(ClientSize.Width - Edge - 96, buttonsTop);
        _save.Location = new Point(_cancel.Left - 8 - 96, buttonsTop);
        Controls.Add(_save);
        Controls.Add(_cancel);

        AcceptButton = _save;
        CancelButton = _cancel;

        // ----- content -----

        foreach (Site site in sites)
        {
            _site.Items.Add(site);
        }

        _filter.Items.Add(new FilterItem(null, "(None)"));

        foreach ((FilterFolder folder, string path) in FilterStore.Flatten(filters))
        {
            _filter.Items.Add(new FilterItem(folder.Id, path));
        }

        _filling = true;

        if (editing is not null)
        {
            _name.Text = editing.Name;
            _site.SelectedIndex = sites.FindIndex(s => s.Id == editing.SiteId);
            _repository.Text = editing.Path;
            _folder.Text = editing.Folder;
            SelectFilter(editing.FilterId);
            _keepPrevious.Checked = editing.KeepPrevious;
            _keepVersions.Text = editing.KeepVersions.ToString(System.Globalization.CultureInfo.InvariantCulture);

            // Keeps following Name and Repository only if it was never changed by hand.
            string suggestion = RepoAddress.SuggestFolder(editing.Name, editing.Path);
            _autoFolder = editing.Folder == suggestion ? suggestion : "\0";
        }
        else
        {
            _site.SelectedIndex = sites.Count > 0 ? 0 : -1;
            SelectFilter(defaultFilterId);

            if (dropped?.Site is { } site)
            {
                _site.SelectedIndex = sites.FindIndex(s => s.Id == site.Id);
                _repository.Text = dropped.Path;

                // Just the repo name for now; it becomes "Name - repo" as the name is typed.
                _autoFolder = RepoAddress.SuggestFolder(string.Empty, dropped.Path);
                _folder.Text = _autoFolder;
            }
        }

        _filling = false;
        ShowKeepVersions();

        _name.TextChanged += (_, _) => FollowFolder();
        _repository.TextChanged += RepositoryChanged;

        // Characters a folder name cannot have are ignored in Folder. KeyPress
        // stops them as they are typed; TextChanged is the net for pasted
        // text, which does not go through KeyPress.
        _folder.KeyPress += (_, e) =>
        {
            if (!char.IsControl(e.KeyChar) && DownloadPaths.IsInvalidChar(e.KeyChar))
            {
                e.Handled = true;
            }
        };
        _folder.TextChanged += (_, _) => DropInvalidFolderChars();
    }

    private void ShowKeepVersions()
    {
        _keepFrame.Visible = _keepPrevious.Checked;
        _keepLabel.Visible = _keepPrevious.Checked;
    }

    /// <summary>The number typed beside "Keep previous versions"; empty counts as 0, keep all.</summary>
    private int KeepVersions() =>
        int.TryParse(_keepVersions.Text, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out int count) ? count : 0;

    private void DropInvalidFolderChars()
    {
        string clean = DownloadPaths.RemoveInvalidChars(_folder.Text);

        if (clean == _folder.Text)
        {
            return;
        }

        // The cursor moves back by the characters dropped before it.
        int removedBefore = _folder.SelectionStart - DownloadPaths.RemoveInvalidChars(_folder.Text[.._folder.SelectionStart]).Length;
        int cursor = _folder.SelectionStart - removedBefore;

        bool wasFilling = _filling;
        _filling = true;
        _folder.Text = clean;
        _folder.SelectionStart = Math.Clamp(cursor, 0, clean.Length);
        _filling = wasFilling;
    }

    /// <summary>The name is what is left to type, whether the window starts empty or from a dropped link.</summary>
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _name.Focus();
    }

    private TextBox AddField(string label, int top, int tabIndex)
    {
        Controls.Add(DarkControls.CreateLabel(label, Edge, top));

        (Panel frame, TextBox field) = DarkControls.CreateField(tabIndex);
        frame.SetBounds(Edge, top + LabelGap, ClientSize.Width - (2 * Edge), DarkControls.FieldHeight);
        Controls.Add(frame);
        _frames.Add(frame);

        return field;
    }

    private ComboBox AddList(string label, int top, int tabIndex)
    {
        Controls.Add(DarkControls.CreateLabel(label, Edge, top));

        ComboBox list = DarkControls.CreateList(tabIndex);
        list.SetBounds(Edge, top + LabelGap, ClientSize.Width - (2 * Edge), list.Height);
        Controls.Add(list);

        return list;
    }

    private void SelectFilter(long? id)
    {
        int index = 0;

        for (int i = 0; i < _filter.Items.Count; i++)
        {
            if (_filter.Items[i] is FilterItem item && item.Id == id)
            {
                index = i;
                break;
            }
        }

        _filter.SelectedIndex = index;
    }

    // ---------- automatic filling ----------

    private void RepositoryChanged(object? sender, EventArgs e)
    {
        if (_filling)
        {
            return;
        }

        string text = _repository.Text;
        SetHint(DefaultHint, Theme.DimText);

        if (RepoAddress.LooksLikeAddress(text))
        {
            AddressMatch? match = RepoAddress.Parse(text, _sites);

            if (match is null)
            {
                SetHint("This address does not point at a repository.", Theme.Amber);
            }
            else if (match.Site is null)
            {
                _ = DetectSiteAsync(text, match.Host);
            }
            else
            {
                _filling = true;
                _site.SelectedIndex = _sites.IndexOf(match.Site);
                _repository.Text = match.Path;
                _repository.SelectionStart = _repository.Text.Length;
                _filling = false;
            }
        }

        FollowFolder();
    }

    /// <summary>
    /// An address from a host Git-Sync does not know: asks the server what it
    /// runs and, when it is a forge Git-Sync reads, adds it as a site and fills
    /// the fields as for a known one. An answer that arrives after the user
    /// typed something else is dropped.
    /// </summary>
    private async Task DetectSiteAsync(string text, string host)
    {
        int number = ++_detectNumber;
        SetHint($"Checking what {host} runs…", Theme.DimText);

        ApiType? type;
        string? problem = null;

        try
        {
            type = await SiteDetection.DetectAsync(host);
        }
        catch (ApiException error)
        {
            type = null;
            problem = error.Message;
        }

        if (number != _detectNumber || IsDisposed || _repository.Text != text)
        {
            return;
        }

        if (type is not { } api)
        {
            SetHint(problem ?? $"{host} is not a GitHub, GitLab or Forgejo/Gitea server.", Theme.Amber);
            return;
        }

        Site site = SiteStore.Add(host, api);

        if (!_sites.Any(s => s.Id == site.Id))
        {
            _sites.Add(site);
            _site.Items.Add(site);
        }

        // Read again with the site known: GitLab paths keep their nested groups.
        AddressMatch? match = RepoAddress.Parse(text, _sites);

        if (match?.Site is null)
        {
            SetHint("This address does not point at a repository.", Theme.Amber);
            return;
        }

        _filling = true;
        _site.SelectedIndex = _sites.FindIndex(s => s.Id == site.Id);
        _repository.Text = match.Path;
        _repository.SelectionStart = _repository.Text.Length;
        _filling = false;

        SetHint($"{host} runs {ApiName(api)}; it was added to the sites.", Theme.DimText);
        FollowFolder();
    }

    internal static string ApiName(ApiType type) => type switch
    {
        ApiType.GitHub => "GitHub",
        ApiType.GitLab => "GitLab",
        _ => "Forgejo / Gitea"
    };

    private void SetHint(string text, Color color)
    {
        _hint.Text = text;
        _hint.ForeColor = color;
    }

    /// <summary>
    /// "Name - repo" in Folder, as long as the field still holds what was put
    /// there automatically (or nothing).
    /// </summary>
    private void FollowFolder()
    {
        if (_filling)
        {
            return;
        }

        string suggestion = RepoAddress.SuggestFolder(_name.Text, RepoAddress.LooksLikeAddress(_repository.Text) ? string.Empty : _repository.Text);

        if (_folder.Text == _autoFolder || _folder.Text.Trim().Length == 0)
        {
            _filling = true;
            _folder.Text = suggestion;
            _filling = false;
        }

        _autoFolder = suggestion;
    }

    // ---------- saving ----------

    private void Save(object? sender, EventArgs e)
    {
        string name = _name.Text.Trim();

        if (name.Length == 0)
        {
            Warn("Enter a Name.", _name);
            return;
        }

        if (_site.SelectedItem is not Site site)
        {
            Warn("Choose a Site.", _site);
            return;
        }

        string path = RepoAddress.NormalizePath(_repository.Text);

        if (!RepoAddress.IsValidPath(path, site.Type))
        {
            Warn(site.Type == ApiType.GitLab
                    ? "Enter the project's path, like group/project, or paste its address."
                    : "Enter the repository as owner/repo, like morluto/rea, or paste its address.",
                _repository);
            return;
        }

        string folder = _folder.Text.Trim();

        if (!DownloadPaths.IsValidName(folder))
        {
            Warn("Enter a Folder name. It cannot contain \\ / : * ? \" < > | or end with a dot.", _folder);
            return;
        }

        long? filterId = (_filter.SelectedItem as FilterItem)?.Id;

        if (RepoStore.FindDuplicate(site.Id, path, _editing?.Id ?? 0) is { } duplicate)
        {
            Warn($"This repository is already in the list as \"{duplicate.Name}\".", _repository);
            return;
        }

        // A new Folder or Filter moves the download folder along, so the files
        // downloaded under the old name are still where Git-Sync looks. Done
        // before anything is saved: if the folder cannot move, the window
        // stays open with the old values still in effect. Not when another
        // repo shares the folder — its files would go along without it knowing.
        string toFolder = DownloadPaths.RepoFolder(new Repo { Folder = folder, FilterId = filterId }, _filters);
        bool moveFolder = _editing is not null && _startFolder is not null
            && !string.Equals(_startFolder, toFolder, StringComparison.Ordinal)
            && !RepoStore.List().Any(r => r.Id != _editing.Id
                && string.Equals(DownloadPaths.RepoFolder(r, _filters), _startFolder, StringComparison.OrdinalIgnoreCase));

        if (moveFolder && RepoFolderMove.Move(_startFolder!, toFolder) is { } problem)
        {
            Warn(problem, _folder);
            return;
        }

        Repo repo = _editing ?? new Repo();

        bool addressChanged = _editing is null
            || _editing.SiteId != site.Id
            || !string.Equals(_editing.Path, path, StringComparison.OrdinalIgnoreCase);

        repo.Name = name;
        repo.SiteId = site.Id;
        repo.Path = path;
        repo.Folder = folder;
        repo.FilterId = filterId;
        repo.KeepPrevious = _keepPrevious.Checked;
        repo.KeepVersions = KeepVersions();

        try
        {
            RepoStore.Save(repo);

            if (moveFolder)
            {
                // The saved paths of the files follow the folder.
                var moves = new PathMoves();
                moves.Add(_startFolder!, toFolder);
                RepoStore.SetFiles(repo.Id,
                    moves.Apply(RepoStore.Get(repo.Id)?.CurrentFiles ?? []),
                    moves.Apply(RepoStore.PreviousVersions(repo.Id)));
            }

            // What was checked and pinned belonged to the old address.
            if (_editing is not null && addressChanged)
            {
                RepoStore.ForgetRemoteState(repo.Id);
            }
        }
        catch (Exception error)
        {
            // Not saved: the folder goes back where the database still says it is.
            if (moveFolder)
            {
                RepoFolderMove.Move(toFolder, _startFolder!);
            }

            DarkMessageBox.Show(this, "Could not save the repository.\n" + error.Message,
                Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        Saved = repo;
        AddressChanged = addressChanged;
        DialogResult = DialogResult.OK;
    }

    private void Warn(string message, Control focus)
    {
        DarkMessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        focus.Focus();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tips.Dispose();
        }

        base.Dispose(disposing);
    }
}
