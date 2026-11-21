using System.Globalization;
using Microsoft.Data.Sqlite;

namespace GitSync;

/// <summary>
/// Reads and writes the Repos table.
///
/// Each kind of change writes only its own columns: a Refresh that finishes
/// while a download is running must not undo the Current the download just
/// saved, and the other way around.
/// </summary>
internal static class RepoStore
{
    private const string Columns = """
        Id, Name, SiteId, Path, Folder, FilterId,
        LatestTag, LatestDate, LatestKind,
        CurrentTag, CurrentDate, CurrentKind,
        SelectedTag, SelectedDate, SelectedKind,
        CurrentFiles, Error, CheckedAt, KeepPrevious, KeepVersions, IgnoreOnRefresh, IgnorePrereleases
        """;

    internal static List<Repo> List()
    {
        using SqliteConnection connection = Database.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM Repos ORDER BY Name COLLATE NOCASE, Id";

        var repos = new List<Repo>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            repos.Add(Read(reader));
        }

        return repos;
    }

    internal static Repo? Get(long id)
    {
        using SqliteConnection connection = Database.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM Repos WHERE Id = $id";
        command.Parameters.AddWithValue("$id", id);

        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    private static Repo Read(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Name = reader.GetString(1),
        SiteId = reader.GetInt64(2),
        Path = reader.GetString(3),
        Folder = reader.GetString(4),
        FilterId = reader.IsDBNull(5) ? null : reader.GetInt64(5),
        Latest = ReadVersion(reader, 6),
        Current = ReadVersion(reader, 9),
        Selected = ReadVersion(reader, 12),
        CurrentFiles = [.. reader.GetString(15).Split('\n', StringSplitOptions.RemoveEmptyEntries)],
        Error = reader.GetString(16),
        CheckedAt = Database.ReadDate(reader, 17),
        KeepPrevious = reader.GetInt64(18) != 0,
        KeepVersions = reader.GetInt32(19),
        IgnoreOnRefresh = reader.GetInt64(20) != 0,
        IgnorePrereleases = reader.GetInt64(21) != 0
    };

    /// <summary>Tag, date and kind live in three consecutive columns.</summary>
    private static VersionRef? ReadVersion(SqliteDataReader reader, int first)
    {
        if (reader.IsDBNull(first))
        {
            return null;
        }

        return new VersionRef(
            reader.GetString(first),
            Database.ReadDate(reader, first + 1),
            (VersionKind)reader.GetInt32(first + 2));
    }

    private static void AddVersion(SqliteCommand command, string prefix, VersionRef? version)
    {
        command.Parameters.AddWithValue($"${prefix}Tag", (object?)version?.Tag ?? DBNull.Value);
        command.Parameters.AddWithValue($"${prefix}Date", Database.DateValue(version?.Date));
        command.Parameters.AddWithValue($"${prefix}Kind", (int)(version?.Kind ?? VersionKind.Release));
    }

    /// <summary>
    /// Inserts when Id is zero, otherwise updates the fields of the Add/Edit
    /// window. Versions are never written here.
    /// </summary>
    /// <param name="transaction">The import's transaction (see <see cref="StoreCommand"/>); null for a write of its own.</param>
    internal static long Save(Repo repo, SqliteTransaction? transaction = null)
    {
        using var store = new StoreCommand(transaction);
        SqliteCommand command = store.Command;

        command.CommandText = repo.Id == 0
            ? """
              INSERT INTO Repos (Name, SiteId, Path, Folder, FilterId, KeepPrevious, KeepVersions, IgnoreOnRefresh, IgnorePrereleases)
              VALUES ($name, $site, $path, $folder, $filter, $keep, $versions, $ignore, $stableOnly);
              SELECT last_insert_rowid();
              """
            : """
              UPDATE Repos SET Name = $name, SiteId = $site, Path = $path,
                               Folder = $folder, FilterId = $filter, KeepPrevious = $keep,
                               KeepVersions = $versions, IgnoreOnRefresh = $ignore,
                               IgnorePrereleases = $stableOnly
               WHERE Id = $id;
              SELECT $id;
              """;

        command.Parameters.AddWithValue("$id", repo.Id);
        command.Parameters.AddWithValue("$name", repo.Name);
        command.Parameters.AddWithValue("$site", repo.SiteId);
        command.Parameters.AddWithValue("$path", repo.Path);
        command.Parameters.AddWithValue("$folder", repo.Folder);
        command.Parameters.AddWithValue("$filter", Database.NullableId(repo.FilterId));
        command.Parameters.AddWithValue("$keep", repo.KeepPrevious ? 1 : 0);
        command.Parameters.AddWithValue("$versions", Math.Max(0, repo.KeepVersions));
        command.Parameters.AddWithValue("$ignore", repo.IgnoreOnRefresh ? 1 : 0);
        command.Parameters.AddWithValue("$stableOnly", repo.IgnorePrereleases ? 1 : 0);

        repo.Id = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        return repo.Id;
    }

    internal static void Delete(long id)
    {
        using SqliteConnection connection = Database.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Repos WHERE Id = $id";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>A successful check: the new Latest (null when the repo has nothing at all) and no error.</summary>
    internal static void SetLatest(long id, VersionRef? latest, DateTimeOffset checkedAt)
    {
        using SqliteConnection connection = Database.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Repos SET LatestTag = $latestTag, LatestDate = $latestDate, LatestKind = $latestKind,
                             Error = '', CheckedAt = $checked
             WHERE Id = $id
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$checked", Database.DateValue(checkedAt));
        AddVersion(command, "latest", latest);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// A failed check keeps the last Latest that was seen: the site being down
    /// for a moment does not make the release disappear.
    /// </summary>
    internal static void SetError(long id, string error, DateTimeOffset checkedAt)
    {
        using SqliteConnection connection = Database.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE Repos SET Error = $error, CheckedAt = $checked WHERE Id = $id";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$error", error);
        command.Parameters.AddWithValue("$checked", Database.DateValue(checkedAt));
        command.ExecuteNonQuery();
    }

    /// <summary>Pins a version, or goes back to following Latest with null.</summary>
    internal static void SetSelected(long id, VersionRef? selected)
    {
        using SqliteConnection connection = Database.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Repos SET SelectedTag = $selectedTag, SelectedDate = $selectedDate, SelectedKind = $selectedKind
             WHERE Id = $id
            """;
        command.Parameters.AddWithValue("$id", id);
        AddVersion(command, "selected", selected);
        command.ExecuteNonQuery();
    }

    /// <summary>A finished download: what is now on disk and which files belong to it.</summary>
    internal static void SetCurrent(long id, VersionRef current, IReadOnlyList<string> files)
    {
        using SqliteConnection connection = Database.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Repos SET CurrentTag = $currentTag, CurrentDate = $currentDate, CurrentKind = $currentKind,
                             CurrentFiles = $files
             WHERE Id = $id
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$files", string.Join("\n", files));
        AddVersion(command, "current", current);
        command.ExecuteNonQuery();
    }

    /// <summary>The versions kept by "Keep previous versions", oldest first.</summary>
    internal static List<PreviousVersion> PreviousVersions(long repoId)
    {
        using SqliteConnection connection = Database.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT Tag, Date, Kind, Files FROM PreviousVersions WHERE RepoId = $id ORDER BY Id";
        command.Parameters.AddWithValue("$id", repoId);

        var versions = new List<PreviousVersion>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            versions.Add(new PreviousVersion(
                new VersionRef(reader.GetString(0), Database.ReadDate(reader, 1), (VersionKind)reader.GetInt32(2)),
                [.. reader.GetString(3).Split('\n', StringSplitOptions.RemoveEmptyEntries)]));
        }

        return versions;
    }

    /// <summary>
    /// A finished download that keeps previous versions: Current and its
    /// files, and the whole list of kept versions, in one transaction — a list
    /// written apart from Current could leave a version both current and kept.
    /// </summary>
    internal static void SetCurrent(long id, VersionRef current, IReadOnlyList<string> files, IReadOnlyList<PreviousVersion> previous)
    {
        using SqliteConnection connection = Database.Open();
        using SqliteTransaction transaction = connection.BeginTransaction();

        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE Repos SET CurrentTag = $currentTag, CurrentDate = $currentDate, CurrentKind = $currentKind,
                                 CurrentFiles = $files
                 WHERE Id = $id
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$files", string.Join("\n", files));
            AddVersion(command, "current", current);
            command.ExecuteNonQuery();
        }

        WritePrevious(connection, transaction, id, previous);
        transaction.Commit();
    }

    /// <summary>
    /// The download folder was renamed or moved (Edit): the files are the same,
    /// their paths are new. Current's files and every kept version's, together.
    /// </summary>
    internal static void SetFiles(long id, IReadOnlyList<string> currentFiles, IReadOnlyList<PreviousVersion> previous)
    {
        using SqliteConnection connection = Database.Open();
        using SqliteTransaction transaction = connection.BeginTransaction();

        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE Repos SET CurrentFiles = $files WHERE Id = $id";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$files", string.Join("\n", currentFiles));
            command.ExecuteNonQuery();
        }

        WritePrevious(connection, transaction, id, previous);
        transaction.Commit();
    }

    /// <summary>
    /// A folder moved on disk (renamed or moved in Settings → Filters): every
    /// saved path inside it follows, for every repository and every kept
    /// version — also files a repo left in a folder it no longer downloads to.
    /// One transaction, writing only the rows that change. Returns how many.
    /// </summary>
    internal static int ApplyMoves(PathMoves moves)
    {
        using SqliteConnection connection = Database.Open();
        using SqliteTransaction transaction = connection.BeginTransaction();
        int changed = 0;

        // A repo's "Custom folder" inside the moved place moves with it.
        var folders = new List<(long Id, string Folder)>();

        using (SqliteCommand read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT Id, Folder FROM Repos";
            using SqliteDataReader reader = read.ExecuteReader();

            while (reader.Read())
            {
                folders.Add((reader.GetInt64(0), reader.GetString(1)));
            }
        }

        foreach ((long id, string folder) in folders.Where(f => DownloadPaths.IsCustomFolder(f.Folder)))
        {
            string moved = DownloadPaths.FullFolder(DownloadPaths.ToAbsolute(moves.Apply(folder)));

            if (!string.Equals(moved, DownloadPaths.FullFolder(folder), StringComparison.Ordinal))
            {
                using SqliteCommand update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE Repos SET Folder = $folder WHERE Id = $id";
                update.Parameters.AddWithValue("$id", id);
                update.Parameters.AddWithValue("$folder", moved);
                update.ExecuteNonQuery();
                changed++;
            }
        }

        foreach (string table in new[] { "Repos", "PreviousVersions" })
        {
            string column = table == "Repos" ? "CurrentFiles" : "Files";
            var rows = new List<(long Id, string Files)>();

            using (SqliteCommand read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = $"SELECT Id, {column} FROM {table}";
                using SqliteDataReader reader = read.ExecuteReader();

                while (reader.Read())
                {
                    rows.Add((reader.GetInt64(0), reader.GetString(1)));
                }
            }

            foreach ((long id, string files) in rows)
            {
                string moved = string.Join("\n", moves.Apply(files.Split('\n', StringSplitOptions.RemoveEmptyEntries)));

                if (moved == string.Join("\n", files.Split('\n', StringSplitOptions.RemoveEmptyEntries)))
                {
                    continue;
                }

                using SqliteCommand update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = $"UPDATE {table} SET {column} = $files WHERE Id = $id";
                update.Parameters.AddWithValue("$id", id);
                update.Parameters.AddWithValue("$files", moved);
                update.ExecuteNonQuery();
                changed++;
            }
        }

        transaction.Commit();
        return changed;
    }

    /// <summary>
    /// A new download folder without the files of the old one: no record
    /// lists a file inside <paramref name="folder"/> any more, current or
    /// kept, so nothing reports them missing and no update deletes or moves
    /// them. Files elsewhere (a repo's own "Custom folder") stay recorded.
    /// The versions themselves stay, so a repository is still shown as up to date.
    /// </summary>
    internal static void ForgetFilesUnder(string folder)
    {
        string root = DownloadPaths.FullFolder(folder);
        bool Outside(string file) => !DownloadRoot.IsInside(DownloadPaths.ToAbsolute(file), root);

        foreach (Repo repo in List())
        {
            List<PreviousVersion> kept = PreviousVersions(repo.Id);
            List<string> current = [.. repo.CurrentFiles.Where(Outside)];
            List<PreviousVersion> left = [.. kept.Select(v => v with { Files = [.. v.Files.Where(Outside)] }).Where(v => v.Files.Count > 0)];

            if (current.Count != repo.CurrentFiles.Count || left.Count != kept.Count || left.Sum(v => v.Files.Count) != kept.Sum(v => v.Files.Count))
            {
                SetFiles(repo.Id, current, left);
            }
        }
    }

    /// <summary>Replaces the repo's kept versions with this list, keeping its order.</summary>
    private static void WritePrevious(SqliteConnection connection, SqliteTransaction transaction, long id, IReadOnlyList<PreviousVersion> previous)
    {
        using (SqliteCommand delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM PreviousVersions WHERE RepoId = $id";
            delete.Parameters.AddWithValue("$id", id);
            delete.ExecuteNonQuery();
        }

        foreach (PreviousVersion version in previous)
        {
            using SqliteCommand insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO PreviousVersions (RepoId, Tag, Date, Kind, Files)
                VALUES ($id, $tag, $date, $kind, $files)
                """;
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$tag", version.Version.Tag);
            insert.Parameters.AddWithValue("$date", Database.DateValue(version.Version.Date));
            insert.Parameters.AddWithValue("$kind", (int)version.Version.Kind);
            insert.Parameters.AddWithValue("$files", string.Join("\n", version.Files));
            insert.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// After Edit points the entry at another repository, what was checked and
    /// pinned belonged to the old one. Current and its files stay: they are
    /// still on disk, and the next download replaces them.
    /// </summary>
    internal static void ForgetRemoteState(long id)
    {
        using SqliteConnection connection = Database.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Repos SET LatestTag = NULL, LatestDate = NULL, LatestKind = 0,
                             SelectedTag = NULL, SelectedDate = NULL, SelectedKind = 0,
                             Error = '', CheckedAt = NULL
             WHERE Id = $id
            """;
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>Another entry for the same repository on the same site, if there is one.</summary>
    internal static Repo? FindDuplicate(long siteId, string path, long exceptId) =>
        List().FirstOrDefault(r =>
            r.Id != exceptId && r.SiteId == siteId &&
            string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
}
