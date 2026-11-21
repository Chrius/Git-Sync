using System.Net;
using System.Net.Http.Headers;

namespace GitSync;

/// <summary>An error already worded for the user (tooltip of the red X, message boxes).</summary>
internal sealed class ApiException(string message) : Exception(message);

/// <summary>
/// Addresses of each API. Kept apart from the HTTP code so the tests can check
/// every address without touching the network.
/// </summary>
internal static class ApiUrls
{
    /// <summary>"https://host", or "http://host" when the site was saved with that prefix.</summary>
    internal static string WebRoot(Site site) =>
        site.Host.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? site.Host : "https://" + site.Host;

    private static bool IsGitHubCom(Site site) =>
        string.Equals(RepoAddress.HostOnly(site.Host), "github.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// github.com keeps its API on a separate host; GitHub Enterprise serves it
    /// under /api/v3 of its own host.
    /// </summary>
    internal static string ApiRoot(Site site) => site.Type switch
    {
        ApiType.GitHub => IsGitHubCom(site) ? "https://api.github.com" : WebRoot(site) + "/api/v3",
        ApiType.GitLab => WebRoot(site) + "/api/v4",
        _ => WebRoot(site) + "/api/v1"
    };

    /// <summary>
    /// GitLab addresses a project by its whole path encoded as one segment
    /// ("group%2Fsub%2Fproject"); the others take owner and repo as two segments.
    /// </summary>
    private static string RepoRoot(Site site, string path) => site.Type == ApiType.GitLab
        ? $"{ApiRoot(site)}/projects/{Uri.EscapeDataString(path)}"
        : $"{ApiRoot(site)}/repos/{EscapeSegments(path)}";

    internal static string Releases(Site site, string path, int count) => site.Type switch
    {
        ApiType.GitLab => $"{RepoRoot(site, path)}/releases?per_page={count}",
        ApiType.Gitea => $"{RepoRoot(site, path)}/releases?limit={count}",
        _ => $"{RepoRoot(site, path)}/releases?per_page={count}"
    };

    /// <summary>
    /// GitHub and Gitea take a tag with slashes as plain path segments;
    /// GitLab wants it encoded as one segment, like the project.
    /// </summary>
    internal static string ReleaseByTag(Site site, string path, string tag) => site.Type == ApiType.GitLab
        ? $"{RepoRoot(site, path)}/releases/{Uri.EscapeDataString(tag)}"
        : $"{RepoRoot(site, path)}/releases/tags/{EscapeSegments(tag)}";

    internal static string RepoInfo(Site site, string path) => RepoRoot(site, path);

    /// <summary>
    /// The README.md of a version (tag or commit). GitHub has an endpoint that
    /// finds the README whatever its case ("readme.md", "Readme.md"); the
    /// others are asked for the file "README.md" itself.
    /// </summary>
    internal static string Readme(Site site, string path, string reference) => site.Type switch
    {
        ApiType.GitLab => $"{RepoRoot(site, path)}/repository/files/README.md/raw?ref={Uri.EscapeDataString(reference)}",
        ApiType.Gitea => $"{RepoRoot(site, path)}/raw/README.md?ref={Uri.EscapeDataString(reference)}",
        _ => $"{RepoRoot(site, path)}/readme?ref={Uri.EscapeDataString(reference)}"
    };

    /// <summary>
    /// Where to ask how many requests are left. GitHub has an endpoint for it
    /// that does not count against the limit. The others put the limit on every
    /// answer, so the cheapest call there is "version" — which also rejects a
    /// wrong token, and is how the Check button tests one before it is saved.
    /// </summary>
    internal static string RateLimit(Site site) => site.Type == ApiType.GitHub
        ? $"{ApiRoot(site)}/rate_limit"
        : $"{ApiRoot(site)}/version";

    internal static string HeadCommit(Site site, string path, string branch) => site.Type switch
    {
        ApiType.GitLab => $"{RepoRoot(site, path)}/repository/commits/{Uri.EscapeDataString(branch)}",
        ApiType.Gitea => $"{RepoRoot(site, path)}/branches/{Uri.EscapeDataString(branch)}",
        _ => $"{RepoRoot(site, path)}/commits/{Uri.EscapeDataString(branch)}"
    };

    internal static string SourceZip(Site site, string path, string sha) => site.Type switch
    {
        ApiType.GitLab => $"{RepoRoot(site, path)}/repository/archive.zip?sha={Uri.EscapeDataString(sha)}",
        ApiType.Gitea => $"{RepoRoot(site, path)}/archive/{Uri.EscapeDataString(sha)}.zip",
        _ => $"{RepoRoot(site, path)}/zipball/{Uri.EscapeDataString(sha)}"
    };

    internal static string SourceTarGz(Site site, string path, string sha) => site.Type switch
    {
        ApiType.GitLab => $"{RepoRoot(site, path)}/repository/archive.tar.gz?sha={Uri.EscapeDataString(sha)}",
        ApiType.Gitea => $"{RepoRoot(site, path)}/archive/{Uri.EscapeDataString(sha)}.tar.gz",
        _ => $"{RepoRoot(site, path)}/tarball/{Uri.EscapeDataString(sha)}"
    };

    /// <summary>The repository's own page: release texts link the site's source archives under it.</summary>
    internal static string ProjectPage(Site site, string path) => $"{WebRoot(site)}/{path}";

    /// <summary>
    /// GitLab: where a file attached to a release text ("/uploads/…") downloads
    /// from, the project's API address. Under the project's page, where the
    /// text's own link points, gitlab.com now answers 404; the API serves the
    /// file by the project's path, without its number.
    /// </summary>
    internal static string? UploadsRoot(Site site, string path) =>
        site.Type == ApiType.GitLab ? RepoRoot(site, path) : null;

    /// <summary>The releases page in the browser.</summary>
    internal static string ReleasesPage(Site site, string path) => site.Type == ApiType.GitLab
        ? $"{WebRoot(site)}/{path}/-/releases"
        : $"{WebRoot(site)}/{path}/releases";

    /// <summary>
    /// Whether the site's token may go along with a request to this address.
    /// Only the site's own host (and api.github.com for github.com) gets it:
    /// release files often live elsewhere — GitLab links can point at any
    /// server, and GitHub redirects downloads to a storage host — and none of
    /// those should ever see the token. A switch from https to http also drops it.
    /// </summary>
    internal static bool ShouldSendToken(Site site, Uri url)
    {
        if (site.Token.Length == 0)
        {
            return false;
        }

        var siteRoot = new Uri(WebRoot(site));

        if (!string.Equals(url.Scheme, siteRoot.Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(url.Authority, siteRoot.Authority, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IsGitHubCom(site) && string.Equals(url.Host, "api.github.com", StringComparison.OrdinalIgnoreCase);
    }

    private static string EscapeSegments(string path) =>
        string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
}

/// <summary>Talks to the three APIs. Every failure comes out as an <see cref="ApiException"/>.</summary>
internal static class ApiClient
{
    internal const int RefreshCount = 20;
    internal const int MenuCount = 100;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            // Redirects are followed by hand in SendAsync, so the token can be
            // decided again for every hop. The automatic follower keeps custom
            // headers such as GitLab's PRIVATE-TOKEN on the way to another host.
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(20),

            // IPv6 and IPv4 side by side: .NET's own connect tries them one
            // after the other and never reached IPv4 behind a silent IPv6.
            ConnectCallback = (context, cancel) => Connector.ConnectAsync(context.DnsEndPoint, cancel)
        };

        // Timeouts are per request (and per read during downloads), not one
        // global value that would cut a large download in the middle.
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

        // GitHub refuses requests without a User-Agent.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Git-Sync/1.0");

        return client;
    }

    /// <summary>
    /// True when the deadline that ran out was ConnectTimeout, which the handler
    /// marks with a TimeoutException inside: no connection was ever made, a
    /// different trouble from a site that connects and then does not answer.
    /// </summary>
    internal static bool ConnectTimedOut(OperationCanceledException error) =>
        error.InnerException is TimeoutException;

    private static string TimedOut(Site site, OperationCanceledException error) =>
        ConnectTimedOut(error)
            ? $"Could not connect to {RepoAddress.HostOnly(site.Host)} in time."
            : "The site did not answer in time.";

    private static void AddAuth(HttpRequestMessage request, Site site)
    {
        switch (site.Type)
        {
            case ApiType.GitHub:
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", site.Token);
                break;

            case ApiType.GitLab:
                request.Headers.Add("PRIVATE-TOKEN", site.Token);
                break;

            default:
                request.Headers.Authorization = new AuthenticationHeaderValue("token", site.Token);
                break;
        }
    }

    /// <summary>GET that follows redirects by hand, deciding about the token on every hop.</summary>
    internal static async Task<HttpResponseMessage> SendAsync(Site site, string url, string accept, CancellationToken cancel)
    {
        var address = new Uri(url);

        for (int hop = 0; hop < 10; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            request.Headers.Accept.ParseAdd(accept);

            if (site.Type == ApiType.GitHub && address.Host.StartsWith("api.", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            }

            if (ApiUrls.ShouldSendToken(site, address))
            {
                AddAuth(request, site);
            }

            HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);

            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location)
            {
                address = location.IsAbsoluteUri ? location : new Uri(address, location);
                response.Dispose();
                continue;
            }

            return response;
        }

        throw new ApiException("The site redirected too many times.");
    }

    /// <summary>Short text for the tooltip of the red X.</summary>
    internal static string Describe(Site site, HttpResponseMessage response)
    {
        int code = (int)response.StatusCode;

        bool rateLimited = code == 429
            || (code == 403
                && response.Headers.TryGetValues("X-RateLimit-Remaining", out IEnumerable<string>? remaining)
                && remaining.FirstOrDefault() == "0");

        if (rateLimited)
        {
            return site.Token.Length == 0
                ? $"Rate limit reached on {site.Host}. Add a token for it in Settings, or wait a while."
                : $"Rate limit reached on {site.Host}. Wait a while and try again.";
        }

        return code switch
        {
            401 => $"{site.Host} rejected the token (401). Check it in Settings.",
            403 => "Access denied (403).",
            404 => "Not found (404). Check the repository name.",
            _ => $"The site answered {code} {response.ReasonPhrase}."
        };
    }

    private static string JsonAccept(Site site) =>
        site.Type == ApiType.GitHub ? "application/vnd.github+json" : "application/json";

    /// <summary>The body of a JSON answer, or null for 404/409 when <paramref name="missingIsNull"/>.</summary>
    internal static async Task<string?> GetJsonAsync(Site site, string url, CancellationToken cancel, bool missingIsNull = false)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(RequestTimeout);

        try
        {
            using HttpResponseMessage response = await SendAsync(site, url, JsonAccept(site), timeout.Token);

            // 409 is GitHub's answer for the commits of an empty repository.
            if (missingIsNull && response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new ApiException(Describe(site, response));
            }

            return await response.Content.ReadAsStringAsync(timeout.Token);
        }
        catch (OperationCanceledException error) when (!cancel.IsCancellationRequested)
        {
            throw new ApiException(TimedOut(site, error));
        }
        catch (HttpRequestException error)
        {
            throw new ApiException("Could not connect: " + error.Message);
        }
    }

