using System.Globalization;

namespace GitSync;

/// <summary>The "Automation" settings: whether to check by itself, how often, and when everything was last checked.</summary>
internal sealed record AutoCheckSettings(bool Enabled, int Minutes, DateTimeOffset? LastFullCheck);

/// <summary>
/// Rules of the automatic check. The period counts from the last time every
/// repository was checked — by hand (Refresh with "All", or "Refresh
/// everything") or automatically — and that time is saved, so a period that
/// ran out while the app was closed is caught up right after it opens.
/// </summary>
internal static class AutoCheck
{
    /// <summary>The choices of the drop down, in its order.</summary>
    internal static readonly (int Minutes, string Text)[] Intervals =
    [
        (30, "30 minutes"),
        (60, "1 hour"),
        (180, "3 hours"),
        (360, "6 hours"),
        (720, "12 hours")
    ];

    internal const int DefaultMinutes = 60;

    private const string EnabledKey = "AutoCheck";
    private const string MinutesKey = "AutoCheckMinutes";
    private const string LastKey = "LastFullCheck";

    /// <summary>A saved period, or the default when it is missing or not one of the choices.</summary>
    internal static int ParseMinutes(string? text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes)
        && Intervals.Any(i => i.Minutes == minutes)
            ? minutes
            : DefaultMinutes;

    /// <summary>When the next check falls due; right away when nothing was ever checked.</summary>
    internal static DateTimeOffset NextCheck(DateTimeOffset? lastFullCheck, int minutes, DateTimeOffset now) =>
        lastFullCheck is { } last ? last.AddMinutes(minutes) : now;

    internal static bool IsDue(DateTimeOffset? lastFullCheck, int minutes, DateTimeOffset now) =>
        now >= NextCheck(lastFullCheck, minutes, now);

    internal static AutoCheckSettings Load()
    {
        DateTimeOffset? last = DateTimeOffset.TryParse(
            PreferenceStore.Get(LastKey), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset value)
            ? value
            : null;

        return new AutoCheckSettings(
            PreferenceStore.Get(EnabledKey) == "1",
            ParseMinutes(PreferenceStore.Get(MinutesKey)),
            last);
    }

    internal static void SetEnabled(bool enabled) => PreferenceStore.Set(EnabledKey, enabled ? "1" : "0");

    internal static void SetMinutes(int minutes) =>
        PreferenceStore.Set(MinutesKey, ParseMinutes(minutes.ToString(CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture));

    /// <summary>Every repository was just checked: the next period starts now.</summary>
    internal static void SetLastFullCheck(DateTimeOffset when) =>
        PreferenceStore.Set(LastKey, when.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
}
