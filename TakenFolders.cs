using System.Globalization;

namespace GitSync;

/// <summary>
/// The download folders of a set of repositories, for giving a new or moved
/// one a folder of its own: "Name - repo", or "Name - repo 2", "3"... when
/// another repository already has it in that filter.
///
/// A folder clashes as <see cref="DownloadPaths.FolderConflict"/> says: the
/// same folder, inside another's Old folder, or with another inside its own
/// Old. Here the folders are gathered once and each question costs a few
/// lookups: the Add window asks on every key typed in Name, an import once
/// per repository, and comparing with every repository each time would grow
/// with the square of a list of thousands.
/// </summary>
internal sealed class TakenFolders
{
    private readonly HashSet<string> _folders = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _olds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every folder and every folder above it: tells whether one sits inside a given folder.</summary>
    private readonly HashSet<string> _withParents = new(StringComparer.OrdinalIgnoreCase);

    private readonly IReadOnlyList<FilterFolder> _filters;

    internal TakenFolders(IEnumerable<Repo> repos, IReadOnlyList<FilterFolder> filters)
    {
        _filters = filters;

        foreach (Repo repo in repos)
        {
            Add(DownloadPaths.RepoFolder(repo, filters));
        }
    }

    /// <summary>One more folder taken: a repository added during an import.</summary>
    internal void Add(string folder)
    {
        string full = DownloadPaths.FullFolder(folder);
        _folders.Add(full);
        _olds.Add(Path.Combine(full, OldFolder.Name));

        for (string? above = full; above is not null; above = Path.GetDirectoryName(above))
        {
            _withParents.Add(above);
        }
    }

    internal bool Clashes(string folder)
    {
        string mine = DownloadPaths.FullFolder(folder);

        if (_folders.Contains(mine) || _withParents.Contains(Path.Combine(mine, OldFolder.Name)))
        {
            return true;
        }

        for (string? above = mine; above is not null; above = Path.GetDirectoryName(above))
        {
            if (_olds.Contains(above))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The Folder to give a repository in this filter: <paramref name="folder"/>
    /// itself when no other repository clashes with it, or the same with
    /// " 2", " 3"... after it. The number goes after the whole Folder, after
    /// its last subfolder when it has some.
    /// </summary>
    internal string Unique(string folder, long? filterId)
    {
        if (folder.Length == 0)
        {
            return folder;
        }

        string candidate = folder;

        for (int number = 2; number < 10_000 && Clashes(DownloadPaths.RepoFolder(new Repo { Folder = candidate, FilterId = filterId }, _filters)); number++)
        {
            candidate = $"{folder} {number.ToString(CultureInfo.InvariantCulture)}";
        }

        return candidate;
    }

    /// <summary>
    /// <paramref name="folder"/> is the suggestion itself or the suggestion
    /// numbered ("Ky - ky 2"): the folder Git-Sync gave it, not one typed.
    /// </summary>
    internal static bool IsNumberedForm(string folder, string suggestion)
    {
        if (string.Equals(folder, suggestion, StringComparison.Ordinal))
        {
            return true;
        }

        if (suggestion.Length == 0 || !folder.StartsWith(suggestion + " ", StringComparison.Ordinal))
        {
            return false;
        }

        string number = folder[(suggestion.Length + 1)..];
        return number.Length > 0 && number.All(char.IsAsciiDigit) && number[0] != '0'
            && int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value >= 2;
    }
}
