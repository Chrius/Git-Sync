using System.Globalization;
using Microsoft.Data.Sqlite;

namespace GitSync;

/// <summary>
/// Connection and schema.
///
/// The database is a single file, Git-Sync.db, beside the executable. The
/// engine is embedded in the .exe, so there is nothing to install on the
/// target machine.
/// </summary>
internal static class Database
{
    /// <summary>
    /// The journal stays in the default mode (DELETE) on purpose. WAL is
    /// faster, but it leaves -wal and -shm files beside the database, and on an
    /// external drive pulled out without ejecting that is exactly what corrupts
    /// the set.
    /// </summary>
    private static string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = AppPaths.DatabaseFile,
        Mode = SqliteOpenMode.ReadWriteCreate,
        ForeignKeys = true
    }.ToString();

    internal static SqliteConnection Open()
    {
        var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        return connection;
    }

    /// <summary>Creates the file and the tables on the first run. Idempotent.</summary>
    internal static void Prepare()
    {
        using SqliteConnection connection = Open();

        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS Filters (
                    Id         INTEGER PRIMARY KEY,
                    ParentId   INTEGER NULL REFERENCES Filters(Id) ON DELETE CASCADE,
                    Name       TEXT    NOT NULL,
                    SortOrder  INTEGER NOT NULL DEFAULT 0,
                    Expanded   INTEGER NOT NULL DEFAULT 1
                );

                CREATE TABLE IF NOT EXISTS Sites (
                    Id       INTEGER PRIMARY KEY,
                    Host     TEXT NOT NULL UNIQUE COLLATE NOCASE,
                    ApiType  TEXT NOT NULL,
                    Token    TEXT NOT NULL DEFAULT ''
                );

                CREATE TABLE IF NOT EXISTS Repos (
                    Id            INTEGER PRIMARY KEY,
                    Name          TEXT    NOT NULL,
                    SiteId        INTEGER NOT NULL REFERENCES Sites(Id),
                    Path          TEXT    NOT NULL,
                    Folder        TEXT    NOT NULL,
                    FilterId      INTEGER NULL REFERENCES Filters(Id),
                    LatestTag     TEXT    NULL,
                    LatestDate    TEXT    NULL,
                    LatestKind    INTEGER NOT NULL DEFAULT 0,
                    CurrentTag    TEXT    NULL,
                    CurrentDate   TEXT    NULL,
                    CurrentKind   INTEGER NOT NULL DEFAULT 0,
                    SelectedTag   TEXT    NULL,
                    SelectedDate  TEXT    NULL,
                    SelectedKind  INTEGER NOT NULL DEFAULT 0,
                    CurrentFiles  TEXT    NOT NULL DEFAULT '',
                    Error         TEXT    NOT NULL DEFAULT '',
                    CheckedAt     TEXT    NULL,
                    KeepPrevious  INTEGER NOT NULL DEFAULT 0
                );

                CREATE TABLE IF NOT EXISTS Preferences (
                    Key    TEXT PRIMARY KEY,
                    Value  TEXT NOT NULL
                );
                """;

            command.ExecuteNonQuery();
        }

        // Added after the first version: databases created before it lack the
        // column, and CREATE TABLE IF NOT EXISTS above does not add it to them.
        EnsureColumn(connection, "Repos", "KeepPrevious", "INTEGER NOT NULL DEFAULT 0");

        EnsureSites(connection);
    }

    /// <summary>
    /// The three supported sites, one per API. Each API needs its own code,
    /// so the user cannot add or delete sites — only set their tokens — and
    /// they are put back on every start if a row is ever missing. OR IGNORE
    /// keeps the existing rows, and with them the saved tokens.
    /// </summary>
    private static void EnsureSites(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO Sites (Host, ApiType) VALUES
                ('github.com',   'GitHub'),
                ('gitlab.com',   'GitLab'),
                ('codeberg.org', 'Gitea');
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Adds a column if the table does not have it yet. CREATE TABLE IF NOT
    /// EXISTS does not reach a table that already exists, so every column added
    /// in a later version has to be added here, or the database of whoever used
    /// the previous version breaks on the first query.
    /// </summary>
    internal static void EnsureColumn(SqliteConnection connection, string table, string column, string definition)
    {
        using (SqliteCommand query = connection.CreateCommand())
        {
            query.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $column";
            query.Parameters.AddWithValue("$column", column);

            if (Convert.ToInt32(query.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture) > 0)
            {
                return;
            }
        }

        using SqliteCommand alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        alter.ExecuteNonQuery();
    }

    // ---------- value conversion shared by the stores ----------

    /// <summary>Dates go to the database as round-trip UTC text, never in the machine's culture.</summary>
    internal static object DateValue(DateTimeOffset? date) =>
        date is { } d ? d.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) : DBNull.Value;

    internal static DateTimeOffset? ReadDate(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            reader.GetString(ordinal), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset date)
            ? date
            : null;
    }

    internal static object NullableId(long? id) => id is { } value ? value : DBNull.Value;
}
