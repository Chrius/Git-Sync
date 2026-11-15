using System.Globalization;
using System.Drawing;
using System.Windows.Forms;

namespace GitSync;

/// <summary>
/// "Sites" tab: the token of each supported API.
///
/// Git-Sync works with three fixed sites — github.com, gitlab.com and
/// codeberg.org — so there is nothing to add or delete here, only a token per
/// site. Nothing goes to the database before Save. Save and Cancel are
/// enabled only while the token differs from the saved one; picking another
/// API in the drop down is not a change.
/// </summary>
internal sealed class SitesTab : UserControl
{
    private sealed record SiteItem(Site Site)
    {
        public override string ToString() => Site.Type switch
        {
            ApiType.GitHub => $"GitHub ({Site.Host})",
            ApiType.GitLab => $"GitLab ({Site.Host})",
            _ => $"Forgejo ({Site.Host})"
        };
    }

    private const int LabelGap = 18;
    private const int TopApi = 6;
    private const int TopToken = 58;
    private const int TopShow = 108;
    private const int TopHint = 140;
    private const int TopLimit = 226;

    private readonly ComboBox _api;
    private readonly Panel _tokenFrame;
    private readonly TextBox _token;
    private readonly CheckBox _showToken;
    private readonly Label _hint;
    private readonly Label _limit;
    private readonly FlatButton _check;
    private readonly Button _save;
    private readonly Button _cancel;

    /// <summary>True while the screen fills itself, so it is not taken for user edits.</summary>
    private bool _filling;

    /// <summary>
    /// Number of the latest limit check. An answer that arrives after the
    /// user picked another API belongs to the old one and is dropped.
    /// </summary>
    private int _checkNumber;

    internal SitesTab()
    {
        Dock = DockStyle.Fill;

        Controls.Add(DarkControls.CreateLabel("API", 0, TopApi));
        _api = DarkControls.CreateList(0);
        _api.SelectedIndexChanged += (_, _) => ShowSelected();
        Controls.Add(_api);

        Controls.Add(DarkControls.CreateLabel("Token", 0, TopToken));
        (_tokenFrame, _token) = DarkControls.CreateField(1);
        _token.UseSystemPasswordChar = true;
        _token.TextChanged += (_, _) => UpdateButtons();
        Controls.Add(_tokenFrame);

        _showToken = new CheckBox
        {
            Text = "Show token",
            AutoSize = true,
            Location = new Point(0, TopShow),
            TabIndex = 2
        };
        _showToken.CheckedChanged += (_, _) => _token.UseSystemPasswordChar = !_showToken.Checked;
        Controls.Add(_showToken);

        _hint = new Label { AutoSize = false, ForeColor = Theme.DimText, Location = new Point(0, TopHint) };
        Controls.Add(_hint);

        Controls.Add(DarkControls.CreateLabel("Requests left", 0, TopLimit));

        _limit = new Label
        {
            AutoSize = false,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.DimText,
            Location = new Point(0, TopLimit + LabelGap)
        };
        Controls.Add(_limit);

        // Checks with what is in the Token field, so a new token can be tried before Save.
        _check = new FlatButton { Text = "Check", Size = new Size(80, DarkControls.FieldHeight), TabIndex = 3 };
        _check.Click += (_, _) => CheckLimit();
        Controls.Add(_check);

        _save = new Button { Text = "Save", Size = new Size(88, 28), Enabled = false, TabIndex = 100 };
        _save.Click += Save;
        Controls.Add(_save);

        _cancel = new Button { Text = "Cancel", Size = new Size(88, 28), Enabled = false, TabIndex = 101 };
        _cancel.Click += (_, _) => ShowSelected();
        Controls.Add(_cancel);

        Reload(0);
    }

