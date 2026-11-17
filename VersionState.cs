namespace GitSync;

internal enum RepoStatus
{
    /// <summary>Added but not checked yet.</summary>
    NeverChecked,

    /// <summary>Checked: the repo has no releases and no commits.</summary>
    NoReleases,

    UpToDate,

    /// <summary>Something was downloaded, and the target version is a different one.</summary>
    Update,

    /// <summary>Nothing downloaded yet.</summary>
    Download
}

/// <summary>Decides what a repo's button offers, and which repos Update All goes through.</summary>
internal static class VersionState
{
    /// <summary>The version the button downloads: the pinned one when there is a pin, otherwise Latest.</summary>
    internal static VersionRef? Target(Repo repo) => repo.Selected ?? repo.Latest;

    /// <summary>The repos a refresh checks: all but those set to "Ignore on refresh".</summary>
    internal static List<Repo> ToRefresh(IEnumerable<Repo> repos) => [.. repos.Where(r => !r.IgnoreOnRefresh)];

    internal static RepoStatus StatusOf(Repo repo)
    {
        VersionRef? target = Target(repo);

        if (target is null)
        {
            return repo.CheckedAt is null ? RepoStatus.NeverChecked : RepoStatus.NoReleases;
        }

        if (repo.Current is null)
        {
            return RepoStatus.Download;
        }

        return NeedsUpdate(target, repo.Current, pinned: repo.Selected is not null)
            ? RepoStatus.Update
            : RepoStatus.UpToDate;
    }

    /// <summary>
    /// A pin means "this tag": any other tag on disk needs the download, older
    /// or newer. Following Latest means "newer": decided by release date, not
    /// by tag text, because tags like "nightly" never change while the release
    /// behind them does — and because "v10" sorts before "v9" as text.
    /// </summary>
    internal static bool NeedsUpdate(VersionRef target, VersionRef current, bool pinned)
    {
        if (pinned)
        {
            return !SameVersion(target, current);
        }

        if (target.Date is { } targetDate && current.Date is { } currentDate)
        {
            return targetDate > currentDate;
        }

        return !SameVersion(target, current);
    }

    /// <summary>Same tag (or same commit). A release and its own source archive are the same version.</summary>
    internal static bool SameVersion(VersionRef a, VersionRef b) =>
        string.Equals(a.Tag, b.Tag, StringComparison.Ordinal)
        && (a.Kind == VersionKind.Commit) == (b.Kind == VersionKind.Commit);

    /// <summary>Repos Update All goes through: only those already downloaded once.</summary>
    internal static List<Repo> PendingUpdates(IEnumerable<Repo> repos) =>
        [.. repos.Where(r => StatusOf(r) == RepoStatus.Update)];
}
