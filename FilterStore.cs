using System.Globalization;
using Microsoft.Data.Sqlite;

namespace GitSync;

/// <summary>
/// Reads and writes the folder tree of filters.
///
/// The tree logic itself (children, subtrees, paths) works on a plain list, so
/// the screens can answer from what they already loaded and the tests can run
/// it without a database.
/// </summary>
internal static class FilterStore
{
    internal static List<FilterFolder> List()
    {
        using SqliteConnection connection = Database.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT Id, ParentId, Name, SortOrder, Expanded FROM Filters ORDER BY SortOrder, Id";

        var filters = new List<FilterFolder>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            filters.Add(new FilterFolder
            {
                Id = reader.GetInt64(0),
                ParentId = reader.IsDBNull(1) ? null : reader.GetInt64(1),
                Name = reader.GetString(2),
                SortOrder = reader.GetInt32(3),
                Expanded = reader.GetInt64(4) != 0
            });
        }

        return filters;
    }

    /// <summary>Adds a folder at the end of its siblings and returns its id.</summary>
    /// <param name="transaction">The import's transaction (see <see cref="StoreCommand"/>); null for a write of its own.</param>
    internal static long Add(long? parentId, string name, SqliteTransaction? transaction = null)
    {
        using var store = new StoreCommand(transaction);
        SqliteCommand command = store.Command;

        command.CommandText = """
            INSERT INTO Filters (ParentId, Name, SortOrder, Expanded)
            VALUES ($parent, $name,
                    (SELECT COALESCE(MAX(SortOrder), -1) + 1 FROM Filters WHERE ParentId IS $parent),
                    1);
            SELECT last_insert_rowid();
            """;

        command.Parameters.AddWithValue("$parent", Database.NullableId(parentId));
        command.Parameters.AddWithValue("$name", name);

        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    internal static void Rename(long id, string name) =>
        Execute("UPDATE Filters SET Name = $value WHERE Id = $id", id, name);

    internal static void SetExpanded(long id, bool expanded) =>
        Execute("UPDATE Filters SET Expanded = $value WHERE Id = $id", id, expanded ? 1 : 0);

    /// <summary>
    /// Moves a folder (with everything under it) to the end of another
    /// folder's children, or to the root when <paramref name="newParentId"/> is null.
    /// </summary>
    internal static void MoveTo(long id, long? newParentId)
    {
        if (newParentId is { } target && SubtreeIds(id, List()).Contains(target))
        {
            // A folder inside its own subfolder would cut the branch off the tree.
            throw new InvalidOperationException("A folder cannot be moved into itself or one of its subfolders.");
        }

        using SqliteConnection connection = Database.Open();
        using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            UPDATE Filters
               SET SortOrder = (SELECT COALESCE(MAX(SortOrder), -1) + 1 FROM Filters
                                 WHERE ParentId IS $parent AND Id <> $id),
                   ParentId  = $parent
             WHERE Id = $id
            """;

        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$parent", Database.NullableId(newParentId));
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Moves a folder one place up (-1) or down (+1) among its siblings.
    /// The siblings are renumbered 0..n on the way, so gaps and ties left by
    /// earlier moves never make the order ambiguous.
    /// </summary>
    internal static void Shift(long id, int direction)
    {
        List<FilterFolder> all = List();
        FilterFolder? folder = all.Find(f => f.Id == id);

        if (folder is null)
        {
            return;
        }

        List<FilterFolder> siblings = Children(folder.ParentId, all);
        int index = siblings.FindIndex(f => f.Id == id);
        int target = index + direction;

        if (target < 0 || target >= siblings.Count)
        {
            return;
        }

        (siblings[index], siblings[target]) = (siblings[target], siblings[index]);

        using SqliteConnection connection = Database.Open();
        using SqliteTransaction transaction = connection.BeginTransaction();

        for (int order = 0; order < siblings.Count; order++)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE Filters SET SortOrder = $order WHERE Id = $id";
            command.Parameters.AddWithValue("$order", order);
            command.Parameters.AddWithValue("$id", siblings[order].Id);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>Deletes the folder and its subfolders (cascade). The caller checks for repos first.</summary>
    internal static void Delete(long id)
    {
        using SqliteConnection connection = Database.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Filters WHERE Id = $id";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>How many repos sit in any of these folders.</summary>
    internal static int CountRepos(IReadOnlyCollection<long> ids)
    {
        if (ids.Count == 0)
        {
            return 0;
        }

        using SqliteConnection connection = Database.Open();
        using SqliteCommand command = connection.CreateCommand();

        // The ids come from the database itself (long), so joining them is safe.
        string list = string.Join(",", ids.Select(i => i.ToString(CultureInfo.InvariantCulture)));
        command.CommandText = $"SELECT COUNT(*) FROM Repos WHERE FilterId IN ({list})";

        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void Execute(string sql, long id, object value)
    {
        using SqliteConnection connection = Database.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    // ---------- tree logic on a loaded list ----------

    internal static List<FilterFolder> Children(long? parentId, IReadOnlyList<FilterFolder> all) =>
        [.. all.Where(f => f.ParentId == parentId).OrderBy(f => f.SortOrder).ThenBy(f => f.Id)];

    /// <summary>
    /// The folder and every folder below it, at any depth. Selecting a folder
    /// in the main window lists the repos of this whole set, so "Games" also
    /// shows what is in Nintendo › NES.
    /// </summary>
    internal static HashSet<long> SubtreeIds(long id, IReadOnlyList<FilterFolder> all)
    {
        var result = new HashSet<long> { id };
        var pending = new Queue<long>();
        pending.Enqueue(id);

        while (pending.Count > 0)
        {
            long current = pending.Dequeue();

            foreach (FilterFolder child in all.Where(f => f.ParentId == current))
            {
                // The Add guard keeps a corrupted cycle from looping forever.
                if (result.Add(child.Id))
                {
                    pending.Enqueue(child.Id);
                }
            }
        }

        return result;
    }

    /// <summary>Names from the root down to the folder: ["Games", "Nintendo", "NES"].</summary>
    internal static List<string> PathOf(long? id, IReadOnlyList<FilterFolder> all)
    {
        var names = new List<string>();
        var byId = all.ToDictionary(f => f.Id);
        var seen = new HashSet<long>();
        long? current = id;

        while (current is { } value && byId.TryGetValue(value, out FilterFolder? folder) && seen.Add(value))
        {
            names.Insert(0, folder.Name);
            current = folder.ParentId;
        }

        return names;
    }

    /// <summary>Every folder in tree order with its full path, for the Filter drop down.</summary>
    internal static List<(FilterFolder Folder, string FullPath)> Flatten(IReadOnlyList<FilterFolder> all)
    {
        var result = new List<(FilterFolder, string)>();

        void Visit(long? parentId, string prefix, int depth)
        {
            if (depth > 64)
            {
                return;
            }

            foreach (FilterFolder child in Children(parentId, all))
            {
                string path = prefix.Length == 0 ? child.Name : prefix + "\\" + child.Name;
                result.Add((child, path));
                Visit(child.Id, path, depth + 1);
            }
        }

        Visit(null, string.Empty, 0);
        return result;
    }

    /// <summary>
    /// Why a name cannot be used, or null when it can. Folder names become real
    /// folders under Apps, so they follow Windows' rules, and two siblings with
    /// the same name would download into the same place.
    /// </summary>
    internal static string? NameProblem(string name, long? parentId, long? selfId, IReadOnlyList<FilterFolder> all)
    {
        string trimmed = name.Trim();

        if (trimmed.Length == 0)
        {
            return "Enter a name.";
        }

        if (!DownloadPaths.IsValidName(trimmed))
        {
            return "A folder name cannot contain \\ / : * ? \" < > | or end with a dot, and cannot be a reserved name like CON.";
        }

        bool taken = all.Any(f =>
            f.ParentId == parentId && f.Id != selfId &&
            string.Equals(f.Name, trimmed, StringComparison.OrdinalIgnoreCase));

        return taken ? $"There is already a folder named \"{trimmed}\" here." : null;
    }

    /// <summary>"New Folder", then "New Folder 2", "New Folder 3"... among the siblings.</summary>
    internal static string UniqueName(string baseName, long? parentId, IReadOnlyList<FilterFolder> all)
    {
        string name = baseName;

        for (int n = 2; NameProblem(name, parentId, null, all) is not null; n++)
        {
            name = $"{baseName} {n}";
        }

        return name;
    }
}
