namespace GitSync;

/// <summary>
/// Files and folders that were moved, full path before and after, so the paths
/// saved in the database (relative to the exe) can follow them.
/// </summary>
internal sealed class PathMoves
{
    private readonly List<(string From, string To)> _moves = [];

    internal int Count => _moves.Count;

    internal void Add(string from, string to) => _moves.Add((Full(from), Full(to)));

    private static string Full(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);

    /// <summary>A saved path after the moves: a moved file, or a file inside a moved folder.</summary>
    internal string Apply(string relative)
    {
        string full = Full(DownloadPaths.ToAbsolute(relative));

        foreach ((string from, string to) in _moves)
        {
            if (string.Equals(full, from, StringComparison.OrdinalIgnoreCase))
            {
                return DownloadPaths.ToRelative(to);
            }

            if (full.StartsWith(from + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return DownloadPaths.ToRelative(to + full[from.Length..]);
            }
        }

        return relative;
    }

    internal List<string> Apply(IEnumerable<string> relatives) => [.. relatives.Select(Apply)];

    internal List<PreviousVersion> Apply(IEnumerable<PreviousVersion> versions) =>
        [.. versions.Select(v => v with { Files = Apply(v.Files) })];
}

/// <summary>
/// "Move previous versions to a different folder" (Automation tab): before a
/// new version of a repository that keeps previous versions is put in place,
/// everything in its folder goes into "Old" inside it, so the folder itself
/// holds only the version just downloaded.
/// </summary>
internal static class OldFolder
{
    internal const string Name = "Old";

    private const string Readme = "README.md";

    /// <summary>
    /// Moves every file and folder in <paramref name="folder"/> into Old,
    /// except Old itself, README.md (the new version's replaces it) and the
    /// paths in <paramref name="stay"/> — the ".part" files of the download
    /// under way. Something that cannot be moved (open in another program)
    /// stays where it is, with a warning; the update goes on.
    /// </summary>
    internal static (PathMoves Moves, List<string> Warnings) MoveAside(string folder, IEnumerable<string> stay)
    {
        var moves = new PathMoves();
        var warnings = new List<string>();

        if (!Directory.Exists(folder))
        {
            return (moves, warnings);
        }

        string old = Path.Combine(folder, Name);
        var staying = new HashSet<string>(stay.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(old)
        };

        List<string> entries = [.. Directory.EnumerateFileSystemEntries(folder)
            .Where(e => !staying.Contains(Path.GetFullPath(e)) && !IsReadme(e) && !IsWindowsMetadata(e))];

        if (entries.Count == 0)
        {
            return (moves, warnings);
        }

        if (File.Exists(old))
        {
            warnings.Add($"A file named \"{Name}\" is in the way, so the previous files stay where they are.");
            return (moves, warnings);
        }

        Directory.CreateDirectory(old);

        foreach (string entry in entries)
        {
            bool isFolder = Directory.Exists(entry);
            string target = FreeName(Path.Combine(old, Path.GetFileName(entry)), isFolder);

            try
            {
                if (isFolder)
                {
                    Directory.Move(entry, target);
                }
                else
                {
                    File.Move(entry, target);
                }

                moves.Add(entry, target);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Could not move {Path.GetFileName(entry)} into {Name}: {error.Message}");
            }
        }

        return (moves, warnings);
    }

    private static bool IsReadme(string path) =>
        string.Equals(Path.GetFileName(path), Readme, StringComparison.OrdinalIgnoreCase) && File.Exists(path);

    /// <summary>
    /// desktop.ini and Thumbs.db: hidden system files Windows keeps for the
    /// folder itself (its icon, its thumbnails), not anything of a version.
    /// </summary>
    internal static bool IsWindowsMetadata(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        FileAttributes both = FileAttributes.Hidden | FileAttributes.System;
        return (File.GetAttributes(path) & both) == both;
    }

    /// <summary>
    /// The path itself, or "name (2).ext", "name (3).ext"… when Old already
    /// has that name — a file moved there on an earlier update is never
    /// overwritten. A folder gets "name (2)".
    /// </summary>
    internal static string FreeName(string path, bool isFolder)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return path;
        }

        string parent = Path.GetDirectoryName(path) ?? string.Empty;
        string name = Path.GetFileName(path);
        (string stem, string extension) = isFolder ? (name, string.Empty) : DownloadPaths.SplitExtension(name);

        for (int n = 2; ; n++)
        {
            string candidate = Path.Combine(parent, $"{stem} ({n}){extension}");

            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }
    }
}

