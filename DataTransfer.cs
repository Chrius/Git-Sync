using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace GitSync;

/// <summary>A repository as an export file describes it: what another install needs to add it again.</summary>
/// <param name="Filter">The folder it sits in, as names from the root; empty when it is in none.</param>
internal sealed record ExportedRepo(
    string Name, string Host, ApiType Type, string Path, string Folder,
    IReadOnlyList<string> Filter, bool KeepPrevious, int KeepVersions = 0);

/// <summary>The content of an export file.</summary>
/// <param name="Filters">Folders as names from the root, parents before children.</param>
internal sealed record ExportData(List<IReadOnlyList<string>> Filters, List<ExportedRepo> Repos);

/// <summary>What an import changed.</summary>
/// <param name="LeftOut">New repositories not imported because their folder was set to "Do not import".</param>
internal sealed record ImportResult(List<long> AddedRepoIds, int AlreadyThere, int FoldersAdded, int LeftOut);

/// <summary>Where the repositories of one folder of the file go (the "Goes into" of the import window).</summary>
/// <param name="Skip">"Do not import": every repository of that folder is left out.</param>
/// <param name="CreatePath">A folder path here, created if missing (an empty path is no folder).</param>
/// <param name="FolderId">An existing folder here, or null for no folder; used when <paramref name="CreatePath"/> is null.</param>
internal sealed record ImportTarget(bool Skip, IReadOnlyList<string>? CreatePath, long? FolderId)
{
    /// <summary>The same folder as in the file — the default.</summary>
    internal static ImportTarget SameAs(IReadOnlyList<string> path) => new(false, path, null);

    internal static ImportTarget DoNotImport { get; } = new(true, null, null);

    internal static ImportTarget Into(long? folderId) => new(false, null, folderId);
}

/// <summary>
/// Export and import of the folder tree and the repositories (Settings → Data).
///
/// The file is JSON: readable and editable by hand, built into .NET, and able
/// to hold the tree and each repository's settings in one file — a CSV would
/// need the tree flattened into paths, and a copy of the database would carry
/// the tokens and this install's download history along.
///
/// Only what describes the list goes out: no tokens (the file may be shared,
/// and each install has its own), nothing about what was downloaded here
/// (Current, its files) or fetched from the sites (Latest), which the other
/// install works out for itself, and no pinned version — whoever imports the
/// file picks their own versions.
///
/// Importing only ever adds. A repository already in the list — same site,
/// same path — is skipped, so several files can be imported one after another,
/// or the same one twice, and only what is missing comes in.
/// </summary>
internal static class DataTransfer
{
    internal const string FileFilter = "Git-Sync data (*.json)|*.json|All files (*.*)|*.*";

    private const string FormatName = "Git-Sync data";
    private const int FormatVersion = 1;

    /// <summary>The Edit window's field takes three digits; a file cannot ask for more.</summary>
    internal const int MaxVersionsToKeep = 999;
    private const string NotOurs = "This is not a Git-Sync data file.";

    // ---------- export ----------

    /// <summary>
    /// The list as it will be written. A folder goes in only when a repository
    /// sits in it or somewhere below it: empty ones would only clutter the
    /// other install's tree. Its parents go in with it, being part of its path.
    /// </summary>
    internal static ExportData Collect(IReadOnlyList<FilterFolder> filters, IReadOnlyList<Repo> repos, IReadOnlyList<Site> sites)
    {
        var data = new ExportData([], []);
        var used = new HashSet<long>();

        foreach (Repo repo in repos)
        {
            // A repo whose site is gone has nothing to point at on the other side.
            if (sites.FirstOrDefault(s => s.Id == repo.SiteId) is not { } site)
            {
                continue;
            }

            List<string> filter = repo.FilterId is { } id ? FilterStore.PathOf(id, filters) : [];

            data.Repos.Add(new ExportedRepo(
                repo.Name, site.Host, site.Type, repo.Path, repo.Folder, filter, repo.KeepPrevious, repo.KeepVersions));

            for (long? current = repo.FilterId; current is { } folderId && used.Add(folderId);)
            {
                current = filters.FirstOrDefault(f => f.Id == folderId)?.ParentId;
            }
        }

        foreach ((FilterFolder folder, _) in FilterStore.Flatten(filters))
        {
            if (used.Contains(folder.Id))
            {
                data.Filters.Add(FilterStore.PathOf(folder.Id, filters));
            }
        }

        return data;
    }

