namespace GitSync;

/// <summary>
/// Application paths, always relative to the folder that holds the .exe.
/// That is what keeps the delivery portable: copying the folder takes the
/// database and the downloads along.
/// </summary>
internal static class AppPaths
{
    /// <summary>Folder that contains the running executable.</summary>
    internal static string BaseDir { get; } = ResolveBaseDir();

    /// <summary>Right beside the exe, not in a Data subfolder: the user asked for it that way.</summary>
    internal static string DatabaseFile => Path.Combine(BaseDir, "Git-Sync.db");

    /// <summary>Root of every download: Apps\&lt;filter path&gt;\&lt;repo folder&gt;.</summary>
    internal static string AppsDir => Path.Combine(BaseDir, "Apps");

    /// <summary>
    /// Finds the real folder of the executable.
    /// In a single-file publish the process is extracted to a temporary folder,
    /// and AppContext.BaseDirectory may point there instead of at the .exe.
    /// Environment.ProcessPath always returns the real executable, so the
    /// database follows the .exe even when the whole set is moved.
    /// </summary>
    private static string ResolveBaseDir()
    {
        string? exe = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exe))
        {
            string? dir = Path.GetDirectoryName(exe);
            if (!string.IsNullOrEmpty(dir))
            {
                return dir;
            }
        }

        return AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    }
}
