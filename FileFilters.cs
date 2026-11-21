using System.Text.RegularExpressions;

namespace GitSync;

/// <summary>
/// "Ignored files" (Settings → Files): file names hidden when choosing what
/// to download, in the download window and in Update All, until "Show All
/// Files" is clicked there.
///
/// One pattern per line. * stands for any text and ? for any one character,
/// and a pattern is matched against the whole file name, ignoring case, as in
/// a file search: "*.tar.gz" hides every .tar.gz file, "*-linux-*" every file
/// with "-linux-" in its name.
/// </summary>
internal sealed class FileFilters
{
    private readonly Regex[] _patterns;

    internal FileFilters(IEnumerable<string> patterns) => _patterns = [.. patterns.Select(ToRegex)];

    internal static FileFilters None { get; } = new([]);

    internal int Count => _patterns.Length;

    /// <summary>The patterns of the Files tab's text: one per line, blank lines and spaces around them dropped, each once.</summary>
    internal static List<string> Parse(string? text) =>
    [
        .. (text ?? string.Empty)
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
    ];

    internal static FileFilters FromText(string? text) => new(Parse(text));

    /// <summary>The patterns saved in the Files tab, read again each time: a change there applies to the next window.</summary>
    internal static FileFilters Saved() => FromText(PreferenceStore.IgnoredFiles());

    internal bool Hides(string fileName) => _patterns.Any(pattern => pattern.IsMatch(fileName));

    /// <summary>
    /// Not backtracking: a pattern of many stars against a long name cannot
    /// make matching take forever, whatever was typed in the Files tab.
    /// </summary>
    private static Regex ToRegex(string pattern) => new(
        "^" + Regex.Escape(pattern).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", ".", StringComparison.Ordinal) + "$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking | RegexOptions.Singleline);
}
