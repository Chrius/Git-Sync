using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GitSync;

/// <summary>
/// Turns the JSON of each API into <see cref="Release"/> objects. No network
/// here, so every format can be tested from sample payloads.
///
/// Everything is read field by field with JsonDocument instead of
/// deserialized into classes: the three APIs name things differently, and a
/// missing field should leave a blank, not fail the whole list.
/// </summary>
internal static partial class ReleaseParser
{
    /// <param name="projectUrl">
    /// The repository's web address ("https://gitlab.com/group/project"), for
    /// links in the release text: GitLab writes attached files as
    /// "/uploads/…" under it, and the site's own source archives live under
    /// it too. Null reads only absolute links and skips no archives.
    /// </param>
    internal static List<Release> ParseList(ApiType type, string json, string repoPath, string? projectUrl = null)
    {
        using JsonDocument document = JsonDocument.Parse(json);

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("Expected a list of releases.");
        }

        return [.. document.RootElement.EnumerateArray().Select(e => ParseOne(type, e, repoPath, projectUrl))];
    }

    internal static Release ParseSingle(ApiType type, string json, string repoPath, string? projectUrl = null)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return ParseOne(type, document.RootElement, repoPath, projectUrl);
    }

    private static Release ParseOne(ApiType type, JsonElement e, string repoPath, string? projectUrl) => type switch
    {
        ApiType.GitHub => ParseGitHub(e, repoPath, projectUrl),
        ApiType.GitLab => ParseGitLab(e, repoPath, projectUrl),
        _ => ParseGitea(e, repoPath, projectUrl)
    };

    private static Release ParseGitHub(JsonElement e, string repoPath, string? projectUrl)
    {
        string tag = Text(e, "tag_name");
        var files = new List<ReleaseFile>();

        if (e.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                files.Add(new ReleaseFile(
                    Text(asset, "name"), Number(asset, "size"),
                    Text(asset, "browser_download_url"), Text(asset, "url")));
            }
        }

        return Build(tag, Text(e, "name"),
            Date(e, "published_at") ?? Date(e, "created_at"),
            Flag(e, "prerelease"), Flag(e, "draft"),
            Text(e, "html_url"), files, Text(e, "zipball_url"), Text(e, "tarball_url"), repoPath,
            Text(e, "body"), projectUrl);
    }

    private static Release ParseGitLab(JsonElement e, string repoPath, string? projectUrl)
    {
        string tag = Text(e, "tag_name");
        var files = new List<ReleaseFile>();
        string sourceZip = string.Empty;
        string sourceTar = string.Empty;

        if (e.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Object)
        {
            if (assets.TryGetProperty("links", out JsonElement links) && links.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement link in links.EnumerateArray())
                {
                    // direct_asset_url is the permanent address; url may point at a page.
                    string url = Text(link, "direct_asset_url");
                    files.Add(new ReleaseFile(Text(link, "name"), -1, url.Length > 0 ? url : Text(link, "url")));
                }
            }

            // GitLab also lists tar.bz2 and tar; only the two formats the other
            // sites offer are kept, so the choice looks the same everywhere.
            if (assets.TryGetProperty("sources", out JsonElement sources) && sources.ValueKind == JsonValueKind.Array)
            {
                string SourceOf(string format) => sources.EnumerateArray()
                    .Where(s => Text(s, "format") == format)
                    .Select(s => Text(s, "url"))
                    .FirstOrDefault() ?? string.Empty;

                sourceZip = SourceOf("zip");
                sourceTar = SourceOf("tar.gz");
            }
        }

        string page = e.TryGetProperty("_links", out JsonElement linksBlock) ? Text(linksBlock, "self") : string.Empty;

        // GitLab has no pre-release flag; an upcoming release is the closest thing.
        return Build(tag, Text(e, "name"),
            Date(e, "released_at") ?? Date(e, "created_at"),
            Flag(e, "upcoming_release"), draft: false,
            page, files, sourceZip, sourceTar, repoPath,
            Text(e, "description"), projectUrl);
    }

    private static Release ParseGitea(JsonElement e, string repoPath, string? projectUrl)
    {
        string tag = Text(e, "tag_name");
        var files = new List<ReleaseFile>();

        if (e.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                files.Add(new ReleaseFile(Text(asset, "name"), Number(asset, "size"), Text(asset, "browser_download_url")));
            }
        }

        return Build(tag, Text(e, "name"),
            Date(e, "published_at") ?? Date(e, "created_at"),
            Flag(e, "prerelease"), Flag(e, "draft"),
            Text(e, "html_url"), files, Text(e, "zipball_url"), Text(e, "tarball_url"), repoPath,
            Text(e, "body"), projectUrl);
    }

    /// <summary>
    /// The uploaded files and those linked from the release text; a release
    /// with neither offers its source archive as the single file.
    /// </summary>
    private static Release Build(
        string tag, string title, DateTimeOffset? date, bool prerelease, bool draft,
        string page, List<ReleaseFile> files, string sourceZip, string sourceTar, string repoPath,
        string notes, string? projectUrl)
    {
        VersionKind kind = VersionKind.Release;
        List<ReleaseFile> sources = SourceFiles(repoPath, sourceZip, sourceTar);
        files.AddRange(LinkedFiles(notes, files, projectUrl));

        if (files.Count == 0 && sources.Count > 0)
        {
            files.Add(sources[0]);
            kind = VersionKind.ReleaseSource;
        }

        return new Release
        {
            Tag = tag,
            Title = title,
            Date = date,
            Prerelease = prerelease,
            Draft = draft,
            Kind = kind,
            PageUrl = page,
            Files = files,
            Sources = sources
        };
    }

    /// <summary>The source archives that have an address, zip first: the zip is the one used when only one is offered.</summary>
    private static List<ReleaseFile> SourceFiles(string repoPath, string zipUrl, string tarUrl)
    {
        var sources = new List<ReleaseFile>();

        if (zipUrl.Length > 0)
        {
            sources.Add(new ReleaseFile(DownloadPaths.SourceFileName(repoPath, ".zip"), -1, zipUrl, IsSource: true));
        }

        if (tarUrl.Length > 0)
        {
            sources.Add(new ReleaseFile(DownloadPaths.SourceFileName(repoPath, ".tar.gz"), -1, tarUrl, IsSource: true));
        }

        return sources;
    }

    // ---------- files linked from the release text ----------

    /// <summary>
    /// Endings of a file worth downloading. A link counts only when its
    /// address ends in one: links to commits, changelogs, pages and pictures
    /// in the same text do not.
    /// </summary>
    private static readonly string[] DownloadTypes =
    [
        ".zip", ".7z", ".rar", ".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle", ".appimage",
        ".tar", ".gz", ".tgz", ".xz", ".txz", ".bz2", ".tbz2", ".zst",
        ".dmg", ".pkg", ".apk", ".aab", ".ipa", ".deb", ".rpm", ".flatpak", ".snap", ".jar", ".iso"
    ];

    internal static bool IsDownloadName(string name) =>
        DownloadTypes.Any(type => name.Length > type.Length && name.EndsWith(type, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Files linked from the release's own text. Some projects upload nothing
    /// to the release and link their builds from its description instead
    /// (Eden's CI: eight builds, all on another server), and such a release
    /// looked like source code only. Each link keeps the file name its
    /// address ends with; the size is unknown until it downloads.
    ///
    /// Left out: links that are not files (see <see cref="DownloadTypes"/>),
    /// the site's own source archives (offered by "Show source code"), and a
    /// name already uploaded to the release or linked earlier in the text.
    /// </summary>
    internal static List<ReleaseFile> LinkedFiles(string notes, IEnumerable<ReleaseFile> uploaded, string? projectUrl)
    {
        var found = new List<ReleaseFile>();

        if (string.IsNullOrWhiteSpace(notes))
        {
            return found;
        }

        var names = new HashSet<string>(uploaded.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
        string project = (projectUrl ?? string.Empty).TrimEnd('/');

        // A description written in HTML escapes the & of a query.
        foreach (Match match in LinkPattern().Matches(notes.Replace("&amp;", "&", StringComparison.Ordinal)))
        {
            string address = match.Groups["absolute"].Success
                ? match.Groups["absolute"].Value.TrimEnd('.', ',', ';', ':', '!', '?')
                : project.Length > 0 ? project + match.Groups["upload"].Value : string.Empty;

            if (!Uri.TryCreate(address, UriKind.Absolute, out Uri? uri)
                || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                continue;
            }

            if (project.Length > 0
                && (uri.AbsoluteUri.StartsWith(project + "/archive/", StringComparison.OrdinalIgnoreCase)
                    || uri.AbsoluteUri.StartsWith(project + "/-/archive/", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            string path = uri.AbsolutePath;
            string name = Uri.UnescapeDataString(path[(path.LastIndexOf('/') + 1)..]);

            if (IsDownloadName(name) && names.Add(name))
            {
                found.Add(new ReleaseFile(name, -1, uri.AbsoluteUri));
            }
        }

        return found;
    }

    /// <summary>
    /// An address written out in the text (markdown link, bare or in HTML: it
    /// stops at a space, bracket, quote, backtick, asterisk or table bar), or
    /// a GitLab attachment, which markdown links as "(/uploads/…)".
    /// </summary>
    [GeneratedRegex(@"(?<absolute>https?://[^\s<>()\[\]{}""'`*|\\]+)|\]\((?<upload>/uploads/[^)\s]+)\)", RegexOptions.IgnoreCase)]
    private static partial Regex LinkPattern();

    /// <summary>
    /// The release shown as Latest: the newest stable one, or the newest
    /// pre-release when the repo publishes nothing else (many emulators only
    /// ship nightly pre-releases). Drafts never count.
    /// </summary>
    internal static Release? PickLatest(IEnumerable<Release> releases)
    {
        List<Release> published = [.. NewestFirst(releases.Where(r => !r.Draft))];

        return published.FirstOrDefault(r => !r.Prerelease) ?? published.FirstOrDefault();
    }

    /// <summary>
    /// Newest first by date; releases without a date go last, keeping the
    /// order the API gave (OrderBy is stable).
    /// </summary>
    internal static IEnumerable<Release> NewestFirst(IEnumerable<Release> releases) =>
        releases.OrderByDescending(r => r.Date ?? DateTimeOffset.MinValue);

    // ---------- repos without releases ----------

    internal static string ParseDefaultBranch(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return Text(document.RootElement, "default_branch");
    }

    /// <summary>
    /// Commit id, date and page of a branch head. Each API answers a
    /// different endpoint with a different shape.
    /// </summary>
    internal static (string Sha, DateTimeOffset? Date, string Page)? ParseHeadCommit(ApiType type, string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        switch (type)
        {
            case ApiType.GitHub:
            {
                // GET /repos/{o}/{r}/commits/{branch}
                string sha = Text(root, "sha");
                DateTimeOffset? date = null;

                if (root.TryGetProperty("commit", out JsonElement commit))
                {
                    date = (commit.TryGetProperty("committer", out JsonElement committer) ? Date(committer, "date") : null)
                        ?? (commit.TryGetProperty("author", out JsonElement author) ? Date(author, "date") : null);
                }

                return sha.Length == 0 ? null : (sha, date, Text(root, "html_url"));
            }

            case ApiType.GitLab:
            {
                // GET /projects/{id}/repository/commits/{branch}
                string sha = Text(root, "id");
                return sha.Length == 0 ? null : (sha, Date(root, "committed_date"), Text(root, "web_url"));
            }

            default:
            {
                // GET /repos/{o}/{r}/branches/{branch}
                if (!root.TryGetProperty("commit", out JsonElement commit))
                {
                    return null;
                }

                string sha = Text(commit, "id");
                return sha.Length == 0 ? null : (sha, Date(commit, "timestamp"), Text(commit, "url"));
            }
        }
    }

    /// <summary>The branch head as a version whose single file is the source archive of that commit.</summary>
    internal static Release SourceHead(
        string repoPath, string branch, string sha, DateTimeOffset? date, string page, string zipUrl, string tarUrl = "")
    {
        string shortSha = sha.Length > 7 ? sha[..7] : sha;
        List<ReleaseFile> sources = SourceFiles(repoPath, zipUrl, tarUrl);

        return new Release
        {
            Tag = sha,
            Title = branch.Length > 0 ? $"Source {branch} @ {shortSha}" : $"Source @ {shortSha}",
            Date = date,
            Kind = VersionKind.Commit,
            Branch = branch,
            PageUrl = page,
            Files = [.. sources.Take(1)],
            Sources = sources
        };
    }

    /// <summary>
    /// The content of GitHub's "/readme" answer, when the README it found is a
    /// README.md in any case. A plain "README" or "README.rst" gives null:
    /// only README.md is saved. The content comes in base64, broken into lines.
    /// </summary>
    internal static byte[]? ReadmeFromGitHub(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        if (!string.Equals(Text(root, "name"), "README.md", StringComparison.OrdinalIgnoreCase)
            || Text(root, "encoding") != "base64")
        {
            return null;
        }

        string content = Text(root, "content").Replace("\n", string.Empty, StringComparison.Ordinal);
        return Convert.FromBase64String(content);
    }

    // ---------- field readers ----------

    private static string Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object
        && e.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static long Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out long number)
            ? number
            : -1;

    private static bool Flag(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    private static DateTimeOffset? Date(JsonElement e, string name)
    {
        string text = Text(e, name);

        return text.Length > 0 && DateTimeOffset.TryParse(
            text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset date)
            ? date
            : null;
    }
}
