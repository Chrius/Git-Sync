using System.Text.RegularExpressions;

namespace GitSync;

/// <summary>What a pasted address turned out to be. <see cref="Site"/> is null for a host nobody configured.</summary>
internal sealed record AddressMatch(Site? Site, string Host, string Path);

/// <summary>
/// Turns what the user types or pastes in the Repository field into a site and
/// an "owner/repo" path, and suggests the download folder name.
/// </summary>
internal static partial class RepoAddress
{
    [GeneratedRegex(@"^[A-Za-z0-9_.\-]+$")]
    private static partial Regex SegmentPattern();

    /// <summary>
    /// True for text that is an address rather than a bare "owner/repo":
    /// "https://...", "git@host:...", or "github.com/owner/repo" without a scheme.
    /// </summary>
    internal static bool LooksLikeAddress(string text)
    {
        text = text.Trim();

        if (text.Contains("://", StringComparison.Ordinal) || text.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // A first segment with a dot is a host: owner names cannot have one on
        // GitHub, and "owner/repo" alone has only one slash.
        string[] segments = text.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 3 && segments[0].Contains('.', StringComparison.Ordinal);
    }

    /// <summary>
    /// The address inside text dragged onto the window. Browsers send the link
    /// alone, Firefox's own format is "address\ntitle", a desktop shortcut
    /// (.url) holds "URL=address", and UTF-16 clipboard data ends in a "\0".
    /// Null when no line is an address.
    /// </summary>
    internal static string? FirstLink(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (string raw in text.Split(['\r', '\n', '\0'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string line = raw.StartsWith("URL=", StringComparison.OrdinalIgnoreCase) ? raw[4..] : raw;

            // A sentence that happens to contain a link is not a dragged link.
            if (!line.Contains(' ', StringComparison.Ordinal) && LooksLikeAddress(line))
            {
                return line;
            }
        }

        return null;
    }

    /// <summary>Reads an address; null when it does not contain at least owner and repo.</summary>
    internal static AddressMatch? Parse(string text, IReadOnlyList<Site> sites)
    {
        text = text.Trim();
        string host;
        string rawPath;

        if (text.StartsWith("git@", StringComparison.OrdinalIgnoreCase)
            && !text.Contains("://", StringComparison.Ordinal)
            && text.IndexOf(':') is int colon and > 4)
        {
            // SSH form copied from the Clone button: git@github.com:owner/repo.git
            host = text[4..colon];
            rawPath = text[(colon + 1)..];
        }
        else
        {
            string withScheme = text.Contains("://", StringComparison.Ordinal) ? text : "https://" + text;

            if (!Uri.TryCreate(withScheme, UriKind.Absolute, out Uri? uri)
                || uri.Scheme is not ("http" or "https" or "ssh" or "git"))
            {
                return null;
            }

            host = uri.IsDefaultPort || uri.Port < 0 ? uri.Host : $"{uri.Host}:{uri.Port}";
            rawPath = Uri.UnescapeDataString(uri.AbsolutePath);
        }

        host = StripWww(host.ToLowerInvariant());

        Site? site = sites.FirstOrDefault(s =>
            string.Equals(StripWww(HostOnly(s.Host)), host, StringComparison.OrdinalIgnoreCase));

        string? path = ExtractPath(rawPath, site?.Type);

        return path is null ? null : new AddressMatch(site, host, path);
    }

    /// <summary>
    /// Keeps only the repository part of an address path.
    /// GitHub and Gitea: always the first two segments (".../releases/tag/v1"
    /// and ".../tree/main" are pages, not the repo). GitLab: groups can nest,
    /// so everything before the "/-/" that starts its page part.
    /// </summary>
    internal static string? ExtractPath(string rawPath, ApiType? type)
    {
        List<string> segments = [.. rawPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        int dash = segments.IndexOf("-");

        List<string> kept = type == ApiType.GitLab || (type is null && dash >= 0)
            ? (dash >= 0 ? segments[..dash] : segments)
            : [.. segments.Take(2)];

        if (kept.Count < 2)
        {
            return null;
        }

        kept[^1] = StripGitSuffix(kept[^1]);

        return kept.All(s => SegmentPattern().IsMatch(s)) ? string.Join('/', kept) : null;
    }

    /// <summary>Tidies a typed "owner/repo": no slashes at the ends, no ".git".</summary>
    internal static string NormalizePath(string text)
    {
        string trimmed = text.Trim().Trim('/');
        int slash = trimmed.LastIndexOf('/');

        return slash < 0
            ? StripGitSuffix(trimmed)
            : trimmed[..(slash + 1)] + StripGitSuffix(trimmed[(slash + 1)..]);
    }

    /// <summary>GitHub and Gitea paths are exactly owner/repo; GitLab ones can have nested groups.</summary>
    internal static bool IsValidPath(string path, ApiType type)
    {
        string[] segments = path.Split('/');

        bool countOk = type == ApiType.GitLab ? segments.Length >= 2 : segments.Length == 2;

        return countOk && segments.All(s => s.Length > 0 && SegmentPattern().IsMatch(s));
    }

    /// <summary>
    /// "Reverse Engineer Anything" + "morluto/rea" → "Reverse Engineer Anything - rea".
    /// The name is free text and the folder is not: characters a folder name
    /// cannot have are left out, so "Zelda 2: The Adventure of Link" gives
    /// "Zelda 2 The Adventure of Link - z2rs" instead of a folder Windows refuses.
    /// </summary>
    internal static string SuggestFolder(string name, string path)
    {
        string cleanName = DownloadPaths.CleanText(name);
        string repo = DownloadPaths.CleanText(LastSegment(NormalizePath(path)));

        string folder = repo.Length == 0 ? cleanName
            : cleanName.Length == 0 ? repo
            : $"{cleanName} - {repo}";

        // Dots are cut only from the end of the whole name, where Windows would drop them.
        return DownloadPaths.CleanName(folder);
    }

    internal static string LastSegment(string path)
    {
        string trimmed = path.Trim().TrimEnd('/');
        int slash = trimmed.LastIndexOf('/');
        return slash < 0 ? trimmed : trimmed[(slash + 1)..];
    }

    /// <summary>The host without the "http://" marker.</summary>
    internal static string HostOnly(string siteHost) =>
        siteHost.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? siteHost["http://".Length..] : siteHost;

    private static string StripWww(string host) =>
        host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;

    private static string StripGitSuffix(string segment) =>
        segment.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? segment[..^4] : segment;
}
