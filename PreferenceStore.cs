using Microsoft.Data.Sqlite;

namespace GitSync;

/// <summary>
/// Small screen choices remembered between runs (the list order), kept in the
/// database beside everything else so they travel with the folder.
/// </summary>
internal static class PreferenceStore
{
    internal const string OrderKey = "ListOrder";

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
