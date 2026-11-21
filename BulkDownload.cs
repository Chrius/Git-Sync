using System.Text.RegularExpressions;

namespace GitSync;

/// <summary>A version picked in Bulk Download, with the files ticked for it.</summary>
internal sealed record BulkChoice(Release Release, List<ReleaseFile> Files)
{
    /// <summary>As it is recorded once downloaded: only its source picked gives it the SOURCE mark.</summary>
    public VersionRef Version => Downloader.DownloadedVersion(Release, Files);
}

/// <param name="Download">The versions to download, oldest first.</param>
/// <param name="Skipped">Picked, but older than the versions the repository keeps: left out. Newest first.</param>
internal sealed record BulkPlan(List<BulkChoice> Download, List<BulkChoice> Skipped);

/// <param name="Done">The versions saved.</param>
/// <param name="Files">How many files, over all of them.</param>
/// <param name="Warnings">Versions that failed, and files that could not be moved or deleted.</param>
internal sealed record BulkOutcome(List<BulkChoice> Done, int Files, List<string> Warnings);

/// <summary>
/// "Bulk Download" on the right-click menu of a repository that keeps
/// previous versions: files of any number of its versions in one go, each
/// put where an update would have put it — the newest in the folder itself,
/// the others with the kept versions (in Old, with that setting).
///
/// It never changes the Selected version: a Latest left out still shows as
/// an update afterwards.
/// </summary>
internal static class BulkDownload
{
    /// <summary>
    /// Which picked versions are downloaded, and in what order.
    ///
    /// With a number of versions to keep, only that many stay on disk: the
    /// newest, counting what is there already (Current and the kept versions)
    /// with what was picked. Picked versions older than those are left out,
    /// as asked — downloaded, they would only be deleted again at once.
    ///
    /// Oldest first, so each one is placed as an update would place it: one
    /// newer than Current becomes Current and the one before it joins the kept
    /// versions, until the newest picked is in the folder itself.
    /// </summary>
    internal static BulkPlan Plan(
        IReadOnlyList<BulkChoice> chosen, VersionRef? current, IReadOnlyList<PreviousVersion> kept, int keepVersions)
    {
        // Picked newest first, as the site lists them: on equal dates the later one is the older.
        List<BulkChoice> oldestFirst = [.. chosen
            .Select((choice, index) => (choice, index))
            .OrderBy(x => Key(x.choice.Release.Date))
            .ThenByDescending(x => x.index)
            .Select(x => x.choice)];

        if (keepVersions <= 0)
        {
            return new BulkPlan(oldestFirst, []);
        }

        // Everything on disk afterwards, once each: a picked version that is
        // already there only adds files to it.
        var onDisk = new List<VersionRef>();

        if (current is not null)
        {
            onDisk.Add(current);
        }

        onDisk.AddRange(kept.Select(k => k.Version));
        var ranked = new List<(DateTimeOffset Date, int Order, BulkChoice? Choice)>();

        foreach (VersionRef version in onDisk)
        {
            ranked.Add((Key(version.Date), 0, null));
        }

        for (int i = oldestFirst.Count - 1; i >= 0; i--)
        {
            BulkChoice choice = oldestFirst[i];
            int there = onDisk.FindIndex(v => SameBuild(v, choice.Version));

            if (there >= 0)
            {
                ranked[there] = ranked[there] with { Choice = choice };
            }
            else
            {
                ranked.Add((Key(choice.Release.Date), 1 + oldestFirst.Count - i, choice));
            }
        }

        var staying = new HashSet<BulkChoice>(ReferenceEqualityComparer.Instance);

        foreach ((_, _, BulkChoice? choice) in ranked.OrderByDescending(r => r.Date).ThenBy(r => r.Order).Take(keepVersions))
        {
            if (choice is not null)
            {
                staying.Add(choice);
            }
        }

        return new BulkPlan(
            [.. oldestFirst.Where(staying.Contains)],
            [.. oldestFirst.Where(c => !staying.Contains(c)).Reverse()]);
    }

    /// <summary>
    /// The same build, as far as can be told: same tag, and the same date
    /// when both have one — a "nightly" published again under its tag is
    /// another build. A release and its own source archive are the same.
    /// </summary>
    internal static bool SameBuild(VersionRef a, VersionRef b) =>
        VersionState.SameVersion(a, b) && (a.Date is null || b.Date is null || a.Date == b.Date);

    /// <summary>Undated versions sort as the oldest.</summary>
    private static DateTimeOffset Key(DateTimeOffset? date) => date ?? DateTimeOffset.MinValue;

    /// <summary>
    /// The kept versions in date order, oldest first: the order in which
    /// "versions to keep" deletes them. An older version pinned and
    /// downloaded puts the newer one it replaced at the end of the list, out
    /// of date order; a bulk download adds versions of any age among them.
    /// </summary>
    internal static List<PreviousVersion> ByDate(IReadOnlyList<PreviousVersion> kept) =>
        [.. kept.Select((version, index) => (version, index))
            .OrderBy(x => Key(x.version.Version.Date))
            .ThenBy(x => x.index)
            .Select(x => x.version)];

