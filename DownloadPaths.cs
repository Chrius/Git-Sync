using System.Text;
using System.Text.RegularExpressions;

namespace GitSync;

/// <summary>The version label of a saved file's name.</summary>
/// <param name="Text">All of it: "nightly, 2".</param>
/// <param name="Tag">The version: "nightly".</param>
/// <param name="Counter">The copy number of an earlier build kept under the same tag, or null.</param>
internal sealed record FileLabel(string Text, string Tag, int? Counter);

/// <summary>
/// Where downloads go, which names are safe on disk, and which old files a
/// new version replaces.
/// </summary>
internal static partial class DownloadPaths
{
    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

    /// <summary>Device names Windows refuses as file or folder names, with or without an extension.</summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>
    /// A name that can be used as one folder or file on Windows. Invalid
    /// characters become "_" — including "\" and "/", so a name can never turn
    /// into a path.
    /// </summary>
    internal static string SafeName(string name)
    {
        var builder = new StringBuilder(name.Length);

        foreach (char c in name.Trim())
        {
            builder.Append(Array.IndexOf(InvalidChars, c) >= 0 ? '_' : c);
        }

        // Windows drops trailing dots and spaces without a word, so "Games." and
        // "Games" would silently be the same folder (and ".." the parent one).
        string clean = builder.ToString().TrimEnd('.', ' ');

        if (clean.Length == 0)
        {
            return "_";
        }

        string stem = clean.Split('.')[0];
        return ReservedNames.Contains(stem) ? "_" + clean : clean;
    }

    /// <summary>A character Windows refuses in a file or folder name (\ / : * ? " &lt; &gt; | and control characters).</summary>
    internal static bool IsInvalidChar(char c) => Array.IndexOf(InvalidChars, c) >= 0;

    /// <summary>
    /// The text without the characters a folder name cannot have — dropped,
    /// not replaced, so "Zelda 2: The Adventure of Link" reads "Zelda 2 The
    /// Adventure of Link". Spaces and dots are left alone: this runs while the
    /// user types, and a space typed at the end must survive until the next word.
    /// </summary>
    internal static string RemoveInvalidChars(string text)
    {
        if (!text.Any(IsInvalidChar))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);

