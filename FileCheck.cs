using System.Text.RegularExpressions;

namespace GitSync;

/// <summary>A file recorded for a repository that is no longer on disk.</summary>
/// <param name="Version">The version it belongs to, as shown on screen.</param>
/// <param name="Path">Relative to the exe, as recorded.</param>
internal sealed record MissingFile(long RepoId, string RepoName, string Version, string Path);

/// <summary>What "Check Files" found.</summary>
/// <param name="Checked">Files recorded for the repositories, current and kept versions.</param>
/// <param name="Missing">Recorded files that are no longer on disk.</param>
/// <param name="Loose">Files and folders in the download folder that belong to no repository, full paths, in folder order.</param>
/// <param name="Adopted">Earlier downloads found in repository folders and recorded now.</param>
/// <param name="RootMissing">
/// The download folder itself is not there (a custom one on a drive that is
/// not connected): nothing was checked, rather than every file reported missing.
/// </param>
internal sealed record FileCheckResult(int Checked, List<MissingFile> Missing, List<string> Loose, int Adopted, bool RootMissing = false);

/// <summary>
/// Settings → Data → Check Files: whether the files recorded for every
/// repository are still on disk, and what in the download folder belongs to none.
/// </summary>
internal static partial class FileCheck
{
    private const string AdoptedKey = "AdoptedDownloads";

    /// <summary>
    /// <see cref="AdoptDownloads"/> the first time this version of Git-Sync
    /// opens a database. Kept versions were not recorded before, so their
    /// files would count as nobody's.
    /// </summary>
    internal static int AdoptOnce()
    {
        if (PreferenceStore.Get(AdoptedKey) == "1")
        {
            return 0;
        }

        int adopted = AdoptDownloads();
        PreferenceStore.Set(AdoptedKey, "1");
        return adopted;
    }

    internal static FileCheckResult Run()
    {
        List<Repo> repos = RepoStore.List();

        // "Forget Missing Files" after a check like that would wipe every record.
        if (!Directory.Exists(DownloadRoot.Current) && repos.Any(r => r.CurrentFiles.Count > 0 || RepoStore.PreviousVersions(r.Id).Count > 0))
        {
            return new FileCheckResult(0, [], [], 0, RootMissing: true);
        }

        int adopted = AdoptDownloads();
        repos = RepoStore.List();
        List<FilterFolder> filters = FilterStore.List();

        var missing = new List<MissingFile>();
        var recorded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int count = 0;

        foreach (Repo repo in repos)
        {
            void Look(string version, IEnumerable<string> files)
            {
                foreach (string file in files)
                {
                    count++;
                    string full = Full(DownloadPaths.ToAbsolute(file));
                    recorded.Add(full);

                    if (!File.Exists(full))
                    {
                        missing.Add(new MissingFile(repo.Id, repo.Name, version, file));
                    }
                }
            }

            Look(repo.Current?.DisplayTag ?? string.Empty, repo.CurrentFiles);

            foreach (PreviousVersion kept in RepoStore.PreviousVersions(repo.Id))
            {
                Look(kept.Version.DisplayTag, kept.Files);
            }
        }

        return new FileCheckResult(count, missing, FindLoose(repos, filters, recorded), adopted);
    }

    /// <summary>
    /// Everything in the download folder outside every repository's folder that is not a
    /// recorded file either. Inside a repository's folder nothing is loose —
    /// what the user put there is theirs, recorded or not. A folder that holds
    /// no repository and no recorded file is listed whole, not file by file.
    /// The folders of the tree are walked through, never listed themselves.
    /// </summary>
    internal static List<string> FindLoose(IReadOnlyList<Repo> repos, IReadOnlyList<FilterFolder> filters, IReadOnlySet<string> recorded)
    {
        string apps = Full(DownloadRoot.Current);
        var loose = new List<string>();

        if (!Directory.Exists(apps))
        {
            return loose;
        }

        var repoFolders = new HashSet<string>(repos.Select(r => Full(DownloadPaths.RepoFolder(r, filters))), StringComparer.OrdinalIgnoreCase);
        var walkThrough = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (FilterFolder folder in filters)
        {
            string path = Full(DownloadPaths.TreeFolder(folder.ParentId, folder.Name, filters));
            walkThrough.Add(path);
            AddParents(walkThrough, path, apps);
        }

        foreach (string path in repoFolders.Concat(recorded))
        {
            AddParents(walkThrough, path, apps);
        }

        void Walk(string folder)
        {
            List<string> entries;

            try
            {
                entries = [.. Directory.EnumerateFileSystemEntries(folder).Order(StringComparer.OrdinalIgnoreCase)];
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return;
            }

            foreach (string entry in entries)
            {
                string full = Full(entry);

                if (Directory.Exists(full))
                {
                    if (repoFolders.Contains(full))
                    {
                        continue;
                    }

                    if (walkThrough.Contains(full))
                    {
                        Walk(full);
                    }
                    else
                    {
                        loose.Add(full);
                    }
                }
                else if (!recorded.Contains(full) && !OldFolder.IsWindowsMetadata(full))
                {
                    loose.Add(full);
                }
            }
        }

        Walk(apps);
        return loose;
    }

    private static void AddParents(HashSet<string> set, string path, string apps)
    {
        for (string? parent = Path.GetDirectoryName(path);
             parent is not null && parent.Length > apps.Length && parent.StartsWith(apps, StringComparison.OrdinalIgnoreCase);
             parent = Path.GetDirectoryName(parent))
        {
            set.Add(Full(parent));
        }
    }

    private static string Full(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);

    // ---------- earlier downloads ----------

    // Commit ids, as source downloads of repositories without releases are labeled.
    [GeneratedRegex("^(?=.*[0-9])[0-9a-f]{7,40}$")]
    private static partial Regex CommitPattern();

