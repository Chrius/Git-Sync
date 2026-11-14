using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GitSync;

/// <param name="Limit">Requests allowed in the current window.</param>
/// <param name="Remaining">Requests left in it.</param>
/// <param name="Reset">When the window starts over, when the site says.</param>
internal sealed record RateLimit(int Limit, int Remaining, DateTimeOffset? Reset);

/// <summary>
/// Reads how many API requests are left. Each site says it differently:
///
/// - GitHub: GET /rate_limit, "resources.core" in the JSON. That call does
///   not count against the limit.
/// - GitLab: "RateLimit-Limit", "RateLimit-Remaining" and "RateLimit-Reset"
///   (Unix time) on every API answer, even a 401.
/// - Codeberg (Forgejo): the newer IETF form, "RateLimit-Policy:
///   "baseline";q=2000;w=600" and "RateLimit: "baseline";r=1999;t=600" —
///   q is the quota, r what is left and t the seconds until the reset.
///
/// No network here, so every form can be tested from samples.
/// </summary>
internal static partial class RateLimits
{
    internal static RateLimit? ParseGitHub(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty("resources", out JsonElement resources)
            || !resources.TryGetProperty("core", out JsonElement core)
            || !core.TryGetProperty("limit", out JsonElement limit)
            || !core.TryGetProperty("remaining", out JsonElement remaining))
        {
            return null;
        }

        DateTimeOffset? reset = core.TryGetProperty("reset", out JsonElement epoch) && epoch.TryGetInt64(out long seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;

        return new RateLimit(limit.GetInt32(), remaining.GetInt32(), reset);
    }

    [GeneratedRegex(@"(?:^|;)\s*q\s*=\s*(\d+)")]
    private static partial Regex QuotaPattern();

    [GeneratedRegex(@"(?:^|;)\s*r\s*=\s*(\d+)")]
    private static partial Regex RemainingPattern();

    [GeneratedRegex(@"(?:^|;)\s*t\s*=\s*(\d+)")]
    private static partial Regex SecondsPattern();

    /// <summary>
    /// The limit from response headers, in either form; null when the answer
    /// carries none. <paramref name="header"/> returns a header's value, or
    /// null when it is missing.
    /// </summary>
    internal static RateLimit? FromHeaders(Func<string, string?> header, DateTimeOffset now)
    {
        // GitLab (and GitHub's X- variant): plain numbers in separate headers.
        string? limitText = header("RateLimit-Limit") ?? header("X-RateLimit-Limit");
        string? remainingText = header("RateLimit-Remaining") ?? header("X-RateLimit-Remaining");

        if (TryNumber(limitText, out long limit) && TryNumber(remainingText, out long remaining))
        {
            string? resetText = header("RateLimit-Reset") ?? header("X-RateLimit-Reset");
            return new RateLimit((int)limit, (int)remaining, ResetFrom(resetText, now));
        }

        // Forgejo: structured fields, quota in the policy and the rest in RateLimit.
        string? policy = header("RateLimit-Policy");
        string? state = header("RateLimit");

        if (policy is not null && state is not null
            && TryMatch(QuotaPattern(), policy, out long quota)
            && TryMatch(RemainingPattern(), state, out long left))
        {
            DateTimeOffset? reset = TryMatch(SecondsPattern(), state, out long seconds) ? now.AddSeconds(seconds) : null;
            return new RateLimit((int)quota, (int)left, reset);
        }

        return null;
    }

    /// <summary>
    /// A reset given either as a Unix time (GitLab, GitHub) or as seconds from
    /// now (what the IETF draft uses). Anything before 2001 in Unix time can
    /// only be a count of seconds.
    /// </summary>
    private static DateTimeOffset? ResetFrom(string? text, DateTimeOffset now)
    {
        if (!TryNumber(text, out long value))
        {
            return null;
        }

        return value > 1_000_000_000 ? DateTimeOffset.FromUnixTimeSeconds(value) : now.AddSeconds(value);
    }

    private static bool TryNumber(string? text, out long value) =>
        long.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static bool TryMatch(Regex pattern, string text, out long value)
    {
        Match match = pattern.Match(text);
        value = 0;
        return match.Success && TryNumber(match.Groups[1].Value, out value);
    }
}
