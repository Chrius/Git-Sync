namespace GitSync;

/// <summary>How far a download is.</summary>
/// <param name="Fraction">0 to 1 over all the files, or null while the server does not say how big the file is
/// (GitHub's source archives are generated on the fly and come without a size).</param>
/// <param name="Bytes">Received so far of the current file.</param>
internal readonly record struct DownloadProgress(double? Fraction, long Bytes);

/// <summary>What a finished download left behind.</summary>
/// <param name="Files">New files, relative to the exe.</param>
/// <param name="Warnings">Old files that could not be deleted (usually because they are in use).</param>
internal sealed record DownloadOutcome(List<string> Files, List<string> Warnings);

/// <summary>What <see cref="Downloader.Commit"/> did besides saving the new files.</summary>
/// <param name="Renamed">Kept old files renamed out of a new file's way.</param>
internal sealed record CommitResult(List<string> Warnings, PathMoves Renamed);

/// <summary>
/// Downloads the chosen files of a version and replaces the previous version's
/// files with them.
///
/// Every file first goes to "name.part". Only when all of them have arrived are
/// they moved to their real names and the old version's files deleted, so a
/// failed or cancelled download never destroys the version that is on disk.
/// </summary>
internal static class Downloader
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    /// <param name="redownload">The Current version again, with the files picked anew (see <see cref="KeepAs"/>).</param>
    internal static async Task<DownloadOutcome> DownloadAsync(
        Site site, Repo repo, IReadOnlyList<FilterFolder> filters, Release release,
        IReadOnlyList<ReleaseFile> files, IProgress<DownloadProgress>? progress, CancellationToken cancel,
        bool redownload = false)
    {
        string folder = DownloadPaths.RepoFolder(repo, filters);

        // Checked on every download, as asked: the folder may have been
        // deleted or renamed by hand since the last one.
        Directory.CreateDirectory(folder);

        var pending = new List<(string Part, string Final)>();

        // Every file is saved with the version at the end of its name.
        string version = release.ToVersion().DisplayTag;

        try
        {
            for (int index = 0; index < files.Count; index++)
            {
                ReleaseFile file = files[index];
                string final = Path.Combine(folder, DownloadPaths.LabeledName(DownloadPaths.SafeFileName(file.Name), version));
                string part = final + ".part";
                pending.Add((part, final));

                int done = index;
                await FetchAsync(site, file, part, (fraction, bytes) => progress?.Report(new DownloadProgress(
                    fraction is { } f ? (done + f) / files.Count : null, bytes)), cancel);
            }
        }
        catch
        {
            foreach ((string part, _) in pending)
            {
                TryDelete(part);
            }

            throw;
        }

        List<string> warnings = Finish(repo, folder, pending, DownloadedVersion(release, files), redownload);
        List<string> saved = [.. pending.Select(p => DownloadPaths.ToRelative(p.Final))];

        // Unticked, nothing is asked of the site and a README.md already there stays as it is.
        if (PreferenceStore.DownloadReadme()
            && await SaveReadmeAsync(site, repo, folder, release.Tag, cancel) is { } readmeProblem)
        {
            warnings.Add(readmeProblem);
        }

        return new DownloadOutcome(saved, warnings);
    }

    /// <summary>
    /// Everything after the files have arrived as ".part": puts them in place,
    /// deals with the previous version's files and records the new Current.
    /// Kept apart from the download so the tests can run it on files of their own.
    ///
    /// Without "Keep previous versions", and for a redownload, the previous
    /// files are replaced. With it, the replaced version joins the list of
    /// kept versions; "Move previous versions to a different folder" first
    /// moves everything in the folder into Old; and with a number of versions
    /// to keep, the oldest kept versions beyond it are deleted.
    /// </summary>
    internal static List<string> Finish(
        Repo repo, string folder, IReadOnlyList<(string Part, string Final)> pending, VersionRef version, bool redownload)
    {
        List<string> saved = [.. pending.Select(p => DownloadPaths.ToRelative(p.Final))];

        if (!repo.KeepPrevious || redownload)
        {
            List<string> replaced = Commit(pending, repo.CurrentFiles, KeepAs(repo, redownload)).Warnings;
            RepoStore.SetCurrent(repo.Id, version, saved);
            return replaced;
        }

        var warnings = new List<string>();
        List<PreviousVersion> kept = RepoStore.PreviousVersions(repo.Id);
        List<string> previousFiles = [.. repo.CurrentFiles];

        if (PreferenceStore.MoveOldVersions())
        {
            (PathMoves moves, List<string> problems) = OldFolder.MoveAside(folder, pending.Select(p => p.Part));
            warnings.AddRange(problems);
            previousFiles = moves.Apply(previousFiles);
            kept = moves.Apply(kept);
        }

        CommitResult commit = Commit(pending, previousFiles, KeepAs(repo, redownload: false));
        warnings.AddRange(commit.Warnings);
        previousFiles = commit.Renamed.Apply(previousFiles);

        PreviousVersion? replacedVersion = repo.Current is { } current ? new PreviousVersion(current, previousFiles) : null;

        (List<PreviousVersion> keep, List<PreviousVersion> delete) = VersionHistory.Plan(
            kept, replacedVersion, version, saved, repo.KeepVersions, f => File.Exists(DownloadPaths.ToAbsolute(f)));

        keep = DeleteVersions(delete, keep, saved, warnings);
        RepoStore.SetCurrent(repo.Id, version, saved, keep);
        return warnings;
    }

    /// <summary>
    /// Deletes the files of versions no longer kept. Never a file that is
    /// still the new version's or a kept version's, and only inside Apps.
    /// What cannot be deleted (open in another program) stays listed, as the
    /// oldest version, so the next update tries again. Returns the new list.
    /// </summary>
    private static List<PreviousVersion> DeleteVersions(
        List<PreviousVersion> delete, List<PreviousVersion> keep, IReadOnlyList<string> current, List<string> warnings)
    {
        var owned = new HashSet<string>(
            current.Concat(keep.SelectMany(v => v.Files)).Select(DownloadPaths.ToAbsolute), StringComparer.OrdinalIgnoreCase);

        var leftovers = new List<PreviousVersion>();

        foreach (PreviousVersion version in delete)
        {
            var failed = new List<string>();

            foreach (string file in version.Files)
            {
                string full = DownloadPaths.ToAbsolute(file);

                if (owned.Contains(full) || !DownloadPaths.IsInsideApps(full))
                {
                    continue;
                }

                try
                {
                    if (File.Exists(full))
                    {
                        File.Delete(full);
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    failed.Add(file);
                    warnings.Add($"Could not delete {Path.GetFileName(full)} of {version.Version.DisplayTag}: {error.Message}");
                }
            }

            if (failed.Count > 0)
            {
                leftovers.Add(version with { Files = failed });
            }
        }

        return [.. leftovers, .. keep];
    }

    /// <summary>
    /// What <see cref="Commit"/> does with the files on disk: null replaces
    /// them, a tag keeps them under that tag. "Keep previous versions" keeps
    /// earlier versions only. A redownload is the same version picked again,
    /// so its old files are replaced even then — kept, a file chosen by mistake
    /// would stay in the folder for good, renamed "(tag, 2)".
    /// </summary>
    internal static string? KeepAs(Repo repo, bool redownload) =>
        repo.KeepPrevious && !redownload ? repo.Current?.DisplayTag ?? "previous" : null;

    /// <summary>
    /// The version to record as Current. Picking only the source archives of a
    /// release that has files of its own (possible with "Show source code")
    /// leaves the source on disk, not the release, so Current gets the SOURCE
    /// mark. It is still the same version: no update is offered for it.
    /// </summary>
    internal static VersionRef DownloadedVersion(Release release, IReadOnlyList<ReleaseFile> files)
    {
        VersionRef version = release.ToVersion();

        return version.Kind == VersionKind.Release && files.Count > 0 && files.All(f => f.IsSource)
            ? version with { Kind = VersionKind.ReleaseSource }
            : version;
    }

    /// <summary>
    /// The repo's README.md of the downloaded version, saved as "README.md"
    /// beside the files and over the previous one. One per folder, so it has
    /// no version in its name, and it is not one of the version's files: a
    /// later update never deletes it, only replaces it. A repo without one is
    /// skipped quietly. Anything else that goes wrong is only a warning — the
    /// files themselves are already saved by now.
    /// </summary>
    private static async Task<string?> SaveReadmeAsync(
        Site site, Repo repo, string folder, string reference, CancellationToken cancel)
    {
        string path = Path.Combine(folder, "README.md");
        string part = path + ".part";

        try
        {
            byte[]? content = await ApiClient.GetReadmeAsync(site, repo, reference, cancel);

            if (content is null)
            {
                return null;
            }

            await File.WriteAllBytesAsync(part, content, cancel);
            File.Move(part, path, overwrite: true);
            return null;
        }
        catch (ApiException error)
        {
            return "README.md could not be downloaded: " + error.Message;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            TryDelete(part);
            return "README.md could not be saved: " + error.Message;
        }
        catch (OperationCanceledException)
        {
            // Closing the app: the update itself is complete.
            TryDelete(part);
            return null;
        }
    }

    /// <summary>
    /// Moves the finished ".part" files to their names, then deletes the
    /// previous version's files that the new ones did not overwrite. Only
    /// files Git-Sync itself recorded are ever deleted, and only inside Apps.
    /// </summary>
    /// <param name="keepAs">
    /// "Keep previous versions": null deletes the old files as above. A tag
    /// keeps every old file instead; one that the new version would overwrite
    /// (a release that reuses its file names, like "app-win64.zip") is first
    /// renamed to "app-win64 (tag).zip".
    /// </param>
    internal static CommitResult Commit(
        IReadOnlyList<(string Part, string Final)> files, IReadOnlyList<string> previous, string? keepAs = null)
    {
        var warnings = new List<string>();
        var renamed = new PathMoves();
        var previousFull = new HashSet<string>(previous.Select(DownloadPaths.ToAbsolute), StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < files.Count; i++)
        {
            string final = files[i].Final;

            if (keepAs is not null && File.Exists(final) && previousFull.Contains(Path.GetFullPath(final)))
            {
                try
                {
                    string kept = DownloadPaths.KeptName(final, keepAs);
                    File.Move(final, kept);
                    renamed.Add(final, kept);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"Could not keep the old {Path.GetFileName(final)}: {error.Message}");
                }
            }

            try
            {
                File.Move(files[i].Part, files[i].Final, overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                for (int j = i; j < files.Count; j++)
                {
                    TryDelete(files[j].Part);
                }

                throw new ApiException($"Could not save {Path.GetFileName(files[i].Final)}: {error.Message}");
            }
        }

        if (keepAs is not null)
        {
            return new CommitResult(warnings, renamed);
        }

        IEnumerable<string> next = files.Select(f => DownloadPaths.ToRelative(f.Final));

        foreach (string old in DownloadPaths.FilesToDelete(previous, next))
        {
            string full = DownloadPaths.ToAbsolute(old);

            if (!DownloadPaths.IsInsideApps(full))
            {
                continue;
            }

            try
            {
                if (File.Exists(full))
                {
                    File.Delete(full);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Could not delete the old file {Path.GetFileName(full)}: {error.Message}");
            }
        }

        return new CommitResult(warnings, renamed);
    }

    private static async Task FetchAsync(
        Site site, ReleaseFile file, string partPath, Action<double?, long> report, CancellationToken cancel)
    {
        // With a token, GitHub's API address of the asset also works for
        // private repos. Without one it would spend the 60-per-hour API quota,
        // so the public address is used instead.
        bool viaApi = site.Type == ApiType.GitHub && site.Token.Length > 0 && !string.IsNullOrEmpty(file.ApiUrl);
        string url = viaApi ? file.ApiUrl! : file.Url;
        string accept = viaApi ? "application/octet-stream" : "*/*";

        try
        {
            HttpResponseMessage response;

            using (var start = CancellationTokenSource.CreateLinkedTokenSource(cancel))
            {
                start.CancelAfter(StartTimeout);

                try
                {
                    response = await ApiClient.SendAsync(site, url, accept, start.Token);
                }
                catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
                {
                    throw new ApiException($"{file.Name}: the download did not start in time.");
                }
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw new ApiException(FailureText(site, file, response));
                }

                long total = response.Content.Headers.ContentLength ?? (file.Size > 0 ? file.Size : -1);

                await using Stream input = await response.Content.ReadAsStreamAsync(cancel);
                await using var output = new FileStream(
                    partPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

                byte[] buffer = new byte[81920];
                long received = 0;
                double reported = 0;
                long reportedBytes = 0;

                while (true)
                {
                    int read;

                    // Each read gets its own deadline: a stalled connection
                    // would otherwise hang the download forever.
                    using (var stall = CancellationTokenSource.CreateLinkedTokenSource(cancel))
                    {
                        stall.CancelAfter(StallTimeout);

                        try
                        {
                            read = await input.ReadAsync(buffer, stall.Token);
                        }
                        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
                        {
                            throw new ApiException($"{file.Name}: the download stalled.");
                        }
                    }

                    if (read == 0)
                    {
                        break;
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancel);
                    received += read;

                    if (total > 0)
                    {
                        double fraction = Math.Min(1.0, (double)received / total);

                        // Half a percent at a time: every 80 KB would flood the screen thread.
                        if (fraction - reported >= 0.005)
                        {
                            reported = fraction;
                            report(fraction, received);
                        }
                    }
                    else if (received - reportedBytes >= 256 * 1024)
                    {
                        reportedBytes = received;
                        report(null, received);
                    }
                }
            }

            report(1.0, 0);
        }
        catch (HttpRequestException error)
        {
            throw new ApiException($"{file.Name}: {error.Message}");
        }
        catch (IOException error)
        {
            throw new ApiException($"{file.Name}: {error.Message}");
        }
    }

    /// <summary>
    /// Why a file could not be downloaded. A server can switch source downloads
    /// off (Forgejo's DISABLE_DOWNLOAD_SOURCE_ARCHIVES, as git.magicbean.dev
    /// does) while its API still lists the archive addresses, and nothing in
    /// the API tells: the archive only fails with 404 when fetched, and the
    /// usual "check the repository name" would send the user the wrong way.
    /// </summary>
    internal static string FailureText(Site site, ReleaseFile file, HttpResponseMessage response) =>
        file.IsSource && response.StatusCode == System.Net.HttpStatusCode.NotFound
            ? $"{file.Name}: this server does not offer source code downloads (404)."
            : $"{file.Name}: {ApiClient.Describe(site, response)}";

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A leftover ".part" is harmless and is overwritten by the next attempt.
        }
    }
}