    internal static string ToJson(ExportData data, DateTimeOffset exported)
    {
        var options = new JsonWriterOptions
        {
            Indented = true,

            // Names stay readable ("Pokémon", not "Pokémon"). The file
            // never ends up inside a web page, which is what the strict
            // default escaping guards against.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        using var buffer = new MemoryStream();

        using (var json = new Utf8JsonWriter(buffer, options))
        {
            json.WriteStartObject();
            json.WriteString("format", FormatName);
            json.WriteNumber("version", FormatVersion);
            json.WriteString("exported", exported.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));

            json.WriteStartArray("filters");

            foreach (IReadOnlyList<string> path in data.Filters)
            {
                WritePath(json, path);
            }

            json.WriteEndArray();

            json.WriteStartArray("repos");

            foreach (ExportedRepo repo in data.Repos)
            {
                json.WriteStartObject();
                json.WriteString("name", repo.Name);
                json.WriteString("site", repo.Host);
                json.WriteString("api", repo.Type.ToString());
                json.WriteString("repository", repo.Path);
                json.WriteString("folder", repo.Folder);
                json.WritePropertyName("filter");
                WritePath(json, repo.Filter);
                json.WriteBoolean("keepPreviousVersions", repo.KeepPrevious);

                // Only when set: a file without it means "keep them all", as before the number existed.
                if (repo.KeepVersions > 0)
                {
                    json.WriteNumber("versionsToKeep", repo.KeepVersions);
                }
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// A folder path as "Games/PC" ("" for none). A slash, not the backslash
    /// of the screens: JSON would double every backslash ("Games\\PC"). No
    /// folder name can hold a slash, so it never splits a name.
    /// </summary>
    private static void WritePath(Utf8JsonWriter json, IReadOnlyList<string> path) =>
        json.WriteStringValue(string.Join("/", path));

    // ---------- reading a file ----------

    /// <summary>
    /// Reads an export file. Everything is checked before anything is added:
    /// a file edited by hand with a bad folder name or address is refused
    /// whole, with the reason, rather than half imported.
    /// </summary>
    /// <exception cref="FormatException">The text is not a valid export, with a message for the user.</exception>
    internal static ExportData Parse(string text)
    {
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
        }
        catch (JsonException)
        {
            throw new FormatException(NotOurs);
        }

        using (document)
        {
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object || Text(root, "format") != FormatName)
            {
                throw new FormatException(NotOurs);
            }

            if (root.TryGetProperty("version", out JsonElement version)
                && version.ValueKind == JsonValueKind.Number
                && version.TryGetInt32(out int number) && number > FormatVersion)
            {
                throw new FormatException("This file was made by a newer Git-Sync. Update Git-Sync to import it.");
            }

            var data = new ExportData([], []);

            foreach (JsonElement entry in Items(root, "filters"))
            {
                List<string> path = ReadPath(entry);

                if (path.Count > 0)
                {
                    AddFolder(data, path);
                }
            }

            int index = 0;

            foreach (JsonElement entry in Items(root, "repos"))
            {
                index++;
                ExportedRepo repo = ReadRepo(entry, index);
                data.Repos.Add(repo);

                // A folder named only by a repo (a file edited by hand) is a folder of the file all the same.
                if (repo.Filter.Count > 0)
                {
                    AddFolder(data, repo.Filter);
                }
            }

            return data;
        }
    }

    private static ExportedRepo ReadRepo(JsonElement e, int index)
    {
        string name = Text(e, "name").Trim();
        string host = Text(e, "site").Trim().TrimEnd('/');
        string path = RepoAddress.NormalizePath(Text(e, "repository"));
        string folder = Text(e, "folder").Trim();
        string where = name.Length > 0 ? $"\"{name}\"" : $"Repository {index}";

        if (name.Length == 0 || host.Length == 0)
        {
            throw new FormatException($"{where} in the file has no name or no site.");
        }

        if (!Enum.TryParse(Text(e, "api"), ignoreCase: true, out ApiType type) || !Enum.IsDefined(type))
        {
            throw new FormatException($"{where} in the file has an unknown API type.");
        }

        if (!RepoAddress.IsValidPath(path, type))
        {
            throw new FormatException($"{where} in the file has an invalid repository address.");
        }

        if (!DownloadPaths.IsValidName(folder))
        {
            throw new FormatException($"{where} in the file has a folder name Windows does not allow.");
        }

        List<string> filter = e.TryGetProperty("filter", out JsonElement filterElement) ? ReadPath(filterElement) : [];

        // A "selectedVersion" in a file from an earlier test build is ignored:
        // whoever imports picks their own versions.
        bool keep = e.TryGetProperty("keepPreviousVersions", out JsonElement keepElement)
            && keepElement.ValueKind == JsonValueKind.True;

        int versions = e.TryGetProperty("versionsToKeep", out JsonElement versionsElement)
            && versionsElement.TryGetInt32(out int count) && count > 0
                ? Math.Min(count, MaxVersionsToKeep)
                : 0;

        return new ExportedRepo(name, host, type, path, folder, filter, keep, versions);
    }

    /// <summary>Adds a folder and its parents to the file's list, each once, parents first.</summary>
    private static void AddFolder(ExportData data, IReadOnlyList<string> path)
    {
        for (int length = 1; length <= path.Count; length++)
        {
            List<string> partial = [.. path.Take(length)];

            if (!data.Filters.Any(f => Key(f) == Key(partial)))
            {
                data.Filters.Add(partial);
            }
        }
    }

    /// <summary>"Games/PC", or a list of names ["Games", "PC"] in a file written by hand.</summary>
    private static List<string> ReadPath(JsonElement e)
    {
        List<string> names = e.ValueKind switch
        {
            JsonValueKind.String => [.. (e.GetString() ?? string.Empty)
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
            JsonValueKind.Array => [.. e.EnumerateArray()
                .Where(n => n.ValueKind == JsonValueKind.String)
                .Select(n => (n.GetString() ?? string.Empty).Trim())],
            _ => []
        };

        // Folder names become real folders under Apps.
        if (names.FirstOrDefault(n => !DownloadPaths.IsValidName(n)) is { } bad)
        {
            throw new FormatException($"The file has a folder name Windows does not allow: \"{bad}\".");
        }

        return names;
    }

    /// <summary>
    /// The entries of an array property, or none when it is missing. Not a
    /// default ArrayEnumerator: that one has no document behind it and throws
    /// when looped over.
    /// </summary>
    private static IEnumerable<JsonElement> Items(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : [];

    private static string Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object
        && e.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    // ---------- comparing a file with this install ----------

    /// <summary>
    /// The key of a folder path, for matching folders and the destinations of
    /// the folder window. Windows folder names ignore case, and cannot hold a
    /// backslash, so joined with one they never collide.
    /// </summary>
    internal static string Key(IReadOnlyList<string> path) => string.Join("\\", path).ToUpperInvariant();

    /// <summary>The same site (host, ignoring case) already has a repository at this path.</summary>
    internal static bool IsAlreadyThere(ExportedRepo repo, IReadOnlyList<Repo> repos, IReadOnlyList<Site> sites) =>
        ExistingKeys(repos, sites).Contains(RepoKey(repo.Host, repo.Path));

    /// <summary>
    /// Every repository here as "HOST|PATH". Looked up once per entry of the
    /// file: comparing each entry with the whole list instead would grow with
    /// the square of a big import.
    /// </summary>
    internal static HashSet<string> ExistingKeys(IReadOnlyList<Repo> repos, IReadOnlyList<Site> sites)
    {
        Dictionary<long, string> hosts = sites.ToDictionary(s => s.Id, s => s.Host);

        return [.. repos.Where(r => hosts.ContainsKey(r.SiteId)).Select(r => RepoKey(hosts[r.SiteId], r.Path))];
    }

    private static string RepoKey(string host, string path) => (host + "|" + path).ToUpperInvariant();

    /// <summary>How many of the file's repositories are not here yet.</summary>
    internal static int NewRepos(ExportData data, IReadOnlyList<Repo> repos, IReadOnlyList<Site> sites)
    {
        HashSet<string> existing = ExistingKeys(repos, sites);
        return data.Repos.Count(r => !existing.Contains(RepoKey(r.Host, r.Path)));
    }

    /// <summary>The local folder at this path, or null when some part of it does not exist here.</summary>
    internal static long? FindFolder(IReadOnlyList<string> path, IReadOnlyList<FilterFolder> filters)
    {
        long? parent = null;

        foreach (string name in path)
        {
            FilterFolder? match = filters.FirstOrDefault(f =>
                f.ParentId == parent && string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                return null;
            }

            parent = match.Id;
        }

        return parent;
    }

    /// <summary>
    /// The file's folders that hold repositories to import, in the file's
    /// order, for the folder window. "No folder" (an empty path) comes first
    /// when some repositories were in none.
    /// </summary>
    internal static List<(IReadOnlyList<string> Path, int NewRepos)> SourceFolders(
        ExportData data, IReadOnlyList<Repo> repos, IReadOnlyList<Site> sites)
    {
        HashSet<string> existing = ExistingKeys(repos, sites);
        Dictionary<string, int> counts = data.Repos
            .Where(r => !existing.Contains(RepoKey(r.Host, r.Path)))
            .GroupBy(r => Key(r.Filter))
            .ToDictionary(g => g.Key, g => g.Count());

        var result = new List<(IReadOnlyList<string>, int)>();

        if (counts.TryGetValue(Key([]), out int loose))
        {
            result.Add(([], loose));
        }

        foreach (IReadOnlyList<string> path in data.Filters)
        {
            if (counts.TryGetValue(Key(path), out int count))
            {
                result.Add((path, count));
            }
        }

        return result;
    }

    // ---------- importing ----------

    /// <summary>
    /// Adds what is missing. Nothing already here is changed: an existing
    /// repository keeps its name, folder and settings even when the file
    /// says otherwise. A folder is created only when a repository goes into
    /// it, so a folder set to "Do not import" leaves no empty folder behind.
    /// </summary>
    /// <param name="targets">
    /// Where each of the file's folders goes, by <see cref="Key"/>. A folder
    /// without an entry goes into the same folder as in the file.
    /// </param>
    /// <param name="progress">How many of the file's repositories have been handled so far.</param>
    internal static ImportResult Import(
        ExportData data, IReadOnlyDictionary<string, ImportTarget>? targets = null, IProgress<int>? progress = null)
    {
        List<FilterFolder> filters = FilterStore.List();
        List<Repo> repos = RepoStore.List();
        List<Site> sites = SiteStore.List();
        int foldersAdded = 0;

        // One transaction for the whole import: fast (see StoreCommand), and
        // an error halfway rolls everything back instead of leaving part of it.
        using SqliteConnection connection = Database.Open();
        using SqliteTransaction transaction = connection.BeginTransaction();

        long? EnsureFolder(IReadOnlyList<string> path)
        {
            long? parent = null;

            foreach (string name in path)
            {
                FilterFolder? match = filters.FirstOrDefault(f =>
                    f.ParentId == parent && string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

                if (match is null)
                {
                    match = new FilterFolder { Id = FilterStore.Add(parent, name, transaction), ParentId = parent, Name = name };
                    filters.Add(match);
                    foldersAdded++;
                }

                parent = match.Id;
            }

            return parent;
        }

        var added = new List<long>();
        int alreadyThere = 0;
        int leftOut = 0;
        int handled = 0;
        HashSet<string> existing = ExistingKeys(repos, sites);

        // About 200 reports whatever the size: one per repository would flood
        // the window's message queue on a big file.
        int step = Math.Max(1, data.Repos.Count / 200);

        foreach (ExportedRepo entry in data.Repos)
        {
            if (handled % step == 0)
            {
                progress?.Report(handled);
            }

            handled++;

            string key = RepoKey(entry.Host, entry.Path);

            if (existing.Contains(key))
            {
                alreadyThere++;
                continue;
            }

            ImportTarget target = targets?.GetValueOrDefault(Key(entry.Filter)) ?? ImportTarget.SameAs(entry.Filter);

            if (target.Skip)
            {
                leftOut++;
                continue;
            }

            // Each one added joins the set: a file listing the same repo twice adds it once.
            existing.Add(key);

            Site? site = sites.FirstOrDefault(s => string.Equals(s.Host, entry.Host, StringComparison.OrdinalIgnoreCase));

            if (site is null)
            {
                // A self-hosted server this install has not seen: added without a token, as detection would.
                site = SiteStore.Add(entry.Host, entry.Type, transaction);
                sites.Add(site);
            }

            long? filterId = target.CreatePath is { } path
                ? path.Count == 0 ? null : EnsureFolder(path)
                : target.FolderId;

            var repo = new Repo
            {
                Name = entry.Name,
                SiteId = site.Id,
                Path = entry.Path,
                Folder = entry.Folder,
                FilterId = filterId,
                KeepPrevious = entry.KeepPrevious,
                KeepVersions = entry.KeepVersions
            };

            RepoStore.Save(repo, transaction);
            added.Add(repo.Id);
        }

        transaction.Commit();
        progress?.Report(handled);
        return new ImportResult(added, alreadyThere, foldersAdded, leftOut);
    }
}
