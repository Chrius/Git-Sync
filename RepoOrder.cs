namespace GitSync;

/// <summary>Order of the main list, chosen in the "Sort by" drop down.</summary>
internal enum RepoOrder
{
    Name,

    /// <summary>Repos with nothing downloaded yet first, then by name.</summary>
    NotDownloadedFirst,

    /// <summary>Repos with a newer version than the one on disk first, then by name.</summary>
    UpdatesFirst,

    /// <summary>
    /// Repos pinned to a version other than Latest first, then by name. A pin
    /// on the Latest version itself changes nothing, so it is not singled out.
    /// </summary>
    SelectedFirst
}

internal static class RepoSorting
{
    internal static List<Repo> Sort(IEnumerable<Repo> repos, RepoOrder order)
    {
        // Within each group, by name without its leading article. The fixed
        // culture keeps the order the same on any machine. The full name, then
        // the id, break ties ("The Witcher" and "Witcher"), so rows never swap
        // places from one redraw to the next.
        return [.. repos
            .OrderBy(r => InFirstGroup(r, order) ? 0 : 1)
            .ThenBy(r => SortKey(r.Name), StringComparer.InvariantCultureIgnoreCase)
            .ThenBy(r => r.Name, StringComparer.InvariantCultureIgnoreCase)
            .ThenBy(r => r.Id)];
    }

    /// <summary>"An" before "A": otherwise "An ..." would be tried as "A" + "n ...".</summary>
    private static readonly string[] Articles = ["The", "An", "A"];

    /// <summary>
    /// The name as it is sorted: without a leading "The", "A" or "An", the way
    /// a library shelves "The Legend of Zelda" under L. The article must be a
    /// whole word ("Anachronox" and "Theme Hospital" keep their first letter),
    /// and a name that is nothing but the article stays as it is.
    /// </summary>
    internal static string SortKey(string name)
    {
        string trimmed = name.Trim();

        foreach (string article in Articles)
        {
            if (trimmed.Length > article.Length
                && trimmed.StartsWith(article, StringComparison.OrdinalIgnoreCase)
                && char.IsWhiteSpace(trimmed[article.Length]))
            {
                string rest = trimmed[article.Length..].TrimStart();
                return rest.Length > 0 ? rest : trimmed;
            }
        }

        return trimmed;
    }

    private static bool InFirstGroup(Repo repo, RepoOrder order) => order switch
    {
        RepoOrder.NotDownloadedFirst => repo.Current is null,
        RepoOrder.UpdatesFirst => VersionState.StatusOf(repo) == RepoStatus.Update,
        RepoOrder.SelectedFirst => repo.Selected is { } selected
            && (repo.Latest is not { } latest || !VersionState.SameVersion(selected, latest)),
        _ => false
    };

    /// <summary>The saved name of an order, or <see cref="RepoOrder.Name"/> for anything unknown.</summary>
    internal static RepoOrder Parse(string? text) =>
        Enum.TryParse(text, ignoreCase: true, out RepoOrder order) && Enum.IsDefined(order) ? order : RepoOrder.Name;
}