    private static T Parse<T>(Func<T> parse)
    {
        try
        {
            return parse();
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or FormatException or InvalidOperationException)
        {
            throw new ApiException("The site sent an answer Git-Sync does not understand.");
        }
    }

    internal static async Task<List<Release>> ListReleasesAsync(Site site, Repo repo, int count, CancellationToken cancel = default)
    {
        string json = await GetJsonAsync(site, ApiUrls.Releases(site, repo.Path, count), cancel) ?? "[]";
        return Parse(() => ReleaseParser.ParseList(site.Type, json, repo.Path,
            ApiUrls.ProjectPage(site, repo.Path), ApiUrls.UploadsRoot(site, repo.Path)));
    }

    /// <summary>
    /// The head of the default branch as a source-only version, for repos with
    /// no releases. Null when the repo is empty.
    /// </summary>
    internal static async Task<Release?> GetSourceHeadAsync(Site site, Repo repo, CancellationToken cancel = default)
    {
        string info = await GetJsonAsync(site, ApiUrls.RepoInfo(site, repo.Path), cancel) ?? "{}";
        string branch = Parse(() => ReleaseParser.ParseDefaultBranch(info));

        if (branch.Length == 0)
        {
            return null;
        }

        string? commitJson = await GetJsonAsync(site, ApiUrls.HeadCommit(site, repo.Path, branch), cancel, missingIsNull: true);

        if (commitJson is null)
        {
            return null;
        }

        var head = Parse(() => ReleaseParser.ParseHeadCommit(site.Type, commitJson));

        return head is { } h
            ? ReleaseParser.SourceHead(repo.Path, branch, h.Sha, h.Date, h.Page,
                ApiUrls.SourceZip(site, repo.Path, h.Sha), ApiUrls.SourceTarGz(site, repo.Path, h.Sha))
            : null;
    }

