namespace GitSync;

/// <summary>
/// Where downloads go: Apps beside the exe, or the folder chosen in Settings →
/// Data → "Use custom download folder". Every download path starts here.
/// </summary>
internal static class DownloadRoot
{
    private const string UseKey = "UseCustomDownloadFolder";
    private const string PathKey = "CustomDownloadFolder";

    // Read once and kept: every repo's folder is worked out from it, many
    // times over in a check of a long list.
    private static string? _current;

    /// <summary>The download folder in use, full path.</summary>
    internal static string Current => _current ??= Custom() ?? AppPaths.AppsDir;

    /// <summary>The custom folder when the option is on and one was chosen; otherwise null.</summary>
    internal static string? Custom() =>
        PreferenceStore.Get(UseKey) == "1" && PreferenceStore.Get(PathKey) is { Length: > 0 } path
            ? Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar)
            : null;

    /// <summary>
    /// A custom folder from now on, or Apps again with null. Choosing Apps
    /// forgets the folder chosen before, as asked: unticked, it is gone.
    /// </summary>
    internal static void SetCustom(string? folder)
    {
        PreferenceStore.Set(UseKey, folder is null ? "0" : "1");
        PreferenceStore.Set(PathKey, folder is null ? string.Empty : Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar));
        _current = null;
    }

    /// <summary>Forgets what was read, for a database that was just replaced (tests).</summary>
    internal static void Reload() => _current = null;

    /// <summary>
    /// Why a folder cannot be the download folder, or null when it can.
    /// Inside the folder in use, the move would put the folder into itself.
    /// Around Git-Sync's own folder, the exe and its database would sit among
    /// the downloads, and Check Files would list them as loose.
    /// </summary>
    internal static string? Problem(string folder, string current)
    {
        string chosen = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
        string now = Path.GetFullPath(current).TrimEnd(Path.DirectorySeparatorChar);
        string own = Path.GetFullPath(AppPaths.BaseDir).TrimEnd(Path.DirectorySeparatorChar);

        if (string.Equals(chosen, now, StringComparison.OrdinalIgnoreCase))
        {
            return "That is the folder downloads already go to.";
        }

        if (IsInside(chosen, now))
        {
            return "That folder is inside the current download folder. Choose one outside it.";
        }

        if (string.Equals(chosen, own, StringComparison.OrdinalIgnoreCase) || IsInside(own, chosen))
        {
            return "That folder holds Git-Sync itself. Choose another one, or a folder inside it.";
        }

        return null;
    }

    internal static bool IsInside(string path, string folder) =>
        path.StartsWith(folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Moves the whole content of one download folder into another, keeping every
/// folder where it was inside it.
/// </summary>
internal static class FolderTransfer
{
    /// <summary>Names in <paramref name="from"/> that <paramref name="to"/> already has: they would be mixed.</summary>
    internal static List<string> Conflicts(string from, string to)
    {
        if (!Directory.Exists(from) || !Directory.Exists(to))
        {
            return [];
        }

        return [.. Directory.EnumerateFileSystemEntries(from)
            .Select(Path.GetFileName)
            .Where(name => File.Exists(Path.Combine(to, name!)) || Directory.Exists(Path.Combine(to, name!)))
            .Select(name => name!)
            .Order(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Whether anything is in the folder at all.</summary>
    internal static bool HasContent(string folder) =>
        Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any();

    /// <summary>
    /// Moves everything in <paramref name="from"/> into <paramref name="to"/>.
    /// On the same drive each entry is renamed, at once. To another drive
    /// every file is copied first and the originals deleted only once all of
    /// them have arrived: a copy that fails (a full drive, a file in use)
    /// removes what it made and leaves everything where it was.
    /// </summary>
    /// <returns>Old files that could not be deleted after being copied; the copies are in place.</returns>
    /// <exception cref="IOException">Nothing was moved; the message says why.</exception>
    internal static List<string> MoveContents(string from, string to, IProgress<(long Done, long Total)>? progress, bool alwaysCopy = false)
    {
        Directory.CreateDirectory(to);
        List<string> entries = [.. Directory.EnumerateFileSystemEntries(from)];

        bool sameDrive = string.Equals(Path.GetPathRoot(Path.GetFullPath(from)), Path.GetPathRoot(Path.GetFullPath(to)), StringComparison.OrdinalIgnoreCase);

        if (sameDrive && !alwaysCopy)
        {
            Rename(entries, to);
            progress?.Report((1, 1));
            return [];
        }

        Copy(entries, from, to, progress);
        return DeleteOriginals(entries);
    }

    private static void Rename(List<string> entries, string to)
    {
        var done = new List<(string From, string To)>();

        try
        {
            foreach (string entry in entries)
            {
                string target = Path.Combine(to, Path.GetFileName(entry));

                if (Directory.Exists(entry))
                {
                    Directory.Move(entry, target);
                }
                else
                {
                    File.Move(entry, target);
                }

                done.Add((entry, target));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Put back what already moved: half the folders here and half
            // there would match neither the old nor the new setting.
            foreach ((string back, string moved) in Enumerable.Reverse(done))
            {
                try
                {
                    if (Directory.Exists(moved))
                    {
                        Directory.Move(moved, back);
                    }
                    else
                    {
                        File.Move(moved, back);
                    }
                }
                catch (Exception again) when (again is IOException or UnauthorizedAccessException)
                {
                    // Left in the new folder; the error below still says nothing was switched.
                }
            }

            throw new IOException(error.Message, error);
        }
    }

    private static void Copy(List<string> entries, string from, string to, IProgress<(long Done, long Total)>? progress)
    {
        var files = new List<(string Source, string Target)>();
        var folders = new List<string>();

        foreach (string entry in entries)
        {
            string target = Path.Combine(to, Path.GetFileName(entry));

            if (Directory.Exists(entry))
            {
                folders.Add(target);

                foreach (string folder in Directory.EnumerateDirectories(entry, "*", SearchOption.AllDirectories))
                {
                    folders.Add(Path.Combine(target, Path.GetRelativePath(entry, folder)));
                }

                foreach (string file in Directory.EnumerateFiles(entry, "*", SearchOption.AllDirectories))
                {
                    files.Add((file, Path.Combine(target, Path.GetRelativePath(entry, file))));
                }
            }
            else
            {
                files.Add((entry, target));
            }
        }

        long total = files.Sum(f => new FileInfo(f.Source).Length);
        long done = 0;
        var made = new List<string>();

        try
        {
            foreach (string folder in folders)
            {
                Directory.CreateDirectory(folder);
            }

            foreach ((string source, string target) in files)
            {
                File.Copy(source, target);
                made.Add(target);
                done += new FileInfo(source).Length;
                progress?.Report((done, total));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            foreach (string file in made)
            {
                TryDelete(() => File.Delete(file));
            }

            // Deepest first, so each folder is empty when its turn comes.
            foreach (string folder in folders.OrderByDescending(f => f.Length))
            {
                TryDelete(() => Directory.Delete(folder));
            }

            throw new IOException(error.Message, error);
        }
    }

    private static List<string> DeleteOriginals(List<string> entries)
    {
        var kept = new List<string>();

        foreach (string entry in entries)
        {
            try
            {
                if (Directory.Exists(entry))
                {
                    Directory.Delete(entry, recursive: true);
                }
                else
                {
                    File.Delete(entry);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                kept.Add(entry);
            }
        }

        return kept;
    }

    private static void TryDelete(Action delete)
    {
        try
        {
            delete();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Cleaning up after a failed copy: what stays is reported by the error itself.
        }
    }
}