    /// <summary>Laid out from the real size (see FiltersTab: Anchor computes from the wrong width here).</summary>
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);

        int width = ClientSize.Width;
        int height = ClientSize.Height;

        if (width <= 100 || height <= 0)
        {
            return;
        }

        _api.SetBounds(0, TopApi + LabelGap, width, _api.Height);
        _tokenFrame.SetBounds(0, TopToken + LabelGap, width, DarkControls.FieldHeight);
        _hint.SetBounds(0, TopHint, width, 80);
        _check.Location = new Point(width - _check.Width, TopLimit + LabelGap);
        _limit.SetBounds(0, TopLimit + LabelGap, Math.Max(0, _check.Left - 8), DarkControls.FieldHeight);

        int buttonsTop = height - _save.Height - 4;
        _cancel.Location = new Point(width - _cancel.Width, buttonsTop);
        _save.Location = new Point(_cancel.Left - _save.Width - 8, buttonsTop);
    }

    /// <summary>
    /// Picks up servers added by an import. Skipped while a token edit is
    /// unsaved, which a reload would throw away; the new servers then show
    /// the next time Settings opens.
    /// </summary>
    internal void ReloadSites()
    {
        if (!_save.Enabled)
        {
            Reload(_api.SelectedIndex);
        }
    }

    private void Reload(int select)
    {
        _filling = true;
        _api.Items.Clear();

        foreach (Site site in SiteStore.List())
        {
            _api.Items.Add(new SiteItem(site));
        }

        _api.SelectedIndex = _api.Items.Count == 0 ? -1 : Math.Clamp(select, 0, _api.Items.Count - 1);
        _filling = false;

        ShowSelected();
    }

    private Site? Selected => (_api.SelectedItem as SiteItem)?.Site;

    /// <summary>Shows the saved token of the selected site; also what Cancel goes back to.</summary>
    private void ShowSelected()
    {
        if (_filling)
        {
            return;
        }

        _filling = true;
        _token.Text = Selected?.Token ?? string.Empty;
        _filling = false;

        // Named after the selected site: a detected self-hosted server has its
        // own address, and only github.com has GitHub's 60-per-hour limit.
        string host = Selected?.Host ?? string.Empty;

        _hint.Text = Selected?.Type switch
        {
            ApiType.GitHub when host == "github.com" =>
                "Without a token GitHub allows 60 requests per hour, which a long list uses up quickly. "
                + "A fine-grained token with read-only access to public repositories is enough "
                + "(github.com → Settings → Developer settings).",
            ApiType.GitHub =>
                "A GitHub Enterprise server. A token with read-only access to repositories also reaches "
                + $"private ones ({host} → Settings → Developer settings).",
            ApiType.GitLab =>
                "Public projects work without a token. A personal access token with the read_api "
                + "scope raises the request limit and also reaches private projects "
                + $"({host} → Preferences → Access tokens).",
            ApiType.Gitea =>
                "Public repositories work without a token. An access token with read permission on "
                + $"repositories also reaches private ones ({host} → Settings → Applications).",
            _ => string.Empty
        };

        if (Selected is { } site && !SiteStore.BuiltInHosts.Contains(site.Host, StringComparer.OrdinalIgnoreCase))
        {
            _hint.Text += "\nAdded on its own when a repository from it was added; it goes away when no repository uses it.";
        }

        UpdateButtons();
        CheckLimit();
    }

    /// <summary>
    /// Asks the selected site how many requests are left, with the token that
    /// is in the field (saved or not). On GitHub this does not use a request;
    /// on the others it uses one, out of hundreds per minute.
    /// </summary>
    private async void CheckLimit()
    {
        if (Selected is not { } saved)
        {
            return;
        }

        int number = ++_checkNumber;
        var site = new Site { Id = saved.Id, Host = saved.Host, Type = saved.Type, Token = _token.Text.Trim() };

        _limit.ForeColor = Theme.DimText;
        _limit.Text = "Checking…";
        _check.Enabled = false;

        string text;
        Color color;

        try
        {
            RateLimit? limit = await ApiClient.GetRateLimitAsync(site);

            (text, color) = limit is null
                ? ($"{site.Host} did not say how many requests are left.", Theme.DimText)
                : (Describe(limit, site.Token.Length > 0), limit.Remaining == 0 ? Theme.Amber : Theme.Text);
        }
        catch (ApiException error)
        {
            (text, color) = (error.Message, Theme.Error);
        }

        // Another API was picked meanwhile, or the window closed.
        if (number != _checkNumber || IsDisposed)
        {
            return;
        }

        _limit.Text = text;
        _limit.ForeColor = color;
        _check.Enabled = true;
    }

    /// <summary>"4,987 of 5,000 left with this token · resets at 15:42".</summary>
    private static string Describe(RateLimit limit, bool withToken)
    {
        string text = string.Create(CultureInfo.InvariantCulture,
            $"{limit.Remaining:N0} of {limit.Limit:N0} left {(withToken ? "with this token" : "without a token")}");

        if (limit.Reset is { } reset)
        {
            text += " · resets at " + reset.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        return text;
    }

    private void UpdateButtons()
    {
        if (_filling)
        {
            return;
        }

        bool changed = Selected is { } site && _token.Text.Trim() != site.Token;
        _save.Enabled = changed;
        _cancel.Enabled = changed;
    }

    private void Save(object? sender, EventArgs e)
    {
        if (Selected is not { } site)
        {
            return;
        }

        try
        {
            SiteStore.SetToken(site.Id, _token.Text.Trim());
        }
        catch (Exception error)
        {
            DarkMessageBox.Show(this, "Could not save the token.\n" + error.Message,
                "Settings", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        Reload(_api.SelectedIndex);
    }
}