    /// <summary>What Refresh shows as Latest; null when the repo has neither releases nor commits.</summary>
    internal static async Task<Release?> CheckLatestAsync(Site site, Repo repo, CancellationToken cancel = default)
    {
        // "Ignore pre-releases": a project that ships nightlies can fill the
        // first 20 with pre-releases, hiding its last stable one; a whole page
        // is read before deciding there is none.
        List<Release> releases = await ListReleasesAsync(site, repo, repo.IgnorePrereleases ? MenuCount : RefreshCount, cancel);

        if (ReleaseParser.PickLatest(releases, repo.IgnorePrereleases) is { } latest)
        {
            return latest;
        }

        // Only pre-releases, and they are ignored: no Latest, rather than the
        // newest commit, which would be offered as an update instead.
        return repo.IgnorePrereleases && releases.Any(r => !r.Draft)
            ? null
            : await GetSourceHeadAsync(site, repo, cancel);
    }

    /// <summary>Every version for the right-click menu, newest first.</summary>
    internal static async Task<List<Release>> ListVersionsAsync(Site site, Repo repo, CancellationToken cancel = default)
    {
        List<Release> releases = [.. ReleaseParser.NewestFirst(
            (await ListReleasesAsync(site, repo, MenuCount, cancel)).Where(r => !r.Draft))];

        if (releases.Count > 0)
        {
            return releases;
        }

        Release? head = await GetSourceHeadAsync(site, repo, cancel);
        return head is null ? [] : [head];
    }

