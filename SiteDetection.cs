using System.Text.Json;

namespace GitSync;

/// <summary>
/// Finds out which software a self-hosted git server runs, so a link from a
/// host Git-Sync does not know (git.magicbean.dev, gitlab.gnome.org...) can be
/// added without the user filling in a host or an API. The API belongs to the
/// software, not to the site: a Forgejo server answers like Codeberg, a
/// self-hosted GitLab like gitlab.com.
///
/// Each answer is judged by a separate function, so the rules can be tested
/// from real samples without the network.
/// </summary>
internal static class SiteDetection
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Forgejo and Gitea answer "/api/v1/version" with {"version": "..."}.
    /// GitLab sends that address to its sign-in page (HTML), GitHub to an
    /// error page, so only a JSON object with a version counts.
    /// </summary>
    internal static bool IsGitea(int status, string body) =>
        status == 200 && JsonHasString(body, "version");

    /// <summary>
    /// GitLab answers "/api/v4/version" with 401 when there is no token, but
    /// marks every API answer with an "X-Gitlab-Meta" header. A 200 with both
    /// "version" and "revision" is GitLab's own answer too.
    /// </summary>
    internal static bool IsGitLab(int status, Func<string, string?> header, string body) =>
        header("X-Gitlab-Meta") is not null
        || (status == 200 && JsonHasString(body, "version") && JsonHasString(body, "revision"));

    /// <summary>GitHub Enterprise names its own version in "/api/v3/meta"; github.com does not.</summary>
    internal static bool IsGitHubEnterprise(int status, string body) =>
        status == 200 && JsonHasString(body, "installed_version");

    private static bool JsonHasString(string body, string property)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(property, out JsonElement value)
                && value.ValueKind == JsonValueKind.String;
        }
        catch (JsonException)
        {
            // An HTML page, a plain-text 404: not the API being probed.
            return false;
        }
    }

    /// <summary>
    /// The API the host speaks, or null when it is none Git-Sync reads.
    /// Forgejo/Gitea is asked first, as the most common self-hosted forge for
    /// game and emulator projects. A host that cannot be reached at all comes
    /// out as an <see cref="ApiException"/>, without trying the other two.
    /// </summary>
    internal static async Task<ApiType?> DetectAsync(string host, CancellationToken cancel = default)
    {
        // No token: nothing about the user goes to a server not yet known.
        var probe = new Site { Host = host, Type = ApiType.Gitea };
        string root = ApiUrls.WebRoot(probe);

        (int status, string body, _) = await GetAsync(probe, $"{root}/api/v1/version", cancel);

        if (IsGitea(status, body))
        {
            return ApiType.Gitea;
        }

        (status, body, Func<string, string?> header) = await GetAsync(probe, $"{root}/api/v4/version", cancel);

        if (IsGitLab(status, header, body))
        {
            return ApiType.GitLab;
        }

        (status, body, _) = await GetAsync(probe, $"{root}/api/v3/meta", cancel);

        return IsGitHubEnterprise(status, body) ? ApiType.GitHub : null;
    }

    private static async Task<(int Status, string Body, Func<string, string?> Header)> GetAsync(
        Site probe, string url, CancellationToken cancel)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(ProbeTimeout);

        try
        {
            using HttpResponseMessage response = await ApiClient.SendAsync(probe, url, "application/json", timeout.Token);
            string body = await response.Content.ReadAsStringAsync(timeout.Token);

            // Copied out: the response is disposed when this returns.
            var headers = response.Headers.ToDictionary(h => h.Key, h => h.Value.FirstOrDefault(), StringComparer.OrdinalIgnoreCase);

            return ((int)response.StatusCode, body, name => headers.GetValueOrDefault(name));
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new ApiException($"{probe.Host} did not answer in time.");
        }
        catch (HttpRequestException error)
        {
            throw new ApiException($"Could not reach {probe.Host}: {error.Message}");
        }
    }
}
