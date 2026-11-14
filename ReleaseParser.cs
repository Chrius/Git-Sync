using System.Globalization;
using System.Text.Json;

namespace GitSync;

/// <summary>
/// Turns the JSON of each API into <see cref="Release"/> objects. No network
/// here, so every format can be tested from sample payloads.
///
/// Everything is read field by field with JsonDocument instead of
/// deserialized into classes: the three APIs name things differently, and a
/// missing field should leave a blank, not fail the whole list.
/// </summary>
internal static class ReleaseParser
{
    internal static List<Release> ParseList(ApiType type, string json, string repoPath)
    {
        using JsonDocument document = JsonDocument.Parse(json);

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("Expected a list of releases.");
        }

        return [.. document.RootElement.EnumerateArray().Select(e => ParseOne(type, e, repoPath))];
    }

    internal static Release ParseSingle(ApiType type, string json, string repoPath)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return ParseOne(type, document.RootElement, repoPath);
    }

    private static Release ParseOne(ApiType type, JsonElement e, string repoPath) => type switch
    {
        ApiType.GitHub => ParseGitHub(e, repoPath),
        ApiType.GitLab => ParseGitLab(e, repoPath),
        _ => ParseGitea(e, repoPath)
    };

    private static Release ParseGitHub(JsonElement e, string repoPath)
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
            Text(e, "html_url"), files, Text(e, "zipball_url"), repoPath);
    }

    private static Release ParseGitLab(JsonElement e, string repoPath)
    {
        string tag = Text(e, "tag_name");
        var files = new List<ReleaseFile>();
        string sourceZip = string.Empty;

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

            if (assets.TryGetProperty("sources", out JsonElement sources) && sources.ValueKind == JsonValueKind.Array)
            {
                sourceZip = sources.EnumerateArray()
                    .Where(s => Text(s, "format") == "zip")
                    .Select(s => Text(s, "url"))
                    .FirstOrDefault() ?? string.Empty;
            }
        }

        string page = e.TryGetProperty("_links", out JsonElement linksBlock) ? Text(linksBlock, "self") : string.Empty;

        // GitLab has no pre-release flag; an upcoming release is the closest thing.
        return Build(tag, Text(e, "name"),
            Date(e, "released_at") ?? Date(e, "created_at"),
            Flag(e, "upcoming_release"), draft: false,
            page, files, sourceZip, repoPath);
    }

    private static Release ParseGitea(JsonElement e, string repoPath)
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
            Text(e, "html_url"), files, Text(e, "zipball_url"), repoPath);
    }

    /// <summary>A release without uploaded files offers its source archive as the single file.</summary>
    private static Release Build(
        string tag, string title, DateTimeOffset? date, bool prerelease, bool draft,
        string page, List<ReleaseFile> files, string sourceZip, string repoPath)
    {
        VersionKind kind = VersionKind.Release;

        if (files.Count == 0 && sourceZip.Length > 0)
        {
            files.Add(new ReleaseFile(DownloadPaths.SourceFileName(repoPath), -1, sourceZip));
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
            Files = files
        };
    }

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
        string repoPath, string branch, string sha, DateTimeOffset? date, string page, string zipUrl)
    {
        string shortSha = sha.Length > 7 ? sha[..7] : sha;

        return new Release
        {
            Tag = sha,
            Title = branch.Length > 0 ? $"Source {branch} @ {shortSha}" : $"Source @ {shortSha}",
            Date = date,
            Kind = VersionKind.Commit,
            Branch = branch,
            PageUrl = page,
            Files = [new ReleaseFile(DownloadPaths.SourceFileName(repoPath), -1, zipUrl)]
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