        foreach (char c in text)
        {
            if (!IsInvalidChar(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex RepeatedSpaces();

    /// <summary>
    /// Free text made usable inside a folder name: invalid characters dropped
    /// and the double spaces they leave ("A : B") merged. Trailing dots stay —
    /// "Wait... - x" is fine; only the end of the whole name matters.
    /// </summary>
    internal static string CleanText(string text) =>
        RepeatedSpaces().Replace(RemoveInvalidChars(text), " ").Trim();

    /// <summary>
    /// A finished folder name: <see cref="CleanText"/> plus the trailing dots
    /// and spaces Windows would silently cut. Can come out empty.
    /// </summary>
    internal static string CleanName(string text) => CleanText(text).TrimEnd('.', ' ');

    /// <summary>True when the name would be used exactly as typed.</summary>
    internal static bool IsValidName(string name)
    {
        string trimmed = name.Trim();
        return trimmed.Length > 0 && SafeName(trimmed) == trimmed;
    }

    /// <summary>
    /// The name a downloaded file is saved under. The server's name is never
    /// trusted as a path: "..\..\x.exe" is reduced to "x.exe" before anything
    /// else, so a release cannot write outside its folder.
    /// </summary>
    internal static string SafeFileName(string serverName) => SafeName(Path.GetFileName(serverName.Replace('\\', '/')));

    /// <summary>
    /// &lt;download folder&gt;\&lt;filter path&gt;\&lt;repo folder&gt;, absolute — the repo
    /// folder may hold subfolders ("Emulators\Ky"). With "Custom folder" the
    /// Folder is a full path of its own, anywhere, and is used as it is.
    /// </summary>
    internal static string RepoFolder(Repo repo, IReadOnlyList<FilterFolder> filters)
    {
        if (IsCustomFolder(repo.Folder))
        {
            return FullFolder(repo.Folder);
        }

        var parts = new List<string> { DownloadRoot.Current };
        parts.AddRange(FilterStore.PathOf(repo.FilterId, filters).Select(SafeName));
        parts.AddRange(repo.Folder.Split(FolderSeparators).Select(SafeName));
        return Path.Combine([.. parts]);
    }

    /// <summary>&lt;download folder&gt;\&lt;filter path&gt;: where the folders of a filter's repos start.</summary>
    internal static string FilterBase(long? filterId, IReadOnlyList<FilterFolder> filters) =>
        Path.Combine([DownloadRoot.Current, .. FilterStore.PathOf(filterId, filters).Select(SafeName)]);

    private static readonly char[] FolderSeparators = ['\\', '/'];

    /// <summary>A repo with "Custom folder": its Folder is a full path ("D:\Emulators\Ky").</summary>
    internal static bool IsCustomFolder(string folder) => Path.IsPathFullyQualified(folder.Trim());

    /// <summary>A full path as saved and compared: no trailing \, except the one of a drive ("D:\").</summary>
    internal static string FullFolder(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));

    /// <summary>
    /// The Folder field without "Custom folder": a folder name, or names
    /// separated by \ or / for subfolders, each one a name Windows takes.
    /// Nothing empty, no "..", no drive.
    /// </summary>
    internal static bool IsValidFolderPath(string folder)
    {
        string[] parts = folder.Trim().Split(FolderSeparators);
        return parts.All(IsValidName);
    }

    /// <summary>The Folder as saved: "Emulators / Ky" becomes "Emulators\Ky"; a full path is made whole.</summary>
    internal static string NormalizeFolder(string folder) =>
        IsCustomFolder(folder)
            ? FullFolder(folder)
            : string.Join('\\', folder.Trim().Split(FolderSeparators).Select(p => p.Trim()));

    /// <summary>
    /// Why a full path cannot be a repo's own folder, or null when it can.
    /// Its downloads, and with "Move previous versions to a different folder"
    /// everything else in it, are handled as the repo's: a whole drive, the
    /// folder that holds Git-Sync, or one that holds the download folder would
    /// put far more than the repo's files there.
    /// </summary>
    internal static string? CustomFolderProblem(string path)
    {
        if (!IsCustomFolder(path))
        {
            return "Enter a full path, like D:\\Emulators\\Ky, or choose the folder.";
        }

        string full;

        try
        {
            full = FullFolder(path);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "That is not a folder path Windows takes.";
        }

        string root = Path.GetPathRoot(full) ?? string.Empty;

        if (string.Equals(Path.TrimEndingDirectorySeparator(root), full, StringComparison.OrdinalIgnoreCase) || full.Length <= root.Length)
        {
            return "Choose a folder, not a whole drive.";
        }

        if (!full[root.Length..].Split(Path.DirectorySeparatorChar).All(IsValidName))
        {
            return "A folder name in that path is not one Windows takes: \\ / : * ? \" < > | or a name ending in a dot.";
        }

        string own = FullFolder(AppPaths.BaseDir);

        if (string.Equals(full, own, StringComparison.OrdinalIgnoreCase) || DownloadRoot.IsInside(own, full))
        {
            return "That folder holds Git-Sync itself. Choose another one.";
        }

        string downloads = FullFolder(DownloadRoot.Current);

        if (string.Equals(full, downloads, StringComparison.OrdinalIgnoreCase) || DownloadRoot.IsInside(downloads, full))
        {
            return "That folder holds the whole download folder. Choose one for this repository alone.";
        }

        return null;
    }

    /// <summary>
    /// Another repository whose folder is inside <paramref name="folder"/>, or
    /// holds it. Two such repos would mix their files: moving one folder, or
    /// its Old, would carry the other one's along.
    /// </summary>
    internal static Repo? OverlappingRepo(string folder, IEnumerable<Repo> repos, long exceptId, IReadOnlyList<FilterFolder> filters)
    {
        string mine = FullFolder(folder);

        return repos.FirstOrDefault(r =>
        {
            if (r.Id == exceptId)
            {
                return false;
            }

            string theirs = FullFolder(RepoFolder(r, filters));
            return DownloadRoot.IsInside(theirs, mine) || DownloadRoot.IsInside(mine, theirs);
        });
    }

    /// <summary>
    /// &lt;download folder&gt;\&lt;path of the parent&gt;\&lt;name&gt;, absolute: a
    /// repo's folder inside its filter, or a folder of the tree itself. The
    /// download folder is Apps, or the custom one (<see cref="DownloadRoot"/>).
    /// </summary>
    internal static string TreeFolder(long? parentId, string name, IReadOnlyList<FilterFolder> filters)
    {
        var parts = new List<string> { DownloadRoot.Current };
        parts.AddRange(FilterStore.PathOf(parentId, filters).Select(SafeName));
        parts.Add(SafeName(name));
        return Path.Combine([.. parts]);
    }

    /// <summary>
    /// Saved relative to the exe, so a moved or copied Git-Sync folder still
    /// knows which files are its own. A file outside Git-Sync's folder (a
    /// custom download folder) is saved whole: as "..\..\Downloads\x" it
    /// would point somewhere else as soon as Git-Sync's folder moved.
    /// </summary>
    internal static string ToRelative(string fullPath)
    {
        string full = Path.GetFullPath(fullPath);
        string own = Path.GetFullPath(AppPaths.BaseDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        return full.StartsWith(own, StringComparison.OrdinalIgnoreCase) ? Path.GetRelativePath(AppPaths.BaseDir, full) : full;
    }

    internal static string ToAbsolute(string relativePath) =>
        Path.GetFullPath(Path.Combine(AppPaths.BaseDir, relativePath));

    /// <summary>
    /// Deletion is only ever allowed inside the download folder in use (Apps,
    /// or the custom one). The file list comes from the database, and a
    /// damaged or hand-edited row must not be able to point a delete at
    /// anything else on the disk.
    /// </summary>
    internal static bool IsInsideDownloads(string fullPath) =>
        DownloadRoot.IsInside(Path.GetFullPath(fullPath), Path.GetFullPath(DownloadRoot.Current));

    /// <summary>
    /// Files of the previous version to delete after a new one is saved: all of
    /// them except those the new version wrote over at the same path.
    /// </summary>
    internal static List<string> FilesToDelete(IEnumerable<string> previous, IEnumerable<string> next)
    {
        var kept = new HashSet<string>(next.Select(NormalizeSeparators), StringComparer.OrdinalIgnoreCase);

        return [.. previous
            .Select(NormalizeSeparators)
            .Where(p => !kept.Contains(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static string NormalizeSeparators(string path) => path.Replace('/', '\\');

    /// <summary>Extensions made of two parts, which the version must not split.</summary>
    private static readonly string[] DoubleExtensions = [".tar.gz", ".tar.xz", ".tar.bz2", ".tar.zst"];

    internal static (string Stem, string Extension) SplitExtension(string name)
    {
        string extension = DoubleExtensions.FirstOrDefault(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase))
            ?? Path.GetExtension(name);

        return (name[..^extension.Length], extension);
    }

    /// <summary>" (build-212)": how the version appears at the end of a saved file's name.</summary>
    private static string Label(string versionTag) => $" ({SafeName(versionTag)})";

    /// <summary>
    /// The name a file is saved under: its own name with the version at the
    /// end, "eglantine-windows-x86_64.zip" of build-212 becoming
    /// "eglantine-windows-x86_64 (build-212).zip". Many projects reuse the same
    /// names on every release, and automated builds often differ only by a
    /// hash; with the version in the name the user can tell which file is
    /// which build. "app.tar.gz" becomes "app (v1).tar.gz".
    /// </summary>
    internal static string LabeledName(string fileName, string versionTag)
    {
        if (versionTag.Trim().Length == 0)
        {
            return fileName;
        }

        (string stem, string extension) = SplitExtension(fileName);
        string label = Label(versionTag);

        if (stem.EndsWith(label, StringComparison.OrdinalIgnoreCase))
        {
            return fileName;
        }

        // A name that is only an extension (".env") gets no leading space.
        return stem.Length == 0 ? $"{label.TrimStart()}{extension}" : $"{stem}{label}{extension}";
    }

    /// <summary>
    /// The version label at the end of a saved file's name, as
    /// <see cref="LabeledName"/> and <see cref="KeptName"/> write it:
    /// "app (v1.2).zip" → v1.2; "app (nightly, 2).zip" → nightly, copy 2.
    /// Null for a name without one.
    /// </summary>
    internal static FileLabel? ReadLabel(string fileName)
    {
        (string stem, _) = SplitExtension(fileName);

        // "app (v1.2.3)" has no extension, but to Path.GetExtension it ends in ".3)".
        if (!stem.EndsWith(')') && fileName.EndsWith(')'))
        {
            stem = fileName;
        }

        int open = stem.LastIndexOf(" (", StringComparison.Ordinal);

        if (!stem.EndsWith(')') || open < 0)
        {
            return null;
        }

        string text = stem[(open + 2)..^1];

        if (text.Length == 0 || text.Contains('(') || text.Contains(')'))
        {
            return null;
        }

        int comma = text.LastIndexOf(", ", StringComparison.Ordinal);

        return comma > 0 && int.TryParse(text[(comma + 2)..], System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture, out int copy)
            ? new FileLabel(text, text[..comma], copy)
            : new FileLabel(text, text, null);
    }

    /// <summary>
    /// Free name for an old file that would be overwritten under "Keep
    /// previous versions". With the version in every name that only happens
    /// when a release is published again under the same tag (a "nightly"),
    /// so "app (nightly).zip" becomes "app (nightly, 2).zip". A file from
    /// before the version was added to names, "app-win64.zip", becomes
    /// "app-win64 (v1.3.0).zip".
    /// </summary>
    internal static string KeptName(string fullPath, string versionTag)
    {
        string folder = Path.GetDirectoryName(fullPath) ?? string.Empty;
        (string stem, string extension) = SplitExtension(Path.GetFileName(fullPath));
        string tag = SafeName(versionTag);
        string label = Label(versionTag);

        if (stem.EndsWith(label, StringComparison.OrdinalIgnoreCase))
        {
            stem = stem[..^label.Length];
        }

        string candidate = Path.Combine(folder, $"{stem}{label}{extension}");

        for (int n = 2; File.Exists(candidate); n++)
        {
            candidate = Path.Combine(folder, $"{stem} ({tag}, {n}){extension}");
        }

        return candidate;
    }

    /// <summary>
    /// "&lt;repo&gt;-source.zip" (or ".tar.gz"). The version is not in it: like
    /// every file, it gets the version at the end when saved ("rea-source (v1.0).zip").
    /// </summary>
    internal static string SourceFileName(string repoPath, string extension = ".zip") =>
        $"{SafeName(RepoAddress.LastSegment(repoPath))}-source{extension}";

    // ---------- matching files across versions ----------

    // Commit hashes, as found in nightly build names (at least one digit, so a
    // plain word made of a-f letters is left alone).
    [GeneratedRegex(@"(?<![0-9a-z])(?=[a-f]*\d)[0-9a-f]{7,40}(?![0-9a-z])")]
    private static partial Regex HashPattern();

    // Dotted or underscored version numbers: 1.3.2, 0_9_1.
    [GeneratedRegex(@"\d+(?:[._]\d+)+")]
    private static partial Regex DottedNumberPattern();

    // Build numbers and dates. Two digits are left alone on purpose: "win32" and
    // "win64" are different files, not two versions of the same one.
    [GeneratedRegex(@"\d{3,}")]
    private static partial Regex LongNumberPattern();

    /// <summary>
    /// A file name with its version parts blanked out, so the same file of two
    /// versions has the same key: "app-v1.3.2-win64.zip" and
    /// "app-v1.4.0-win64.zip" both become "app-v#-win64.zip".
    /// </summary>
    internal static string FileKey(string fileName, string? versionTag)
    {
        // Saved files carry " (tag)" at the end; the release's own names do
        // not. Without the label both sides compare, and so do files saved
        // before the label existed.
        if (!string.IsNullOrWhiteSpace(versionTag))
        {
            fileName = fileName.Replace(Label(versionTag), string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        string key = fileName.ToLowerInvariant();
        string version = (versionTag ?? string.Empty).Trim().ToLowerInvariant();

        if (version.StartsWith('v') && version.Length > 1 && char.IsDigit(version[1]))
        {
            version = version[1..];
        }

        // Tags like "r23b" are not numbers, but the file names still carry them.
        if (version.Length >= 2)
        {
            key = key.Replace(version, "#", StringComparison.Ordinal);
        }

        key = HashPattern().Replace(key, "#");
        key = DottedNumberPattern().Replace(key, "#");
        key = LongNumberPattern().Replace(key, "#");

        return key;
    }

    /// <summary>
    /// Names of the new version's files that match one of the files saved for
    /// the current version. These come pre-checked in the file picker, so an
    /// update does not make the user choose the same files again.
    /// </summary>
    internal static HashSet<string> PreChecked(
        IEnumerable<ReleaseFile> files, string? newTag, IEnumerable<string> currentFiles, string? currentTag)
    {
        var previousKeys = new HashSet<string>(
            currentFiles.Select(f => FileKey(Path.GetFileName(f), currentTag)), StringComparer.Ordinal);

        return new HashSet<string>(
            files.Where(f => previousKeys.Contains(FileKey(SafeFileName(f.Name), newTag))).Select(f => f.Name),
            StringComparer.Ordinal);
    }
}
