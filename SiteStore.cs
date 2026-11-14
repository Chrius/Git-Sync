using Microsoft.Data.Sqlite;

namespace GitSync;

/// <summary>
/// Reads and writes the Sites table: the three built-in sites, plus the
/// self-hosted servers found by <see cref="SiteDetection"/> for the repos
/// that use them.
/// </summary>
internal static class SiteStore
{
    /// <summary>Always there (see Database.EnsureSites); every other site comes and goes with its repos.</summary>
    internal static readonly string[] BuiltInHosts = ["github.com", "gitlab.com", "codeberg.org"];

    /// <summary>In creation order: GitHub, GitLab, Codeberg, then detected servers.</summary>
    internal static List<Site> List()
    {
        using SqliteConnection connection = Database.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Host, ApiType, Token FROM Sites ORDER BY Id";

        var sites = new List<Site>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            sites.Add(new Site
            {
                Id = reader.GetInt64(0),
                Host = reader.GetString(1),
                Type = ParseType(reader.GetString(2)),
                Token = reader.GetString(3)
            });
        }

        return sites;
    }

    internal static ApiType ParseType(string text) =>
        Enum.TryParse(text, ignoreCase: true, out ApiType type) ? type : ApiType.GitHub;

    /// <summary>
    /// Saves a server found by detection and returns it. If the host is
    /// already there it is returned as it is, token included.
    /// </summary>
    internal static Site Add(string host, ApiType type)
    {
        using (SqliteConnection connection = Database.Open())
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "INSERT OR IGNORE INTO Sites (Host, ApiType) VALUES ($host, $type)";
            command.Parameters.AddWithValue("$host", host);
            command.Parameters.AddWithValue("$type", type.ToString());
            command.ExecuteNonQuery();
        }

        return List().First(s => string.Equals(s.Host, host, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Removes detected servers no repo uses any more — the last one was
    /// deleted or moved, or the Add window was cancelled after detection.
    /// This is what keeps Settings free of a Delete button. Built-in sites stay.
    /// </summary>
    internal static void RemoveUnused()
    {
        using SqliteConnection connection = Database.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM Sites
             WHERE Host NOT IN ($github, $gitlab, $codeberg)
               AND Id NOT IN (SELECT SiteId FROM Repos)
            """;
        command.Parameters.AddWithValue("$github", BuiltInHosts[0]);
        command.Parameters.AddWithValue("$gitlab", BuiltInHosts[1]);
        command.Parameters.AddWithValue("$codeberg", BuiltInHosts[2]);
        command.ExecuteNonQuery();
    }

    /// <summary>The token is the only thing about a site the user can change.</summary>
    internal static void SetToken(long id, string token)
    {
        using SqliteConnection connection = Database.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE Sites SET Token = $token WHERE Id = $id";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$token", token);
        command.ExecuteNonQuery();
    }
}
