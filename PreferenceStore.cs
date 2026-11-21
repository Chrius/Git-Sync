using Microsoft.Data.Sqlite;

namespace GitSync;

/// <summary>
/// Small screen choices remembered between runs (the list order), kept in the
/// database beside everything else so they travel with the folder.
/// </summary>
internal static class PreferenceStore
{
    internal const string OrderKey = "ListOrder";

    private const string ShowSourceKey = "ShowSourceCode";

    /// <summary>"Show source code when downloading" (Automation tab). Off until it is ticked.</summary>
    internal static bool ShowSource() => Get(ShowSourceKey) == "1";

    internal static void SetShowSource(bool show) => Set(ShowSourceKey, show ? "1" : "0");

    private const string ReadmeKey = "DownloadReadme";

    /// <summary>
    /// "Download README.md" (Automation tab). On until it is unticked: only
    /// "0" turns it off, so a database from before the option keeps saving it.
    /// </summary>
    internal static bool DownloadReadme() => Get(ReadmeKey) != "0";

    internal static void SetDownloadReadme(bool on) => Set(ReadmeKey, on ? "1" : "0");

    private const string MoveOldKey = "MoveOldVersions";

    /// <summary>
    /// "Move previous versions to a different folder" (Automation tab). Off
    /// until it is ticked. Only acts on repositories that keep previous versions.
    /// </summary>
    internal static bool MoveOldVersions() => Get(MoveOldKey) == "1";

    internal static void SetMoveOldVersions(bool on) => Set(MoveOldKey, on ? "1" : "0");

    private const string IgnoredFilesKey = "IgnoredFiles";

    /// <summary>The Files tab's patterns of file names to hide, one per line (see <see cref="FileFilters"/>).</summary>
    internal static string IgnoredFiles() => Get(IgnoredFilesKey) ?? string.Empty;

    internal static void SetIgnoredFiles(string text) => Set(IgnoredFilesKey, text.Replace("\r\n", "\n", StringComparison.Ordinal));

    internal static string? Get(string key)
    {
        using SqliteConnection connection = Database.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM Preferences WHERE Key = $key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    internal static void Set(string key, string value)
    {
        using SqliteConnection connection = Database.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Preferences (Key, Value) VALUES ($key, $value)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }
}