    /// <summary>
    /// How many requests are left with this site's token (or without one, when
    /// it has none). Null when the site does not say. A token the site refuses
    /// comes out as an <see cref="ApiException"/>.
    /// </summary>
    internal static async Task<RateLimit?> GetRateLimitAsync(Site site, CancellationToken cancel = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(RequestTimeout);

        try
        {
            using HttpResponseMessage response = await SendAsync(site, ApiUrls.RateLimit(site), JsonAccept(site), timeout.Token);
            int code = (int)response.StatusCode;

            if (code == 401 && site.Token.Length > 0)
            {
                throw new ApiException($"{site.Host} rejected this token (401).");
            }

            // GitLab answers "version" with 401 when there is no token, but
            // the limit headers come all the same.
            bool expectedRefusal = code == 401 && site.Type == ApiType.GitLab;

            if (!response.IsSuccessStatusCode && !expectedRefusal)
            {
                throw new ApiException(Describe(site, response));
            }

            if (site.Type == ApiType.GitHub)
            {
                string json = await response.Content.ReadAsStringAsync(timeout.Token);
                return Parse(() => RateLimits.ParseGitHub(json));
            }

            return RateLimits.FromHeaders(name => HeaderValue(response, name), DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException error) when (!cancel.IsCancellationRequested)
        {
            throw new ApiException(TimedOut(site, error));
        }
        catch (HttpRequestException error)
        {
            throw new ApiException("Could not connect: " + error.Message);
        }
    }

    /// <summary>
    /// The README.md of the version <paramref name="reference"/> (a tag, or a
    /// commit id), or null when the repo has none. A README in another format
    /// (README, README.rst) counts as none: only README.md is wanted.
    /// </summary>
    internal static async Task<byte[]?> GetReadmeAsync(Site site, Repo repo, string reference, CancellationToken cancel = default)
    {
        string url = ApiUrls.Readme(site, repo.Path, reference);

        if (site.Type == ApiType.GitHub)
        {
            string? json = await GetJsonAsync(site, url, cancel, missingIsNull: true);
            return json is null ? null : Parse(() => ReleaseParser.ReadmeFromGitHub(json));
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(RequestTimeout);

        try
        {
            using HttpResponseMessage response = await SendAsync(site, url, "*/*", timeout.Token);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new ApiException(Describe(site, response));
            }

            return await response.Content.ReadAsByteArrayAsync(timeout.Token);
        }
        catch (OperationCanceledException error) when (!cancel.IsCancellationRequested)
        {
            throw new ApiException(TimedOut(site, error));
        }
        catch (HttpRequestException error)
        {
            throw new ApiException("Could not connect: " + error.Message);
        }
    }

    private static string? HeaderValue(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out IEnumerable<string>? values) ? values.FirstOrDefault() : null;

    /// <summary>
    /// The release of a stored version, fetched again so its file list is
    /// current. A commit needs no request: its archive address is known.
    /// </summary>
    internal static async Task<Release> GetReleaseAsync(Site site, Repo repo, VersionRef version, CancellationToken cancel = default)
    {
        if (version.Kind == VersionKind.Commit)
        {
            return ReleaseParser.SourceHead(
                repo.Path, string.Empty, version.Tag, version.Date, string.Empty,
                ApiUrls.SourceZip(site, repo.Path, version.Tag), ApiUrls.SourceTarGz(site, repo.Path, version.Tag));
        }

        string? json = await GetJsonAsync(site, ApiUrls.ReleaseByTag(site, repo.Path, version.Tag), cancel, missingIsNull: true)
            ?? throw new ApiException($"Version {version.Tag} is no longer on the site.");

        return Parse(() => ReleaseParser.ParseSingle(site.Type, json, repo.Path,
            ApiUrls.ProjectPage(site, repo.Path), ApiUrls.UploadsRoot(site, repo.Path)));
    }
}