    /// <summary>
    /// Whether a recorded file is this file of the release: its labeled name,
    /// or that name made free beside another file — "app (v1, 2).zip" (kept
    /// beside a build of the same tag) or "app (v1) (2).zip" (Old had one).
    /// </summary>
    internal static bool IsSavedAs(string savedPath, string releaseFileName, string versionTag)
    {
        string expected = DownloadPaths.LabeledName(DownloadPaths.SafeFileName(releaseFileName), versionTag);
        string name = Path.GetFileName(savedPath);

        if (string.Equals(name, expected, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        (string stem, string extension) = DownloadPaths.SplitExtension(name);
        (string expectedStem, string expectedExtension) = DownloadPaths.SplitExtension(expected);

        if (!string.Equals(extension, expectedExtension, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string label = versionTag.Trim().Length == 0 ? string.Empty : $" ({DownloadPaths.SafeName(versionTag)})";
        string bare = label.Length > 0 && expectedStem.EndsWith(label, StringComparison.OrdinalIgnoreCase)
            ? expectedStem[..^label.Length]
            : expectedStem;

        string tagPart = label.Length == 0 ? string.Empty : $@" \({Regex.Escape(DownloadPaths.SafeName(versionTag))}(, \d+)?\)";
        return Regex.IsMatch(stem, $@"^{Regex.Escape(bare)}{tagPart}( \(\d+\))?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// Downloads the planned versions one after another, oldest first, and
    /// puts each in place before the next (see <see cref="Place"/>). A version
    /// that fails is reported and skipped; the others still go through. Only
    /// closing the app stops it.
    /// </summary>
    internal static async Task<BulkOutcome> DownloadAsync(
        Site site, Repo listed, IReadOnlyList<FilterFolder> filters, IReadOnlyList<BulkChoice> versions,
        IProgress<DownloadProgress>? progress, CancellationToken cancel)
    {
        string folder = DownloadPaths.RepoFolder(listed, filters);

        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new ApiException($"Could not create the download folder {folder}: {error.Message}");
        }

        List<string> otherRepos = [.. RepoStore.List().Where(r => r.Id != listed.Id).Select(r => DownloadPaths.RepoFolder(r, filters))];

        Repo repo = Reread(listed.Id);
        List<PreviousVersion> kept = RepoStore.PreviousVersions(repo.Id);
        List<PreviousVersion> sorted = ByDate(kept);

        if (!sorted.SequenceEqual(kept))
        {
            RepoStore.SetFiles(repo.Id, repo.CurrentFiles, sorted);
        }

        int total = Math.Max(1, versions.Sum(v => v.Files.Count));
        int finished = 0;
        int saved = 0;
        bool currentChanged = false;
        var done = new List<BulkChoice>();
        var warnings = new List<string>();

        foreach (BulkChoice choice in versions)
        {
            cancel.ThrowIfCancellationRequested();

            // Each version changes Current or the kept list for the next one.
            repo = Reread(listed.Id);
            string tag = choice.Release.ToVersion().DisplayTag;
            var pending = new List<(string Part, string Final)>();

            try
            {
                for (int index = 0; index < choice.Files.Count; index++)
                {
                    ReleaseFile file = choice.Files[index];
                    string final = Path.Combine(folder, DownloadPaths.LabeledName(DownloadPaths.SafeFileName(file.Name), tag));
                    string part = final + ".part";
                    pending.Add((part, final));

                    int before = finished + index;
                    await Downloader.FetchAsync(site, file, part, (fraction, bytes) => progress?.Report(new DownloadProgress(
                        fraction is { } f ? (before + f) / total : null, bytes)), cancel);
                }
            }
            catch (ApiException error)
            {
                DeleteParts(pending);
                warnings.Add($"{tag}: {error.Message}");
                finished += choice.Files.Count;
                continue;
            }
            catch
            {
                DeleteParts(pending);
                throw;
            }

            finished += choice.Files.Count;

            try
            {
                (List<string> problems, bool becameCurrent) = Place(repo, folder, pending, choice.Version, otherRepos);
                warnings.AddRange(problems);
                currentChanged |= becameCurrent;
                saved += pending.Count;
                done.Add(choice);
            }
            catch (ApiException error)
            {
                warnings.Add($"{tag}: {error.Message}");
            }
        }

        // The folder's README.md goes with the version in it.
        if (currentChanged && PreferenceStore.DownloadReadme() && Reread(listed.Id).Current is { } now
            && await Downloader.SaveReadmeAsync(site, repo, folder, now.Tag, cancel) is { } readmeProblem)
        {
            warnings.Add(readmeProblem);
        }

        return new BulkOutcome(done, saved, warnings);
    }

    private static Repo Reread(long id) =>
        RepoStore.Get(id) ?? throw new ApiException("This repository is no longer in the list.");

    private static void DeleteParts(IEnumerable<(string Part, string Final)> pending)
    {
        foreach ((string part, _) in pending)
        {
            Downloader.TryDelete(part);
        }
    }

    /// <summary>
    /// Puts one downloaded version in place, from its ".part" files in the
    /// repo's folder. Kept apart from the download so the tests can run it.
    ///
    /// Newer than Current (or nothing downloaded yet): it becomes Current,
    /// exactly as an update. Older: it joins the kept versions, in date order
    /// — in Old with "Move previous versions to a different folder", else
    /// beside Current — and Current stays. Already on disk: its new files join it.
    /// </summary>
    /// <returns>What to warn about, and whether the version became Current.</returns>
    internal static (List<string> Warnings, bool BecameCurrent) Place(
        Repo repo, string folder, IReadOnlyList<(string Part, string Final)> pending, VersionRef version,
        IReadOnlyCollection<string>? otherRepos = null)
    {
        var warnings = new List<string>();
        List<PreviousVersion> kept = RepoStore.PreviousVersions(repo.Id);

        if (repo.Current is { } current && SameBuild(current, version))
        {
            // Saved over a file of the same name, as an update would.
            List<string> added = MoveParts(pending, final => final, overwrite: true);
            RepoStore.SetFiles(repo.Id, Merge(repo.CurrentFiles, added), kept);
            return (warnings, false);
        }

        int there = kept.FindIndex(k => SameBuild(k.Version, version));

        if (there >= 0)
        {
            // Beside the version's own files, wherever they are now.
            string? existing = kept[there].Files.Select(DownloadPaths.ToAbsolute).FirstOrDefault(File.Exists);
            string place = existing is not null ? Path.GetDirectoryName(existing)! : KeptFolder(folder, warnings);
            List<string> added = MoveParts(pending, final => FreePath(place, final), overwrite: false);
            kept[there] = kept[there] with { Files = Merge(kept[there].Files, added) };
            RepoStore.SetFiles(repo.Id, repo.CurrentFiles, kept);
            return (warnings, false);
        }

        if (repo.Current is not { } now || Key(version.Date) > Key(now.Date))
        {
            warnings.AddRange(Downloader.Finish(repo, folder, pending, version, redownload: false, otherRepos));
            return (warnings, true);
        }

        // Older than Current: a kept version, never in the folder's own place.
        string target = KeptFolder(folder, warnings);
        List<string> files = MoveParts(pending, final => FreePath(target, final), overwrite: false);

        int at = kept.FindIndex(k => Key(k.Version.Date) > Key(version.Date));
        kept.Insert(at < 0 ? kept.Count : at, new PreviousVersion(version, files));

        var delete = new List<PreviousVersion>();

        if (repo.KeepVersions > 0 && kept.Count > repo.KeepVersions - 1)
        {
            int extra = kept.Count - (repo.KeepVersions - 1);
            delete.AddRange(kept.Take(extra));
            kept = [.. kept.Skip(extra)];
        }

        kept = Downloader.DeleteVersions(delete, kept, repo.CurrentFiles, warnings, folder);
        RepoStore.SetFiles(repo.Id, repo.CurrentFiles, kept);
        return (warnings, false);
    }

    /// <summary>
    /// Where an older version goes: Old with "Move previous versions to a
    /// different folder", else the folder itself (the version in every name
    /// keeps the files apart). A file named "Old" in the way keeps it in the folder.
    /// </summary>
    private static string KeptFolder(string folder, List<string> warnings)
    {
        if (!PreferenceStore.MoveOldVersions())
        {
            return folder;
        }

        string old = Path.Combine(folder, OldFolder.Name);

        if (File.Exists(old))
        {
            warnings.Add($"A file named \"{OldFolder.Name}\" is in the way, so older versions stay in the folder itself.");
            return folder;
        }

        try
        {
            Directory.CreateDirectory(old);
            return old;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Could not create {OldFolder.Name}: {error.Message}");
            return folder;
        }
    }

    /// <summary>The file's name in <paramref name="place"/>, never over a file already there.</summary>
    private static string FreePath(string place, string final) =>
        OldFolder.FreeName(Path.Combine(place, Path.GetFileName(final)), isFolder: false);

    /// <summary>
    /// Moves the ".part" files to their places. One that cannot be moved
    /// fails the version, as in <see cref="Downloader.Commit"/>: its other
    /// ".part" files are removed, and the files already moved stay on disk unrecorded.
    /// </summary>
    private static List<string> MoveParts(
        IReadOnlyList<(string Part, string Final)> pending, Func<string, string> place, bool overwrite)
    {
        var saved = new List<string>();

        for (int i = 0; i < pending.Count; i++)
        {
            string target = place(pending[i].Final);

            try
            {
                File.Move(pending[i].Part, target, overwrite);
                saved.Add(DownloadPaths.ToRelative(target));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                DeleteParts(pending.Skip(i));
                throw new ApiException($"Could not save {Path.GetFileName(target)}: {error.Message}");
            }
        }

        return saved;
    }

    private static List<string> Merge(IEnumerable<string> files, IEnumerable<string> added)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return [.. files.Concat(added).Where(f => seen.Add(f.Replace('/', '\\')))];
    }
}
