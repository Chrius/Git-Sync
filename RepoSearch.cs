using System.Globalization;

namespace GitSync;

/// <summary>Which repos the main list shows: the selected folder's, narrowed by the search box.</summary>
internal static class RepoSearch
{
    /// <summary>
    /// The repos of a folder and of every folder below it, so "Games" also
    /// shows what is filed under Nintendo › NES. No folder ("All") keeps them all.
    /// </summary>
    internal static List<Repo> InFolder(IEnumerable<Repo> repos, long? folderId, IReadOnlyList<FilterFolder> filters)
    {
        if (folderId is not { } id)
        {
            return [.. repos];
        }

        HashSet<long> ids = FilterStore.SubtreeIds(id, filters);
        return [.. repos.Where(r => r.FilterId is { } filter && ids.Contains(filter))];
    }

    /// <summary>The words of what was typed. No words means no search: every repo is kept.</summary>
    internal static string[] Words(string? query) =>
        string.IsNullOrWhiteSpace(query) ? [] : query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Every word must appear in the name or in the repository path, each one
    /// in either, so "zelda link" finds "The Legend of Zelda: A Link to the
    /// Past" although the two words are not side by side, and "nintendo zelda"
    /// can take one word from each. Case and accents are ignored ("pokemon"
    /// finds "Pokémon"); the fixed culture keeps that the same on any machine.
    /// </summary>
    internal static bool Matches(Repo repo, IReadOnlyList<string> words)
    {
        foreach (string word in words)
        {
            if (!Contains(repo.Name, word) && !Contains(repo.Path, word))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The repos that match, in the order given (the list sorts them afterwards).</summary>
    internal static List<Repo> Filter(IEnumerable<Repo> repos, string? query)
    {
        string[] words = Words(query);
        return words.Length == 0 ? [.. repos] : [.. repos.Where(r => Matches(r, words))];
    }

    private static bool Contains(string text, string word) =>
        CultureInfo.InvariantCulture.CompareInfo.IndexOf(text, word, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;
}
