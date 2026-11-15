using System.Text.Json;

namespace GitSync;

/// <summary>Git-Sync's own newest release on GitHub.</summary>
internal sealed record AppRelease(string Tag, Version Version);

/// <summary>
/// "Check for updates on launch" (Automation tab): asks GitHub for the newest
/// Git-Sync release and decides whether to offer it. A version the user chose
/// to skip is not offered again; one newer than it is.
/// </summary>
internal static class AppUpdate
{
    internal const string Repository = "Chrius/Git-Sync";

    /// <summary>
    /// The list of releases rather than the newest one's page: someone a few
    /// versions behind sees every change since theirs.
    /// </summary>
    internal const string ReleasesPage = "https://github.com/" + Repository + "/releases";

    /// <summary>GitHub's newest release that is neither a draft nor a pre-release.</summary>
    internal const string LatestUrl = "https://api.github.com/repos/" + Repository + "/releases/latest";

    private const string EnabledKey = "CheckUpdatesOnLaunch";
    private const string SkippedKey = "SkippedUpdate";

    /// <summary>On until it is unticked: only "0" turns it off, so a database from before the option checks too.</summary>
    internal static bool Enabled() => PreferenceStore.Get(EnabledKey) != "0";

    internal static void SetEnabled(bool on) => PreferenceStore.Set(EnabledKey, on ? "1" : "0");

    /// <summary>The version "Skip update" was last pressed for, or null.</summary>
    internal static Version? Skipped() => ParseTag(PreferenceStore.Get(SkippedKey));

    internal static void Skip(AppRelease release) => PreferenceStore.Set(SkippedKey, release.Tag);

    /// <summary>This exe's version: &lt;Version&gt; in Git-Sync.csproj, which every release bumps before building.</summary>
    internal static Version Running => Normalize(typeof(AppUpdate).Assembly.GetName().Version ?? new Version(0, 0));

    /// <summary>
    /// "v1.0.6", "1.0.6" or "v1.0.6-beta" → 1.0.6. Missing parts count as 0,
    /// so "v1.1" equals the exe's 1.1.0.0. Null for a tag that is no version.
    /// </summary>
    internal static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        string text = tag.Trim();

        if (text.StartsWith('v') || text.StartsWith('V'))
        {
            text = text[1..];
        }

        int suffix = text.IndexOfAny(['-', '+', ' ']);

        if (suffix >= 0)
        {
            text = text[..suffix];
        }

        // Version.Parse wants at least two parts.
        if (!text.Contains('.'))
        {
            text += ".0";
        }

        return Version.TryParse(text, out Version? version) ? Normalize(version) : null;
    }

    /// <summary>
    /// Version leaves the parts that were not written as -1, and 1.0.6 would
    /// then be smaller than 1.0.6.0 — the exe's own version has all four.
    /// </summary>
    private static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));

    /// <summary>"1.0.6", with the fourth part only when it is used.</summary>
    internal static string Display(Version version) =>
        version.Revision > 0 ? version.ToString(4) : version.ToString(3);

    /// <summary>
    /// Offered when newer than this exe and than the skipped version. Newer
    /// than the skipped one, not just different: if that release were taken
    /// down, an older one between it and this exe stays skipped too.
    /// </summary>
    internal static bool ShouldOffer(AppRelease latest, Version running, Version? skipped) =>
        latest.Version > running && (skipped is null || latest.Version > skipped);

    /// <summary>The answer of <see cref="LatestUrl"/>; null when its tag is not a version.</summary>
    internal static AppRelease? ParseLatest(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("tag_name", out JsonElement tagElement)
            || tagElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        // The endpoint never returns these; checked anyway, since offering a
        // draft would send the user to a page they cannot see.
        if (IsTrue(root, "draft") || IsTrue(root, "prerelease"))
        {
            return null;
        }

        string tag = tagElement.GetString() ?? string.Empty;
        return ParseTag(tag) is { } version ? new AppRelease(tag, version) : null;
    }

    private static bool IsTrue(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    /// <summary>
    /// The newest release, or null when there is none. Asked without the
    /// user's github.com token: one request per launch fits well inside
    /// GitHub's limit without one, and an expired token would make every
    /// check fail. Throws ApiException when GitHub cannot be reached.
    /// </summary>
    internal static async Task<AppRelease?> LatestAsync(CancellationToken cancel)
    {
        var github = new Site { Host = "github.com", Type = ApiType.GitHub };
        string? json = await ApiClient.GetJsonAsync(github, LatestUrl, cancel, missingIsNull: true);

        if (json is null)
        {
            return null;
        }

        try
        {
            return ParseLatest(json);
        }
        catch (JsonException)
        {
            throw new ApiException("GitHub sent an answer Git-Sync does not understand.");
        }
    }
}