    [GeneratedRegex(@"\d+")]
    private static partial Regex DigitsPattern();

    /// <summary>
    /// What a version tag looks like with its numbers taken out: "v2.2.0" and
    /// "v2.0.2" are both "v#.#.#". A label of the same shape as the current
    /// version's is taken for an older version of the same repository.
    /// </summary>
    internal static string Shape(string tag) =>
        CommitPattern().IsMatch(tag) ? "#commit" : DigitsPattern().Replace(tag.ToLowerInvariant(), "#");

    /// <summary>
    /// "notes (2).txt" is how Windows, and the Old folder, number a second
    /// copy: one or two digits are never taken for a version, even beside a
    /// current version tagged with a bare number.
    /// </summary>
    private static bool IsCopyNumber(string label) => label.Length <= 2 && label.All(char.IsAsciiDigit);

    /// <summary>
    /// Records the files in each repository's folder (and its Old folder)
    /// that Git-Sync downloaded before it kept a record of every version:
    /// those labeled with the current version join it, those labeled with an
    /// older version of the same shape become kept versions, oldest first,
    /// where the number of versions to keep then counts them. Only names
    /// labeled the way Git-Sync labels downloads: any other file in the
    /// folder (extracted, edited, the user's own) is never recorded, and so
    /// never deleted by an update. Returns how many files were recorded.
    /// </summary>
    internal static int AdoptDownloads()
    {
        List<FilterFolder> filters = FilterStore.List();
        int adopted = 0;

        foreach (Repo repo in RepoStore.List())
        {
            // Without a current version there is nothing to tell versions by.
            if (repo.Current is not { } current)
            {
                continue;
            }

            string folder = DownloadPaths.RepoFolder(repo, filters);

            if (!Directory.Exists(folder))
            {
                continue;
            }

            List<PreviousVersion> kept = RepoStore.PreviousVersions(repo.Id);
            var recorded = new HashSet<string>(
                repo.CurrentFiles.Concat(kept.SelectMany(v => v.Files)).Select(f => Full(DownloadPaths.ToAbsolute(f))),
                StringComparer.OrdinalIgnoreCase);

            string currentLabel = DownloadPaths.SafeName(current.DisplayTag);
            string currentShape = Shape(currentLabel);
            var toCurrent = new List<string>();
            var older = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            foreach (string place in new[] { folder, Path.Combine(folder, OldFolder.Name) }.Where(Directory.Exists))
            {
                foreach (string file in Directory.EnumerateFiles(place).Order(StringComparer.OrdinalIgnoreCase))
                {
                    string full = Full(file);

                    if (recorded.Contains(full) || OldFolder.IsWindowsMetadata(full)
                        || DownloadPaths.ReadLabel(Path.GetFileName(full)) is not { } label)
                    {
                        continue;
                    }

                    string relative = DownloadPaths.ToRelative(full);

                    if (label.Tag == currentLabel && label.Counter is null)
                    {
                        toCurrent.Add(relative);
                    }
                    else if (label.Tag == currentLabel || (Shape(label.Tag) == currentShape && !IsCopyNumber(label.Tag)))
                    {
                        // "(v2.0.2)", or "(nightly, 2)": an earlier build kept under the same tag.
                        if (!older.TryGetValue(label.Text, out List<string>? files))
                        {
                            older[label.Text] = files = [];
                        }

                        files.Add(relative);
                    }
                }
            }

            if (toCurrent.Count == 0 && older.Count == 0)
            {
                continue;
            }

            // Oldest first, by when their files were written: when they were downloaded.
            List<PreviousVersion> found = [.. older
                .Select(g => new PreviousVersion(new VersionRef(g.Key, null, VersionKind.Release), g.Value))
                .OrderBy(v => v.Files.Min(f => File.GetLastWriteTimeUtc(DownloadPaths.ToAbsolute(f))))];

            // A label already recorded as a kept version gets its files; the
            // rest go before the recorded ones, which came later.
            var history = new List<PreviousVersion>();

            foreach (PreviousVersion version in found)
            {
                int index = kept.FindIndex(k => k.Version.DisplayTag == version.Version.Tag);

                if (index >= 0)
                {
                    kept[index] = kept[index] with { Files = [.. kept[index].Files, .. version.Files] };
                }
                else
                {
                    history.Add(version);
                }
            }

            history.AddRange(kept);
            RepoStore.SetFiles(repo.Id, [.. repo.CurrentFiles, .. toCurrent], history);
            adopted += toCurrent.Count + older.Values.Sum(f => f.Count);
        }

        return adopted;
    }

    /// <summary>
    /// "Forget Missing Files": the records stop listing files that are gone.
    /// A kept version left with none is forgotten. Current stays as it is —
    /// the version is still the one last downloaded, even without its files.
    /// </summary>
    internal static void ForgetMissing(IEnumerable<MissingFile> missing)
    {
        foreach (IGrouping<long, MissingFile> repoMissing in missing.GroupBy(m => m.RepoId))
        {
            if (RepoStore.Get(repoMissing.Key) is not { } repo)
            {
                continue;
            }

            var gone = new HashSet<string>(repoMissing.Select(m => m.Path), StringComparer.OrdinalIgnoreCase);

            // Only what is still missing now: a file put back meanwhile stays recorded.
            bool Keep(string file) => !gone.Contains(file) || File.Exists(DownloadPaths.ToAbsolute(file));

            List<PreviousVersion> kept = [.. RepoStore.PreviousVersions(repo.Id)
                .Select(v => v with { Files = [.. v.Files.Where(Keep)] })
                .Where(v => v.Files.Count > 0)];

            RepoStore.SetFiles(repo.Id, [.. repo.CurrentFiles.Where(Keep)], kept);
        }
    }
}