/// <summary>
/// Which kept versions stay on disk after a download, with "Keep previous
/// versions" and a number of versions to keep.
/// </summary>
internal static class VersionHistory
{
    /// <param name="kept">Versions kept so far, oldest first, with their files where they are now.</param>
    /// <param name="replaced">The version that was current until this download, with its files; null if none.</param>
    /// <param name="downloaded">The version just downloaded.</param>
    /// <param name="downloadedFiles">Its files.</param>
    /// <param name="keepVersions">How many versions in all, the new one included; 0 keeps every one.</param>
    /// <param name="exists">Whether a saved path is still on disk.</param>
    /// <returns>The versions to keep listed, oldest first, and those whose files are to be deleted.</returns>
    internal static (List<PreviousVersion> Keep, List<PreviousVersion> Delete) Plan(
        IReadOnlyList<PreviousVersion> kept, PreviousVersion? replaced, VersionRef downloaded,
        IReadOnlyList<string> downloadedFiles, int keepVersions, Func<string, bool> exists)
    {
        var current = new HashSet<string>(downloadedFiles.Select(Normalize), StringComparer.OrdinalIgnoreCase);
        var keep = new List<PreviousVersion>();
        var delete = new List<PreviousVersion>();

        foreach (PreviousVersion version in kept)
        {
            // An older version picked and downloaded again: the copy kept from
            // before is not kept twice beside it.
            (version.SameBuild(downloaded) ? delete : keep).Add(version);
        }

        if (replaced is not null && !replaced.SameBuild(downloaded))
        {
            keep.Add(replaced);
        }

        // A file the new version was saved over belongs to it now; one that
        // is gone from the disk (deleted by hand) is no longer a version kept.
        // A version left with no file is forgotten, and does not count.
        keep = [.. keep
            .Select(v => v with { Files = [.. v.Files.Where(f => !current.Contains(Normalize(f)) && exists(f))] })
            .Where(v => v.Files.Count > 0)];

        delete = [.. delete.Select(v => v with { Files = [.. v.Files.Where(f => !current.Contains(Normalize(f)))] })];

        if (keepVersions > 0 && keep.Count > keepVersions - 1)
        {
            int extra = keep.Count - (keepVersions - 1);
            delete.AddRange(keep.Take(extra));
            keep = [.. keep.Skip(extra)];
        }

        return (keep, delete);
    }

    private static string Normalize(string path) => path.Replace('/', '\\');
}

/// <summary>
/// A download folder follows its new name or place: a repository's when Edit
/// changes its Folder or Filter, a folder of the tree's when Settings → Filters
/// renames or moves it. The files downloaded under the old name are then still
/// where Git-Sync looks for them.
/// </summary>
internal static class RepoFolderMove
{
    /// <summary>
    /// Moves <paramref name="from"/> to <paramref name="to"/>. Nothing to do
    /// when they are the same or nothing was ever downloaded. Returns what to
    /// tell the user when the folder could not be moved, or null when it was
    /// (or did not need to be).
    /// </summary>
    internal static string? Move(string from, string to)
    {
        from = Path.GetFullPath(from).TrimEnd(Path.DirectorySeparatorChar);
        to = Path.GetFullPath(to).TrimEnd(Path.DirectorySeparatorChar);

        if (string.Equals(from, to, StringComparison.Ordinal) || !Directory.Exists(from))
        {
            return null;
        }

        // Paths come from names typed in Edit, never from outside; checked
        // anyway, as every delete is, since a move empties the old place.
        if (!DownloadPaths.IsInsideDownloads(from) || !DownloadPaths.IsInsideDownloads(to))
        {
            return "That folder is outside the download folder, so it was not moved.";
        }

        // "Ky" to "KY" is the same folder to Windows: renamed in place.
        bool sameIgnoringCase = string.Equals(from, to, StringComparison.OrdinalIgnoreCase);

        try
        {
            if (!sameIgnoringCase)
            {
                if (File.Exists(to) || (Directory.Exists(to) && Directory.EnumerateFileSystemEntries(to).Any()))
                {
                    return $"Something by that name is already there:\n{to}\n\n"
                         + "Choose another name, or move it away first.";
                }

                // Left empty by something else: taken over.
                if (Directory.Exists(to))
                {
                    Directory.Delete(to);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            }

            Directory.Move(from, to);
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return $"Could not move the download folder\n{from}\nto\n{to}\n\n{error.Message}\n\n"
                 + "Close any program that is using it and try again.";
        }
    }
}
