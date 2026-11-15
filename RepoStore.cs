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
        CurrentFiles, Error, CheckedAt, KeepPrevious
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
        KeepPrevious = reader.GetInt64(18) != 0
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
              INSERT INTO Repos (Name, SiteId, Path, Folder, FilterId, KeepPrevious)
              VALUES ($name, $site, $path, $folder, $filter, $keep);
              SELECT last_insert_rowid();
              """
            : """
              UPDATE Repos SET Name = $name, SiteId = $site, Path = $path,
                               Folder = $folder, FilterId = $filter, KeepPrevious = $keep
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
