using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;

namespace GitSync;

/// <summary>
/// Test bench for the logic without UI. Returns a non-zero exit code when any
/// check fails, so a delivery can be held back on it.
/// </summary>
internal static class Tests
{
    private static int _failures;
    private static int _total;

    private static int Main(string[] args)
    {
        PrepareCleanDatabase();

        TestAddressParsing();
        TestDroppedLinks();
        TestFolderSuggestion();
        TestSafeNames();
        TestDownloadFolder();
        TestFilesToDelete();
        TestCommit();
        TestFileKeys();
        TestGitHubParsing();
        TestGitLabParsing();
        TestGiteaParsing();
        TestPickLatest();
        TestSourceHead();
        TestApiUrls();
        TestTokenHosts();
        TestVersionState();
        TestDatabaseSeed();
        TestFilters();
        TestRepoStore();
        TestKeepPrevious();
        TestFolderMove();
        TestTreeFolderMove();
        TestOldFolder();
        TestVersionHistory();
        TestDownloadSequence();
        TestDownloadQueue();
        TestFileCheck();
        TestDownloadRoot();
        TestRepoFolders();
        TestSorting();
        TestListWindow();
        TestSearch();
        TestRateLimits();
        TestSiteDetection();
        TestConnector();
        TestAutoCheck();
        TestAppUpdate();
        TestDataTransfer();

        if (args.Contains("--online"))
        {
            TestOnline().GetAwaiter().GetResult();
        }

        Console.WriteLine();
        Console.WriteLine(_failures == 0
            ? $"OK: {_total} checks."
            : $"FAILED: {_failures} of {_total} checks.");

        return _failures == 0 ? 0 : 1;
    }

    // ---------- infrastructure ----------

    private static void Section(string title) => Console.WriteLine("\n== " + title);

    private static void Check(string what, object? expected, object? actual)
    {
        _total++;

        if (Equals(expected, actual))
        {
            Console.WriteLine($"  ok   {what}");
            return;
        }

        _failures++;
        Console.WriteLine($"  FAIL {what}: expected <{expected}>, got <{actual}>");
    }

    private static void Require(string what, bool condition) => Check(what, true, condition);

    /// <summary>Every run starts from an empty database and an empty Apps, or the checks depend on order.</summary>
    private static void PrepareCleanDatabase()
    {
        // The pool keeps the connection open and the file would be locked for the delete.
        SqliteConnection.ClearAllPools();

        if (File.Exists(AppPaths.DatabaseFile))
        {
            File.Delete(AppPaths.DatabaseFile);
        }

        if (Directory.Exists(AppPaths.AppsDir))
        {
            Directory.Delete(AppPaths.AppsDir, recursive: true);
        }

        Database.Prepare();
        DownloadRoot.Reload();

        Console.WriteLine("Test database: " + AppPaths.DatabaseFile);
    }

    private static readonly List<Site> SampleSites =
    [
        new Site { Id = 1, Host = "github.com", Type = ApiType.GitHub },
        new Site { Id = 2, Host = "gitlab.com", Type = ApiType.GitLab },
        new Site { Id = 3, Host = "codeberg.org", Type = ApiType.Gitea },
        new Site { Id = 4, Host = "http://192.168.0.5:3000", Type = ApiType.Gitea }
    ];

    private static Site SiteOf(string host) => SampleSites.First(s => s.Host == host);

    // ---------- addresses ----------

    private static void TestAddressParsing()
    {
        Section("Repository addresses");

        void Expect(string text, string? host, string? path)
        {
            AddressMatch? match = RepoAddress.Parse(text, SampleSites);
            Check($"{text} -> site", host, match?.Site?.Host);
            Check($"{text} -> path", path, match?.Path);
        }

        Expect("https://github.com/morluto/rea", "github.com", "morluto/rea");
        Expect("https://github.com/morluto/rea.git", "github.com", "morluto/rea");
        Expect("https://www.github.com/morluto/rea/releases/tag/v1.0", "github.com", "morluto/rea");
        Expect("github.com/morluto/rea/tree/main", "github.com", "morluto/rea");
        Expect("git@github.com:morluto/rea.git", "github.com", "morluto/rea");
        Expect("  https://GitHub.com/morluto/rea/  ", "github.com", "morluto/rea");
        Expect("https://gitlab.com/group/sub/project/-/releases", "gitlab.com", "group/sub/project");
        Expect("https://gitlab.com/group/project", "gitlab.com", "group/project");
        Expect("https://codeberg.org/forgejo/forgejo/releases", "codeberg.org", "forgejo/forgejo");
        Expect("http://192.168.0.5:3000/me/tool/releases", "http://192.168.0.5:3000", "me/tool");

        AddressMatch? unknown = RepoAddress.Parse("https://example.org/a/b", SampleSites);
        Check("unknown host -> no site", null, unknown?.Site);
        Check("unknown host -> host kept", "example.org", unknown?.Host);
        Check("unknown host -> path", "a/b", unknown?.Path);
        Check("owner only -> nothing", null, RepoAddress.Parse("https://github.com/morluto", SampleSites));

        Check("bare owner/repo is not an address", false, RepoAddress.LooksLikeAddress("morluto/rea"));
        Check("host without scheme is an address", true, RepoAddress.LooksLikeAddress("github.com/morluto/rea"));
        Check("scheme is an address", true, RepoAddress.LooksLikeAddress("https://x.org/a/b"));

        Check("normalize typed path", "morluto/rea", RepoAddress.NormalizePath(" /morluto/rea.git/ "));
        Check("valid GitHub path", true, RepoAddress.IsValidPath("morluto/rea", ApiType.GitHub));
        Check("GitHub path with three parts", false, RepoAddress.IsValidPath("a/b/c", ApiType.GitHub));
        Check("GitLab nested path", true, RepoAddress.IsValidPath("a/b/c", ApiType.GitLab));
        Check("path with a space", false, RepoAddress.IsValidPath("a b/c", ApiType.GitHub));
        Check("path with one part", false, RepoAddress.IsValidPath("rea", ApiType.Gitea));

    }

    private static void TestDroppedLinks()
    {
        Section("Links dragged onto the window");

        Check("plain link", "https://github.com/morluto/rea", RepoAddress.FirstLink("https://github.com/morluto/rea"));
        Check("UTF-16 data ends in a null", "https://github.com/morluto/rea", RepoAddress.FirstLink("https://github.com/morluto/rea\0"));
        Check("Firefox: address then title", "https://codeberg.org/forgejo/forgejo",
            RepoAddress.FirstLink("https://codeberg.org/forgejo/forgejo\nForgejo: beyond coding"));
        Check("desktop shortcut", "https://gitlab.com/gitlab-org/cli",
            RepoAddress.FirstLink("[InternetShortcut]\r\nURL=https://gitlab.com/gitlab-org/cli\r\n"));
        Check("spaces and blank lines around it", "https://github.com/a/b", RepoAddress.FirstLink("  \r\n  https://github.com/a/b  \r\n"));
        Check("link without a scheme", "github.com/a/b/releases", RepoAddress.FirstLink("github.com/a/b/releases"));
        Check("a sentence is not a link", null, RepoAddress.FirstLink("see https://github.com/a/b for details"));
        Check("plain words", null, RepoAddress.FirstLink("Reverse Engineer Anything"));
        Check("nothing", null, RepoAddress.FirstLink(""));

        // What the drop handler does with it: site, path and the folder the window starts with.
        AddressMatch? match = RepoAddress.Parse(
            RepoAddress.FirstLink("https://github.com/morluto/rea/releases/tag/rea-agents-6.9.0\0")!, SampleSites);
        Check("dropped release page -> site", "github.com", match?.Site?.Host);
        Check("dropped release page -> path", "morluto/rea", match?.Path);
        Check("folder before a name is typed", "rea", RepoAddress.SuggestFolder("", match?.Path ?? ""));
    }

    private static void TestFolderSuggestion()
    {
        Section("Folder suggestion");

        Check("name + repo", "Reverse Engineer Anything - rea",
            RepoAddress.SuggestFolder("Reverse Engineer Anything", "morluto/rea"));
        Check("no name yet", "rea", RepoAddress.SuggestFolder("  ", "morluto/rea"));
        Check("no repo yet", "Reverse Engineer Anything", RepoAddress.SuggestFolder("Reverse Engineer Anything", ""));
        Check("GitLab nested", "My Tool - project", RepoAddress.SuggestFolder("My Tool", "group/sub/project"));

        // The name is free text; the folder must be a valid Windows name.
        Check("colon left out", "Zelda 2 The Adventure of Link - z2rs",
            RepoAddress.SuggestFolder("Zelda 2: The Adventure of Link", "someone/z2rs"));
        Check("every invalid character left out", "What Why ACDC - x",
            RepoAddress.SuggestFolder("What? Why* <AC|DC>", "o/x"));
        Check("slash does not make a subfolder", "ACDC - x", RepoAddress.SuggestFolder("AC/DC", "o/x"));
        Check("space left behind is merged", "A B - x", RepoAddress.SuggestFolder("A : B", "o/x"));
        Check("trailing dots of the name kept inside", "Wait... - x", RepoAddress.SuggestFolder("Wait...", "o/x"));
        Check("trailing dots cut at the very end", "Wait", RepoAddress.SuggestFolder("Wait...", ""));
        Check("name of only invalid characters", "x", RepoAddress.SuggestFolder("<>:", "o/x"));
        Check("repo ending in a dot", "Name - repo", RepoAddress.SuggestFolder("Name", "o/repo."));
        Require("every suggestion is a valid folder name",
            new[] { "Zelda 2: The Adventure of Link", "What? Why*", "A : B", "Wait...", "\"Quoted\"", "Tab\there" }
                .All(n => DownloadPaths.IsValidName(RepoAddress.SuggestFolder(n, "o/x"))));

        Check("typing: invalid characters dropped", "Zelda 2 The", DownloadPaths.RemoveInvalidChars("Zelda 2: The"));
        Check("typing: a space at the end survives", "Zelda ", DownloadPaths.RemoveInvalidChars("Zelda "));
        Check("typing: nothing to drop", "Plain - name", DownloadPaths.RemoveInvalidChars("Plain - name"));
    }

    // ---------- paths ----------

    private static void TestSafeNames()
    {
        Section("Safe names");

        Check("plain", "Games", DownloadPaths.SafeName("Games"));
        Check("invalid char", "a_b", DownloadPaths.SafeName("a:b"));
        Check("slash cannot make a path", "a_b", DownloadPaths.SafeName("a/b"));
        Check("trailing dot", "Games", DownloadPaths.SafeName("Games."));
        Check("dot dot", "_", DownloadPaths.SafeName(".."));
        Check("reserved", "_CON", DownloadPaths.SafeName("CON"));
        Check("reserved with extension", "_con.txt", DownloadPaths.SafeName("con.txt"));
        Check("server name with ..\\", "x.exe", DownloadPaths.SafeFileName(@"..\..\x.exe"));
        Check("server name with ../", "evil.exe", DownloadPaths.SafeFileName("../evil.exe"));
        Check("server name with folders", "file.zip", DownloadPaths.SafeFileName("dir/sub/file.zip"));
        Check("valid name", true, DownloadPaths.IsValidName("NES"));
        Check("name with slash is invalid", false, DownloadPaths.IsValidName("A/B"));
        Check("name with trailing space is trimmed", true, DownloadPaths.IsValidName("NES "));
        Check("source file name", "rea-source.zip", DownloadPaths.SourceFileName("morluto/rea"));

        // Every saved file gets its version at the end of the name.
        Check("label", "eglantine-windows-x86_64 (build-212).zip",
            DownloadPaths.LabeledName("eglantine-windows-x86_64.zip", "build-212"));
        Check("label on a source zip", "rea-source (rea-agents-6.9.0).zip",
            DownloadPaths.LabeledName(DownloadPaths.SourceFileName("morluto/rea"), "rea-agents-6.9.0"));
        Check("label before a double extension", "app (v1.2).tar.gz", DownloadPaths.LabeledName("app.tar.gz", "v1.2"));
        Check("label on a name without extension", "README (v1)", DownloadPaths.LabeledName("README", "v1"));
        Check("label with a slash in the tag", "app (release_1.0).zip", DownloadPaths.LabeledName("app.zip", "release/1.0"));
        Check("label on a hash-only build", "eglantine-3f9c2ab-windows (build-213).zip",
            DownloadPaths.LabeledName("eglantine-3f9c2ab-windows.zip", "build-213"));
        Check("label not added twice", "app (v1).zip", DownloadPaths.LabeledName("app (v1).zip", "v1"));
        Check("no tag, no label", "app.zip", DownloadPaths.LabeledName("app.zip", " "));
        Check("name that is only an extension", "(v1).env", DownloadPaths.LabeledName(".env", "v1"));
    }

    private static readonly List<FilterFolder> SampleTree =
    [
        new FilterFolder { Id = 1, ParentId = null, Name = "Games", SortOrder = 0 },
        new FilterFolder { Id = 2, ParentId = 1, Name = "Nintendo", SortOrder = 0 },
        new FilterFolder { Id = 3, ParentId = 2, Name = "NES", SortOrder = 0 },
        new FilterFolder { Id = 4, ParentId = 2, Name = "SNES", SortOrder = 1 },
        new FilterFolder { Id = 5, ParentId = 1, Name = "Sony", SortOrder = 1 },
        new FilterFolder { Id = 6, ParentId = null, Name = "Emulators", SortOrder = 1 }
    ];

    private static void TestDownloadFolder()
    {
        Section("Download folder");

        var repo = new Repo { Folder = "Reverse Engineer Anything - rea", FilterId = 3 };
        Check("full filter path + folder",
            Path.Combine(AppPaths.AppsDir, "Games", "Nintendo", "NES", "Reverse Engineer Anything - rea"),
            DownloadPaths.RepoFolder(repo, SampleTree));

        repo.FilterId = null;
        Check("no filter", Path.Combine(AppPaths.AppsDir, "Reverse Engineer Anything - rea"),
            DownloadPaths.RepoFolder(repo, SampleTree));

        repo.FilterId = 6;
        repo.Folder = "..";
        Check("folder cannot climb out", Path.Combine(AppPaths.AppsDir, "Emulators", "_"),
            DownloadPaths.RepoFolder(repo, SampleTree));

        Check("relative path", @"Apps\Games\x.zip",
            DownloadPaths.ToRelative(Path.Combine(AppPaths.AppsDir, "Games", "x.zip")));
        Check("inside Apps", true, DownloadPaths.IsInsideDownloads(DownloadPaths.ToAbsolute(@"Apps\Games\x.zip")));
        Check("next to the exe is outside Apps", false, DownloadPaths.IsInsideDownloads(DownloadPaths.ToAbsolute("Git-Sync.db")));
        Check("climbing out of Apps", false, DownloadPaths.IsInsideDownloads(DownloadPaths.ToAbsolute(@"Apps\..\Git-Sync.db")));
        Check("above the exe", false, DownloadPaths.IsInsideDownloads(DownloadPaths.ToAbsolute(@"..\outside.txt")));
        Check("Apps itself", false, DownloadPaths.IsInsideDownloads(AppPaths.AppsDir));
    }

    private static void TestFilesToDelete()
    {
        Section("Files replaced by a new version");

        List<string> delete = DownloadPaths.FilesToDelete(
            [@"Apps\A\app-1.zip", @"Apps\A\README.txt"],
            [@"apps\a\readme.txt", @"Apps\A\app-2.zip"]);

        Check("old file goes", 1, delete.Count);
        Check("which one", @"Apps\A\app-1.zip", delete.FirstOrDefault());

        List<string> moved = DownloadPaths.FilesToDelete([@"Apps\Old\app-1.zip"], [@"Apps\New\app-2.zip"]);
        Check("old file in a folder renamed by Edit is still found", @"Apps\Old\app-1.zip", moved.FirstOrDefault());

        Check("nothing before", 0, DownloadPaths.FilesToDelete([], [@"Apps\A\x"]).Count);
    }

    private static void TestCommit()
    {
        Section("Replacing a version on disk");

        string folder = Path.Combine(AppPaths.AppsDir, "CommitTest");
        Directory.CreateDirectory(folder);

        string Write(string name, string content)
        {
            string path = Path.Combine(folder, name);
            File.WriteAllText(path, content);
            return path;
        }

        string oldApp = Write("app-1.zip", "old app");
        string same = Write("same.bin", "old");
        string unrelated = Write("notes.txt", "mine");
        string locked = Write("locked.zip", "in use");
        string outside = Path.Combine(AppPaths.BaseDir, "outside-test.txt");
        File.WriteAllText(outside, "must survive");

        string newPart = Write("app-2.zip.part", "new app");
        string samePart = Write("same.bin.part", "new");

        List<string> previous =
        [
            DownloadPaths.ToRelative(oldApp),
            DownloadPaths.ToRelative(same),
            DownloadPaths.ToRelative(locked),
            "outside-test.txt"
        ];

        List<string> warnings;

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            warnings = Downloader.Commit(
                [(newPart, Path.Combine(folder, "app-2.zip")), (samePart, same)], previous).Warnings;
        }

        Check("old version's file deleted", false, File.Exists(oldApp));
        Check("new file in place", "new app", File.ReadAllText(Path.Combine(folder, "app-2.zip")));
        Check("same name overwritten", "new", File.ReadAllText(same));
        Check("unrelated file untouched", true, File.Exists(unrelated));
        Check("file outside Apps never deleted", true, File.Exists(outside));
        Check("no .part left", 0, Directory.GetFiles(folder, "*.part").Length);
        Check("file in use reported", 1, warnings.Count);
        Check("file in use kept", true, File.Exists(locked));

        File.Delete(outside);
    }

    private static void TestFileKeys()
    {
        Section("Same file across versions");

        HashSet<string> chosen = DownloadPaths.PreChecked(
            [
                new ReleaseFile("app-v1.4.0-win64.zip", 1, "u"),
                new ReleaseFile("app-v1.4.0-win32.zip", 1, "u"),
                new ReleaseFile("app-v1.4.0-linux.tar.gz", 1, "u")
            ],
            "v1.4.0", [@"Apps\X\app-v1.3.2-win64.zip"], "v1.3.2");

        Check("one file pre-checked", 1, chosen.Count);
        Check("the win64 one", true, chosen.Contains("app-v1.4.0-win64.zip"));

        Check("nightly build number and hash",
            DownloadPaths.FileKey("rpcs3-v0.0.38-17200-d4e5f6a7_win64.7z", "nightly"),
            DownloadPaths.FileKey("rpcs3-v0.0.38-17250-a1b2c3d4_win64.7z", "nightly"));
        Check("tag that is not a number",
            DownloadPaths.FileKey("ndk-r23b-windows.zip", "r23b"),
            DownloadPaths.FileKey("ndk-r24-windows.zip", "r24"));
        Check("source archives",
            DownloadPaths.FileKey("rea-source (0123456).zip", "0123456"),
            DownloadPaths.FileKey("rea-source.zip", "89abcde"));

        // Saved names carry the label, the release's own names do not.
        Check("saved name matches the next release's file",
            DownloadPaths.FileKey("eglantine-windows-x86_64.zip", "build-213"),
            DownloadPaths.FileKey("eglantine-windows-x86_64 (build-212).zip", "build-212"));
        Check("hash-only builds match",
            DownloadPaths.FileKey("eglantine-9d81e04-windows.zip", "build-213"),
            DownloadPaths.FileKey("eglantine-3f9c2ab-windows (build-212).zip", "build-212"));

        HashSet<string> relabeled = DownloadPaths.PreChecked(
            [new ReleaseFile("eglantine-windows-x86_64.zip", 1, "u"), new ReleaseFile("eglantine-linux-x86_64.zip", 1, "u")],
            "build-213", [@"Apps\E\eglantine-windows-x86_64 (build-212).zip"], "build-212");
        Check("pre-check with a labeled saved file", "eglantine-windows-x86_64.zip", string.Join(",", relabeled));

        HashSet<string> older = DownloadPaths.PreChecked(
            [new ReleaseFile("app-v1.4.0-win64.zip", 1, "u")], "v1.4.0", [@"Apps\X\app-v1.3.2-win64.zip"], "v1.3.2");
        Check("pre-check with a file saved before labels existed", 1, older.Count);
        Require("win32 and win64 differ",
            DownloadPaths.FileKey("app-win32.zip", "v1") != DownloadPaths.FileKey("app-win64.zip", "v1"));
    }

    // ---------- release JSON ----------

    private const string GitHubList = """
        [
          {"tag_name":"v2.0.0-rc1","name":"RC","draft":false,"prerelease":true,
           "published_at":"2026-11-10T10:00:00Z","html_url":"https://github.com/o/r/releases/tag/v2.0.0-rc1",
           "zipball_url":"https://api.github.com/repos/o/r/zipball/v2.0.0-rc1",
           "assets":[{"name":"app-v2.0.0-rc1-win64.zip","size":100,
                      "url":"https://api.github.com/repos/o/r/releases/assets/3",
                      "browser_download_url":"https://github.com/o/r/releases/download/v2.0.0-rc1/app-v2.0.0-rc1-win64.zip"}]},
          {"tag_name":"v1.9.0","name":"Draft","draft":true,"prerelease":false,
           "published_at":null,"created_at":"2026-11-12T00:00:00Z","assets":[]},
          {"tag_name":"v1.4.0","name":"1.4","draft":false,"prerelease":false,
           "published_at":"2026-11-01T12:00:00Z","html_url":"https://github.com/o/r/releases/tag/v1.4.0",
           "zipball_url":"https://api.github.com/repos/o/r/zipball/v1.4.0",
           "tarball_url":"https://api.github.com/repos/o/r/tarball/v1.4.0",
           "assets":[{"name":"app-v1.4.0-win64.zip","size":2048,
                      "url":"https://api.github.com/repos/o/r/releases/assets/1",
                      "browser_download_url":"https://github.com/o/r/releases/download/v1.4.0/app-v1.4.0-win64.zip"},
                     {"name":"app-v1.4.0-linux.tar.gz","size":1024,
                      "url":"https://api.github.com/repos/o/r/releases/assets/2",
                      "browser_download_url":"https://github.com/o/r/releases/download/v1.4.0/app-v1.4.0-linux.tar.gz"}]},
          {"tag_name":"v1.3.0","name":"Source only","draft":false,"prerelease":false,
           "published_at":"2026-10-01T12:00:00Z","zipball_url":"https://api.github.com/repos/o/r/zipball/v1.3.0",
           "tarball_url":"https://api.github.com/repos/o/r/tarball/v1.3.0",
           "assets":[]}
        ]
        """;

    private static void TestGitHubParsing()
    {
        Section("GitHub releases");

        List<Release> list = ReleaseParser.ParseList(ApiType.GitHub, GitHubList, "o/r");
        Check("count", 4, list.Count);

        Release v14 = list[2];
        Check("tag", "v1.4.0", v14.Tag);
        Check("date", new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero), v14.Date);
        Check("files", 2, v14.Files.Count);
        Check("public address", "https://github.com/o/r/releases/download/v1.4.0/app-v1.4.0-win64.zip", v14.Files[0].Url);
        Check("API address", "https://api.github.com/repos/o/r/releases/assets/1", v14.Files[0].ApiUrl);
        Check("size", 2048L, v14.Files[0].Size);
        Check("kind", VersionKind.Release, v14.Kind);

        Check("draft flag", true, list[1].Draft);
        Check("draft date falls back to created_at", new DateTimeOffset(2026, 11, 12, 0, 0, 0, TimeSpan.Zero), list[1].Date);

        Release source = list[3];
        Check("no files -> source", VersionKind.ReleaseSource, source.Kind);
        Check("source file name", "r-source.zip", source.Files.Single().Name);
        Check("source address", "https://api.github.com/repos/o/r/zipball/v1.3.0", source.Files.Single().Url);
        Check("source file is marked as source", true, source.Files.Single().IsSource);

        // ----- "Show source code when downloading" -----

        Check("release files are not source", false, v14.Files.Any(f => f.IsSource));
        Check("archives of a release with files", "r-source.zip|r-source.tar.gz", string.Join("|", v14.Sources.Select(f => f.Name)));
        Check("tar.gz address", "https://api.github.com/repos/o/r/tarball/v1.4.0", v14.Sources[1].Url);
        Check("archives have no size", -1L, v14.Sources[1].Size);
        Check("setting off: only the release files", "app-v1.4.0-win64.zip|app-v1.4.0-linux.tar.gz",
            string.Join("|", v14.Offered(false).Select(f => f.Name)));
        Check("setting on: release files, then zip and tar.gz",
            "app-v1.4.0-win64.zip|app-v1.4.0-linux.tar.gz|r-source.zip|r-source.tar.gz",
            string.Join("|", v14.Offered(true).Select(f => f.Name)));
        Check("setting off, no files: the zip alone", "r-source.zip", string.Join("|", source.Offered(false).Select(f => f.Name)));
        Check("setting on, no files: zip and tar.gz, once each", "r-source.zip|r-source.tar.gz",
            string.Join("|", source.Offered(true).Select(f => f.Name)));
        Check("pre-release without tarball_url: zip only", "r-source.zip", string.Join("|", list[0].Offered(true).Where(f => f.IsSource).Select(f => f.Name)));
    }

    private static void TestGitLabParsing()
    {
        Section("GitLab releases");

        const string json = """
            [
              {"tag_name":"v3.1","name":"3.1","released_at":"2026-11-05T08:00:00.000Z","upcoming_release":false,
               "_links":{"self":"https://gitlab.com/g/s/p/-/releases/v3.1"},
               "assets":{"count":5,
                 "sources":[{"format":"zip","url":"https://gitlab.com/g/s/p/-/archive/v3.1/p-v3.1.zip"},
                            {"format":"tar.gz","url":"https://gitlab.com/g/s/p/-/archive/v3.1/p-v3.1.tar.gz"},
                            {"format":"tar.bz2","url":"https://gitlab.com/g/s/p/-/archive/v3.1/p-v3.1.tar.bz2"},
                            {"format":"tar","url":"https://gitlab.com/g/s/p/-/archive/v3.1/p-v3.1.tar"}],
                 "links":[{"id":1,"name":"p-win.exe","url":"https://example.com/page",
                           "direct_asset_url":"https://gitlab.com/g/s/p/-/releases/v3.1/downloads/p-win.exe",
                           "link_type":"package"}]}},
              {"tag_name":"v3.0","name":"3.0","released_at":"2026-10-05T08:00:00.000Z","upcoming_release":false,
               "_links":{"self":"https://gitlab.com/g/s/p/-/releases/v3.0"},
               "assets":{"count":1,
                 "sources":[{"format":"zip","url":"https://gitlab.com/g/s/p/-/archive/v3.0/p-v3.0.zip"}],
                 "links":[]}}
            ]
            """;

        List<Release> list = ReleaseParser.ParseList(ApiType.GitLab, json, "g/s/p");
        Check("count", 2, list.Count);
        Check("link is the file", "p-win.exe", list[0].Files.Single().Name);
        Check("direct asset address", "https://gitlab.com/g/s/p/-/releases/v3.1/downloads/p-win.exe", list[0].Files.Single().Url);
        Check("page", "https://gitlab.com/g/s/p/-/releases/v3.1", list[0].PageUrl);
        Check("released_at", new DateTimeOffset(2026, 11, 5, 8, 0, 0, TimeSpan.Zero), list[0].Date);
        Check("no links -> source", VersionKind.ReleaseSource, list[1].Kind);
        Check("zip source chosen", "https://gitlab.com/g/s/p/-/archive/v3.0/p-v3.0.zip", list[1].Files.Single().Url);
        Check("source file name", "p-source.zip", list[1].Files.Single().Name);

        Check("only zip and tar.gz of GitLab's four formats", "p-source.zip|p-source.tar.gz",
            string.Join("|", list[0].Sources.Select(f => f.Name)));
        Check("GitLab tar.gz address", "https://gitlab.com/g/s/p/-/archive/v3.1/p-v3.1.tar.gz", list[0].Sources[1].Url);
        Check("setting on: link, then the archives", "p-win.exe|p-source.zip|p-source.tar.gz",
            string.Join("|", list[0].Offered(true).Select(f => f.Name)));
    }

    private static void TestGiteaParsing()
    {
        Section("Gitea / Forgejo releases");

        const string json = """
            [{"tag_name":"v9.0.1","name":"9.0.1","draft":false,"prerelease":false,
              "published_at":"2026-11-02T00:00:00+01:00","html_url":"https://codeberg.org/o/r/releases/tag/v9.0.1",
              "zipball_url":"https://codeberg.org/o/r/archive/v9.0.1.zip",
              "tarball_url":"https://codeberg.org/o/r/archive/v9.0.1.tar.gz",
              "assets":[{"name":"r-9.0.1.tar.xz","size":4096,
                         "browser_download_url":"https://codeberg.org/o/r/releases/download/v9.0.1/r-9.0.1.tar.xz"}]}]
            """;

        Release release = ReleaseParser.ParseList(ApiType.Gitea, json, "o/r").Single();
        Check("tag", "v9.0.1", release.Tag);
        Check("date in UTC", new DateTimeOffset(2026, 11, 1, 23, 0, 0, TimeSpan.Zero), release.Date);
        Check("file", "r-9.0.1.tar.xz", release.Files.Single().Name);
        Check("size", 4096L, release.Files.Single().Size);
        Check("kind", VersionKind.Release, release.Kind);
        Check("Gitea archives", "https://codeberg.org/o/r/archive/v9.0.1.zip|https://codeberg.org/o/r/archive/v9.0.1.tar.gz",
            string.Join("|", release.Sources.Select(f => f.Url)));

        const string single ="""{"tag_name":"v1","assets":[],"zipball_url":"https://codeberg.org/o/r/archive/v1.zip","published_at":"2026-01-01T00:00:00Z"}""";
        Release one = ReleaseParser.ParseSingle(ApiType.Gitea, single, "o/r");
        Check("single release source", VersionKind.ReleaseSource, one.Kind);
    }

    private static void TestPickLatest()
    {
        Section("Which release is Latest");

        List<Release> list = ReleaseParser.ParseList(ApiType.GitHub, GitHubList, "o/r");
        Check("newest stable, skipping the newer pre-release and the draft", "v1.4.0", ReleaseParser.PickLatest(list)?.Tag);

        const string onlyPre = """
            [{"tag_name":"rc1","prerelease":true,"published_at":"2026-11-01T00:00:00Z","assets":[]},
             {"tag_name":"rc2","prerelease":true,"published_at":"2026-11-10T00:00:00Z","assets":[]}]
            """;
        Check("only pre-releases -> newest of them", "rc2",
            ReleaseParser.PickLatest(ReleaseParser.ParseList(ApiType.GitHub, onlyPre, "o/r"))?.Tag);

        const string onlyDraft = """[{"tag_name":"d","draft":true,"created_at":"2026-11-01T00:00:00Z","assets":[]}]""";
        Check("only drafts -> none", null,
            ReleaseParser.PickLatest(ReleaseParser.ParseList(ApiType.GitHub, onlyDraft, "o/r"))?.Tag);

        Check("empty list -> none", null, ReleaseParser.PickLatest([])?.Tag);
    }

    private static void TestSourceHead()
    {
        Section("Repos without releases");

        Check("default branch", "main", ReleaseParser.ParseDefaultBranch("""{"default_branch":"main"}"""));
        Check("empty GitLab project", "", ReleaseParser.ParseDefaultBranch("""{"default_branch":null}"""));

        const string sha = "0123456789abcdef0123456789abcdef01234567";

        var gh = ReleaseParser.ParseHeadCommit(ApiType.GitHub,
            """{"sha":"SHA","html_url":"https://github.com/o/r/commit/x","commit":{"committer":{"date":"2026-11-13T09:30:00Z"}}}""".Replace("SHA", sha));
        Check("GitHub commit", sha, gh?.Sha);
        Check("GitHub commit date", new DateTimeOffset(2026, 11, 13, 9, 30, 0, TimeSpan.Zero), gh?.Date);

        var gl = ReleaseParser.ParseHeadCommit(ApiType.GitLab,
            $$"""{"id":"{{sha}}","committed_date":"2026-11-12T10:00:00.000+00:00","web_url":"https://gitlab.com/x"}""");
        Check("GitLab commit", sha, gl?.Sha);
        Check("GitLab commit date", new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero), gl?.Date);

        var gt = ReleaseParser.ParseHeadCommit(ApiType.Gitea,
            """{"name":"main","commit":{"id":"SHA","timestamp":"2026-11-11T11:00:00Z","url":"https://codeberg.org/x"}}""".Replace("SHA", sha));
        Check("Gitea commit", sha, gt?.Sha);
        Check("Gitea commit date", new DateTimeOffset(2026, 11, 11, 11, 0, 0, TimeSpan.Zero), gt?.Date);

        Site github = SiteOf("github.com");
        Release head = ReleaseParser.SourceHead("morluto/rea", "main", sha, gh?.Date, "", ApiUrls.SourceZip(github, "morluto/rea", sha));
        Check("kind", VersionKind.Commit, head.Kind);
        Check("whole commit id kept", sha, head.Tag);
        Check("title", "Source main @ 0123456", head.Title);
        Check("file name", "rea-source.zip", head.Files.Single().Name);
        Check("archive address", $"https://api.github.com/repos/morluto/rea/zipball/{sha}", head.Files.Single().Url);
        Check("short tag on screen", "0123456", head.ToVersion().DisplayTag);
        Check("source flag", true, head.ToVersion().IsSource);
        Check("no tar.gz address given: zip only, even with the setting on", "rea-source.zip",
            string.Join("|", head.Offered(true).Select(f => f.Name)));

        Release both = ReleaseParser.SourceHead("morluto/rea", "main", sha, gh?.Date, "",
            ApiUrls.SourceZip(github, "morluto/rea", sha), ApiUrls.SourceTarGz(github, "morluto/rea", sha));
        Check("commit, setting off: zip", "rea-source.zip", string.Join("|", both.Offered(false).Select(f => f.Name)));
        Check("commit, setting on: zip and tar.gz", "rea-source.zip|rea-source.tar.gz",
            string.Join("|", both.Offered(true).Select(f => f.Name)));
        Check("commit tar.gz address", $"https://api.github.com/repos/morluto/rea/tarball/{sha}", both.Sources[1].Url);
        Check("tar.gz saved with the version before the double extension", "rea-source (0123456).tar.gz",
            DownloadPaths.LabeledName(both.Sources[1].Name, both.ToVersion().DisplayTag));

        // What Current becomes after a download.
        Release withFiles = ReleaseParser.ParseList(ApiType.GitHub, GitHubList, "o/r")[2];
        Check("release files -> release", VersionKind.Release,
            Downloader.DownloadedVersion(withFiles, [withFiles.Files[0]]).Kind);
        Check("release files and source -> release", VersionKind.Release,
            Downloader.DownloadedVersion(withFiles, withFiles.Offered(true)).Kind);
        Check("only the source of a release with files -> marked source", VersionKind.ReleaseSource,
            Downloader.DownloadedVersion(withFiles, withFiles.Sources).Kind);
        Check("marked source, still the same version", true,
            VersionState.SameVersion(withFiles.ToVersion(), Downloader.DownloadedVersion(withFiles, withFiles.Sources)));
        Check("commit stays a commit", VersionKind.Commit, Downloader.DownloadedVersion(both, both.Sources).Kind);

        // The next update pre-checks the same kind of archive as last time, and not the other one.
        List<ReleaseFile> offered = withFiles.Offered(true);
        HashSet<string> again = DownloadPaths.PreChecked(offered, "v1.4.0",
            [@"Apps\X\app-v1.3.0-win64 (v1.3.0).zip", @"Apps\X\r-source (v1.3.0).tar.gz"], "v1.3.0");
        Check("last time's file and tar.gz pre-checked", "app-v1.4.0-win64.zip|r-source.tar.gz",
            string.Join("|", offered.Where(f => again.Contains(f.Name)).Select(f => f.Name)));
    }

    private static void TestApiUrls()
    {
        Section("API addresses");

        Site github = SiteOf("github.com");
        Site gitlab = SiteOf("gitlab.com");
        Site codeberg = SiteOf("codeberg.org");
        Site lan = SiteOf("http://192.168.0.5:3000");
        var enterprise = new Site { Host = "git.corp.local", Type = ApiType.GitHub };

        Check("github.com list", "https://api.github.com/repos/morluto/rea/releases?per_page=20",
            ApiUrls.Releases(github, "morluto/rea", 20));
        Check("Enterprise list", "https://git.corp.local/api/v3/repos/o/r/releases?per_page=20",
            ApiUrls.Releases(enterprise, "o/r", 20));
        Check("GitLab nested list", "https://gitlab.com/api/v4/projects/g%2Fs%2Fp/releases?per_page=100",
            ApiUrls.Releases(gitlab, "g/s/p", 100));
        Check("Codeberg list", "https://codeberg.org/api/v1/repos/o/r/releases?limit=20",
            ApiUrls.Releases(codeberg, "o/r", 20));
        Check("plain http server", "http://192.168.0.5:3000/api/v1/repos/o/r/releases?limit=20",
            ApiUrls.Releases(lan, "o/r", 20));

        Check("GitHub tag with slash", "https://api.github.com/repos/o/r/releases/tags/release/1.0",
            ApiUrls.ReleaseByTag(github, "o/r", "release/1.0"));
        Check("GitLab tag with slash", "https://gitlab.com/api/v4/projects/g%2Fp/releases/release%2F1.0",
            ApiUrls.ReleaseByTag(gitlab, "g/p", "release/1.0"));
        Check("Gitea tag", "https://codeberg.org/api/v1/repos/o/r/releases/tags/v1",
            ApiUrls.ReleaseByTag(codeberg, "o/r", "v1"));

        Check("GitHub head", "https://api.github.com/repos/o/r/commits/main", ApiUrls.HeadCommit(github, "o/r", "main"));
        Check("GitLab head", "https://gitlab.com/api/v4/projects/g%2Fp/repository/commits/main", ApiUrls.HeadCommit(gitlab, "g/p", "main"));
        Check("Gitea head", "https://codeberg.org/api/v1/repos/o/r/branches/main", ApiUrls.HeadCommit(codeberg, "o/r", "main"));

        Check("GitLab archive", "https://gitlab.com/api/v4/projects/g%2Fp/repository/archive.zip?sha=abc",
            ApiUrls.SourceZip(gitlab, "g/p", "abc"));
        Check("Gitea archive", "https://codeberg.org/api/v1/repos/o/r/archive/abc.zip", ApiUrls.SourceZip(codeberg, "o/r", "abc"));
        Check("GitLab tar.gz", "https://gitlab.com/api/v4/projects/g%2Fp/repository/archive.tar.gz?sha=abc",
            ApiUrls.SourceTarGz(gitlab, "g/p", "abc"));
        Check("Gitea tar.gz", "https://codeberg.org/api/v1/repos/o/r/archive/abc.tar.gz", ApiUrls.SourceTarGz(codeberg, "o/r", "abc"));
        Check("GitHub tar.gz", "https://api.github.com/repos/o/r/tarball/abc", ApiUrls.SourceTarGz(github, "o/r", "abc"));

        Check("GitLab releases page", "https://gitlab.com/g/s/p/-/releases", ApiUrls.ReleasesPage(gitlab, "g/s/p"));
        Check("GitHub releases page", "https://github.com/o/r/releases", ApiUrls.ReleasesPage(github, "o/r"));

        Check("GitHub README at a tag", "https://api.github.com/repos/o/r/readme?ref=v1.0", ApiUrls.Readme(github, "o/r", "v1.0"));
        Check("GitLab README at a tag with a slash", "https://gitlab.com/api/v4/projects/g%2Fp/repository/files/README.md/raw?ref=release%2F1.0",
            ApiUrls.Readme(gitlab, "g/p", "release/1.0"));
        Check("Forgejo README at a commit", "https://codeberg.org/api/v1/repos/o/r/raw/README.md?ref=abc123",
            ApiUrls.Readme(codeberg, "o/r", "abc123"));

        // GitHub's /readme answer: whatever README the repo has, in base64 broken into lines.
        Check("README.md content", "# Title\nText\n", System.Text.Encoding.UTF8.GetString(ReleaseParser.ReadmeFromGitHub(
            """{"name":"README.md","path":"README.md","encoding":"base64","content":"IyBUaXRs\nZQpUZXh0Cg==\n"}""") ?? []));
        Check("readme.md in lower case counts", true,
            ReleaseParser.ReadmeFromGitHub("""{"name":"readme.md","encoding":"base64","content":"eA=="}""") is not null);
        Check("plain README is skipped", null,
            ReleaseParser.ReadmeFromGitHub("""{"name":"README","encoding":"base64","content":"SGVsbG8gV29ybGQhCg==\n"}"""));
        Check("README.rst is skipped", null,
            ReleaseParser.ReadmeFromGitHub("""{"name":"README.rst","encoding":"base64","content":"eA=="}"""));
    }

    private static void TestTokenHosts()
    {
        Section("Where the token may go");

        var github = new Site { Host = "github.com", Type = ApiType.GitHub, Token = "t" };
        var gitlab = new Site { Host = "gitlab.com", Type = ApiType.GitLab, Token = "t" };
        var lan = new Site { Host = "http://192.168.0.5:3000", Type = ApiType.Gitea, Token = "t" };
        var noToken = new Site { Host = "github.com", Type = ApiType.GitHub };

        Check("api.github.com", true, ApiUrls.ShouldSendToken(github, new Uri("https://api.github.com/repos/o/r")));
        Check("github.com", true, ApiUrls.ShouldSendToken(github, new Uri("https://github.com/o/r/releases/download/v1/x")));
        Check("GitHub storage host", false, ApiUrls.ShouldSendToken(github, new Uri("https://objects.githubusercontent.com/x")));
        Check("downgrade to http", false, ApiUrls.ShouldSendToken(github, new Uri("http://api.github.com/x")));
        Check("GitLab own host", true, ApiUrls.ShouldSendToken(gitlab, new Uri("https://gitlab.com/api/v4/x")));
        Check("GitLab link to another server", false, ApiUrls.ShouldSendToken(gitlab, new Uri("https://example.com/file.zip")));
        Check("http server, same port", true, ApiUrls.ShouldSendToken(lan, new Uri("http://192.168.0.5:3000/x")));
        Check("http server, other port", false, ApiUrls.ShouldSendToken(lan, new Uri("http://192.168.0.5:4000/x")));
        Check("no token", false, ApiUrls.ShouldSendToken(noToken, new Uri("https://api.github.com/x")));
    }

    // ---------- version rules ----------

    private static void TestVersionState()
    {
        Section("Update / Download rules");

        static DateTimeOffset Day(int month, int day) => new(2026, month, day, 0, 0, 0, TimeSpan.Zero);

        var v13 = new VersionRef("v1.3", Day(10, 1), VersionKind.Release);
        var v14 = new VersionRef("v1.4", Day(11, 1), VersionKind.Release);

        var repo = new Repo();
        Check("never checked", RepoStatus.NeverChecked, VersionState.StatusOf(repo));

        repo.CheckedAt = Day(11, 14);
        Check("checked, nothing on the site", RepoStatus.NoReleases, VersionState.StatusOf(repo));

        repo.Latest = v14;
        Check("nothing downloaded", RepoStatus.Download, VersionState.StatusOf(repo));

        repo.Current = v13;
        Check("newer Latest", RepoStatus.Update, VersionState.StatusOf(repo));

        repo.Current = v14;
        Check("same version", RepoStatus.UpToDate, VersionState.StatusOf(repo));

        repo.Latest = new VersionRef("nightly", Day(11, 10), VersionKind.Release);
        repo.Current = new VersionRef("nightly", Day(11, 2), VersionKind.Release);
        Check("same tag republished", RepoStatus.Update, VersionState.StatusOf(repo));

        repo.Latest = v13;
        repo.Current = v14;
        Check("Current newer than Latest", RepoStatus.UpToDate, VersionState.StatusOf(repo));

        repo.Latest = v14;
        repo.Selected = v13;
        Check("pin older than Current", RepoStatus.Update, VersionState.StatusOf(repo));
        Check("pin is the target", "v1.3", VersionState.Target(repo)?.Tag);

        repo.Current = v13;
        Check("pin downloaded, newer Latest ignored", RepoStatus.UpToDate, VersionState.StatusOf(repo));

        repo.Selected = null;
        Check("pin cleared -> follows Latest again", RepoStatus.Update, VersionState.StatusOf(repo));

        repo.Current = new VersionRef("v1.4", null, VersionKind.ReleaseSource);
        Check("release and its own source archive are the same version", RepoStatus.UpToDate, VersionState.StatusOf(repo));

        var fresh = new Repo { Latest = v14, CheckedAt = Day(11, 14) };
        var outdated = new Repo { Latest = v14, Current = v13, CheckedAt = Day(11, 14) };
        List<Repo> pending = VersionState.PendingUpdates([fresh, outdated]);
        Check("Update All skips never-downloaded repos", 1, pending.Count);
        Check("Update All takes the outdated one", true, pending.Contains(outdated));
    }

    // ---------- database ----------

    private static void TestDatabaseSeed()
    {
        Section("Database creation");

        List<Site> sites = SiteStore.List();
        Check("three sites", 3, sites.Count);
        Check("github.com", "github.com/GitHub", $"{sites[0].Host}/{sites[0].Type}");
        Check("gitlab.com", "gitlab.com/GitLab", $"{sites[1].Host}/{sites[1].Type}");
        Check("codeberg.org", "codeberg.org/Gitea", $"{sites[2].Host}/{sites[2].Type}");

        SiteStore.SetToken(sites[2].Id, "abc");
        Database.Prepare();
        Check("preparing again adds nothing", 3, SiteStore.List().Count);
        Check("preparing again keeps the token", "abc", SiteStore.List().First(s => s.Id == sites[2].Id).Token);

        // Sites cannot be deleted from the screens; a row lost some other way comes back.
        using (SqliteConnection connection = Database.Open())
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM Sites WHERE Host = 'gitlab.com'";
            command.ExecuteNonQuery();
        }

        Database.Prepare();
        Check("a missing site is put back", 3, SiteStore.List().Count);
    }

    private static void TestFilters()
    {
        Section("Filter tree");

        long games = FilterStore.Add(null, "Games");
        long emulators = FilterStore.Add(null, "Emulators");
        long nintendo = FilterStore.Add(games, "Nintendo");
        long nes = FilterStore.Add(nintendo, "NES");
        long snes = FilterStore.Add(nintendo, "SNES");
        long n64 = FilterStore.Add(nintendo, "N64");
        long sony = FilterStore.Add(games, "Sony");

        List<FilterFolder> all = FilterStore.List();
        Check("roots in creation order", "Games,Emulators",
            string.Join(",", FilterStore.Children(null, all).Select(f => f.Name)));
        Check("children in creation order", "NES,SNES,N64",
            string.Join(",", FilterStore.Children(nintendo, all).Select(f => f.Name)));

        FilterStore.Shift(emulators, -1);
        all = FilterStore.List();
        Check("move up", "Emulators,Games", string.Join(",", FilterStore.Children(null, all).Select(f => f.Name)));

        FilterStore.Shift(emulators, -1);
        all = FilterStore.List();
        Check("first cannot go further up", "Emulators,Games", string.Join(",", FilterStore.Children(null, all).Select(f => f.Name)));

        Check("Games subtree",
            string.Join(",", new[] { games, nintendo, nes, snes, n64, sony }.Order()),
            string.Join(",", FilterStore.SubtreeIds(games, all).Order()));
        Check("NES subtree is itself", nes.ToString(), string.Join(",", FilterStore.SubtreeIds(nes, all)));
        Check("Nintendo subtree", 4, FilterStore.SubtreeIds(nintendo, all).Count);

        Check("path", @"Games\Nintendo\NES", string.Join(@"\", FilterStore.PathOf(nes, all)));
        Require("flattened list has full paths", FilterStore.Flatten(all).Any(f => f.FullPath == @"Games\Nintendo\SNES"));
        Check("flattened order", @"Emulators|Games|Games\Nintendo|Games\Nintendo\NES",
            string.Join("|", FilterStore.Flatten(all).Take(4).Select(f => f.FullPath)));

        Require("duplicate name among siblings refused", FilterStore.NameProblem("nes", nintendo, null, all) is not null);
        Check("same name elsewhere is fine", null, FilterStore.NameProblem("NES", sony, null, all));
        Check("renaming to its own name is fine", null, FilterStore.NameProblem("NES", nintendo, nes, all));
        Require("invalid characters refused", FilterStore.NameProblem("A:B", null, null, all) is not null);
        Require("empty name refused", FilterStore.NameProblem("  ", null, null, all) is not null);

        Check("unique name", "New Folder", FilterStore.UniqueName("New Folder", null, all));
        FilterStore.Add(null, "New Folder");
        all = FilterStore.List();
        Check("next unique name", "New Folder 2", FilterStore.UniqueName("New Folder", null, all));

        FilterStore.SetExpanded(nintendo, false);
        FilterStore.Rename(sony, "PlayStation");
        all = FilterStore.List();
        Check("closed state saved", false, all.First(f => f.Id == nintendo).Expanded);
        Check("new folders start open", true, all.First(f => f.Id == games).Expanded);
        Check("renamed", "PlayStation", all.First(f => f.Id == sony).Name);

        // A moved folder brings its whole subtree along.
        FilterStore.MoveTo(nintendo, emulators);
        all = FilterStore.List();
        Check("moved path", @"Emulators\Nintendo\SNES", string.Join(@"\", FilterStore.PathOf(snes, all)));
        Check("Games lost the moved branch", 2, FilterStore.SubtreeIds(games, all).Count);
        Check("Emulators gained it", 5, FilterStore.SubtreeIds(emulators, all).Count);

        bool refused = false;
        try
        {
            FilterStore.MoveTo(emulators, nes);
        }
        catch (InvalidOperationException)
        {
            refused = true;
        }

        Check("cannot move into its own subfolder", true, refused);

        FilterStore.MoveTo(nintendo, games);
        all = FilterStore.List();
        Check("moved back", @"Games\Nintendo\NES", string.Join(@"\", FilterStore.PathOf(nes, all)));

        long tmp = FilterStore.Add(null, "Tmp");
        long tmpChild = FilterStore.Add(tmp, "Child");
        FilterStore.Delete(tmp);
        all = FilterStore.List();
        Check("delete takes subfolders along", false, all.Any(f => f.Id == tmpChild));
    }

    private static void TestRepoStore()
    {
        Section("Repositories");

        List<FilterFolder> filters = FilterStore.List();
        long nes = filters.First(f => f.Name == "NES").Id;
        long games = filters.First(f => f.Name == "Games").Id;
        Site github = SiteStore.List().First(s => s.Host == "github.com");

        var repo = new Repo
        {
            Name = "Reverse Engineer Anything",
            SiteId = github.Id,
            Path = "morluto/rea",
            Folder = "Reverse Engineer Anything - rea",
            FilterId = nes
        };

        long id = RepoStore.Save(repo);
        Require("saved", id > 0);
        Check("duplicate found, any case", id, RepoStore.FindDuplicate(github.Id, "MORLUTO/REA", 0)?.Id);
        Check("not a duplicate of itself", null, RepoStore.FindDuplicate(github.Id, "morluto/rea", id));

        Check("repos in the Games subtree", 1, FilterStore.CountRepos(FilterStore.SubtreeIds(games, filters)));

        var latest = new VersionRef("v1.4", new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero), VersionKind.ReleaseSource);
        var selected = new VersionRef("v1.3", new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero), VersionKind.Release);
        var current = new VersionRef(new string('a', 40), new DateTimeOffset(2026, 9, 1, 8, 30, 15, TimeSpan.Zero), VersionKind.Commit);
        var checkedAt = new DateTimeOffset(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);

        RepoStore.SetLatest(id, latest, checkedAt);
        RepoStore.SetSelected(id, selected);
        RepoStore.SetCurrent(id, current, [@"Apps\Games\x.zip", @"Apps\Games\y.zip"]);

        Repo? back = RepoStore.Get(id);
        Check("Latest round trip", latest, back?.Latest);
        Check("Selected round trip", selected, back?.Selected);
        Check("Current round trip", current, back?.Current);
        Check("Latest is source", true, back?.Latest?.IsSource);
        Check("Selected is not source", false, back?.Selected?.IsSource);
        Check("Current commit is source", true, back?.Current?.IsSource);
        Check("files", @"Apps\Games\x.zip|Apps\Games\y.zip", string.Join("|", back?.CurrentFiles ?? []));
        Check("checked at", checkedAt, back?.CheckedAt);

        // ----- Ignore on refresh -----

        Check("ignore on refresh: off for a new repo", false, back?.IgnoreOnRefresh);
        back!.IgnoreOnRefresh = true;
        RepoStore.Save(back);
        Check("ignore on refresh: saved by Edit", true, RepoStore.Get(id)?.IgnoreOnRefresh);
        Check("ignore on refresh: Edit leaves the versions alone", latest, RepoStore.Get(id)?.Latest);
        Check("ignore on refresh: left out of a refresh, still in the list", "0 of 1",
            $"{VersionState.ToRefresh(RepoStore.List().Where(r => r.Id == id)).Count} of {RepoStore.List().Count(r => r.Id == id)}");

        var fresh = new Repo { Name = "Ignored From The Start", SiteId = github.Id, Path = "o/ignored-new", Folder = "Ignored", IgnoreOnRefresh = true };
        RepoStore.Save(fresh);
        Check("ignore on refresh: saved by Add", true, RepoStore.Get(fresh.Id)?.IgnoreOnRefresh);
        RepoStore.Delete(fresh.Id);

        back.IgnoreOnRefresh = false;
        RepoStore.Save(back);
        Check("ignore on refresh: unticked again", false, RepoStore.Get(id)?.IgnoreOnRefresh);
        Check("ignore on refresh: back in a refresh", 1, VersionState.ToRefresh(RepoStore.List().Where(r => r.Id == id)).Count);

        RepoStore.SetError(id, "Not found (404).", checkedAt.AddHours(1));
        back = RepoStore.Get(id);
        Check("error saved", "Not found (404).", back?.Error);
        Check("error keeps the last Latest", "v1.4", back?.Latest?.Tag);

        RepoStore.SetLatest(id, latest, checkedAt);
        Check("success clears the error", "", RepoStore.Get(id)?.Error);

        RepoStore.SetSelected(id, null);
        Check("pin cleared", null, RepoStore.Get(id)?.Selected);

        RepoStore.ForgetRemoteState(id);
        back = RepoStore.Get(id);
        Check("new address forgets Latest", null, back?.Latest);
        Check("new address keeps Current", current, back?.Current);
        Check("new address keeps the files", 2, back?.CurrentFiles.Count);

        RepoStore.Delete(id);
        Check("deleted", null, RepoStore.Get(id));
    }

    private static void TestKeepPrevious()
    {
        Section("Keep previous versions");

        string folder = Path.Combine(AppPaths.AppsDir, "KeepTest");
        Directory.CreateDirectory(folder);

        var keeper = new Repo { KeepPrevious = true, Current = new VersionRef("v1.3.0", null, VersionKind.Release) };
        Check("update keeps the old version under its tag", "v1.3.0", Downloader.KeepAs(keeper, redownload: false));
        Check("redownload of the same version replaces its files", null, Downloader.KeepAs(keeper, redownload: true));
        Check("nothing kept without the option", null,
            Downloader.KeepAs(new Repo { Current = keeper.Current }, redownload: false));
        Check("kept before anything was current", "previous", Downloader.KeepAs(new Repo { KeepPrevious = true }, redownload: false));

        Check("same name gets the old version", Path.Combine(folder, "app-win64 (v1.3.0).zip"),
            DownloadPaths.KeptName(Path.Combine(folder, "app-win64.zip"), "v1.3.0"));
        Check("double extension stays whole", Path.Combine(folder, "app (v1.3.0).tar.gz"),
            DownloadPaths.KeptName(Path.Combine(folder, "app.tar.gz"), "v1.3.0"));
        Check("slash in the tag", Path.Combine(folder, "app (release_1).zip"),
            DownloadPaths.KeptName(Path.Combine(folder, "app.zip"), "release/1"));

        // A "nightly" published again under the same tag: same labeled name.
        File.WriteAllText(Path.Combine(folder, "app (nightly).zip"), "old nightly");
        Check("same tag again gets a counter", Path.Combine(folder, "app (nightly, 2).zip"),
            DownloadPaths.KeptName(Path.Combine(folder, "app (nightly).zip"), "nightly"));

        string Write(string name, string content)
        {
            string path = Path.Combine(folder, name);
            File.WriteAllText(path, content);
            return path;
        }

        string reused = Write("app-win64.zip", "v1 build");
        string versioned = Write("app-v1.zip", "v1 versioned");
        Write("app-win64 (v1).zip", "taken");

        CommitResult commit = Downloader.Commit(
            [(Write("app-win64.zip.part", "v2 build"), reused), (Write("app-v2.zip.part", "v2 versioned"), Path.Combine(folder, "app-v2.zip"))],
            [DownloadPaths.ToRelative(reused), DownloadPaths.ToRelative(versioned)],
            keepAs: "v1");

        Check("no warnings", 0, commit.Warnings.Count);
        Check("the renamed old file is reported, so its kept version follows it",
            DownloadPaths.ToRelative(Path.Combine(folder, "app-win64 (v1, 2).zip")), commit.Renamed.Apply(DownloadPaths.ToRelative(reused)));
        Check("new file under the reused name", "v2 build", File.ReadAllText(reused));
        Check("old file of the reused name kept, with a counter", "v1 build",
            File.ReadAllText(Path.Combine(folder, "app-win64 (v1, 2).zip")));
        Check("old versioned file not deleted", true, File.Exists(versioned));
        Check("new versioned file in place", "v2 versioned", File.ReadAllText(Path.Combine(folder, "app-v2.zip")));

        // ----- database -----

        Site github = SiteStore.List().First(s => s.Host == "github.com");
        var repo = new Repo { Name = "Keep", SiteId = github.Id, Path = "keep/test", Folder = "Keep" };
        long id = RepoStore.Save(repo);
        Check("new repo does not keep previous versions", false, RepoStore.Get(id)?.KeepPrevious);

        repo.KeepPrevious = true;
        RepoStore.Save(repo);
        Check("option saved", true, RepoStore.Get(id)?.KeepPrevious);

        // A database from before the option existed: the column is added on start.
        using (SqliteConnection connection = Database.Open())
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "ALTER TABLE Repos DROP COLUMN KeepPrevious";
            command.ExecuteNonQuery();
        }

        Database.Prepare();
        Check("older database gets the column, off", false, RepoStore.Get(id)?.KeepPrevious);

        RepoStore.Delete(id);
    }

    // ---------- folders moved by Edit, the Old folder, versions to keep ----------

    private static string AppsPath(params string[] parts) => Path.Combine([AppPaths.AppsDir, .. parts]);

    private static string Touch(string path, string content = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static void TestFolderMove()
    {
        Section("Download folder moved by Edit");

        string a = AppsPath("MoveTest", "Ky - ky");
        Touch(Path.Combine(a, "ky (v1).zip"), "v1");
        Touch(Path.Combine(a, "Old", "ky (v0).zip"), "v0");

        // Folder renamed.
        string b = AppsPath("MoveTest", "Ky");
        Check("renamed", null, RepoFolderMove.Move(a, b));
        Check("old name gone", false, Directory.Exists(a));
        Check("file under the new name", "v1", File.ReadAllText(Path.Combine(b, "ky (v1).zip")));
        Check("subfolder moved along", "v0", File.ReadAllText(Path.Combine(b, "Old", "ky (v0).zip")));

        // Filter changed: into another folder of the tree, created on the way.
        string c = AppsPath("MoveTest", "Games", "PC", "Ky");
        Check("moved into a new filter's folder", null, RepoFolderMove.Move(b, c));
        Check("file in the new filter's folder", "v1", File.ReadAllText(Path.Combine(c, "ky (v1).zip")));
        Check("nothing left at the old place", false, Directory.Exists(b));

        // Only the case changes: the same folder to Windows, renamed in place.
        string lower = AppsPath("MoveTest", "Games", "PC", "ky");
        Check("case-only rename", null, RepoFolderMove.Move(c, lower));
        Check("name on disk has the new case", "ky",
            Path.GetFileName(Directory.GetDirectories(AppsPath("MoveTest", "Games", "PC")).Single()));

        // Something already there: nothing moves.
        string taken = AppsPath("MoveTest", "Taken");
        Touch(Path.Combine(taken, "someone else's.txt"));
        string? refused = RepoFolderMove.Move(lower, taken);
        Require("a folder with files in it is not merged into: " + refused?.Split('\n')[0], refused?.Contains("already there") == true);
        Check("source left as it was", true, File.Exists(Path.Combine(lower, "ky (v1).zip")));

        // An empty folder in the way is taken over.
        string empty = AppsPath("MoveTest", "Empty");
        Directory.CreateDirectory(empty);
        Check("empty folder in the way taken over", null, RepoFolderMove.Move(lower, empty));
        Check("files there", true, File.Exists(Path.Combine(empty, "ky (v1).zip")));

        Check("nothing downloaded yet: nothing to move", null, RepoFolderMove.Move(AppsPath("MoveTest", "Never"), AppsPath("MoveTest", "Other")));
        Check("nothing created for it", false, Directory.Exists(AppsPath("MoveTest", "Other")));
        Check("same place", null, RepoFolderMove.Move(empty, empty));
        Require("outside Apps refused", RepoFolderMove.Move(empty, Path.Combine(AppPaths.BaseDir, "OutsideApps")) is not null);
        Check("still inside Apps", true, Directory.Exists(empty));

        // In use: the move fails and says what to do.
        string held = Touch(Path.Combine(empty, "held.txt"));
        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            string? busy = RepoFolderMove.Move(empty, AppsPath("MoveTest", "Busy"));
            Require("folder in use reported: " + busy?.Split('\n')[0], busy?.Contains("Close any program") == true);
        }

        Check("folder in use left where it was", true, Directory.Exists(empty));

        // ----- saved paths follow -----

        var moves = new PathMoves();
        moves.Add(AppsPath("Games", "Ky - ky"), AppsPath("Games", "PC", "Ky"));
        Check("file path follows the folder", @"Apps\Games\PC\Ky\ky (v1).zip", moves.Apply(@"Apps\Games\Ky - ky\ky (v1).zip"));
        Check("file in a subfolder follows", @"Apps\Games\PC\Ky\Old\a.zip", moves.Apply(@"Apps\Games\Ky - ky\Old\a.zip"));
        Check("case of the saved path does not matter", @"Apps\Games\PC\Ky\b.zip", moves.Apply(@"apps\games\ky - KY\b.zip"));
        Check("a folder that only starts the same is not moved", @"Apps\Games\Ky - ky2\c.zip", moves.Apply(@"Apps\Games\Ky - ky2\c.zip"));
        Check("other paths unchanged", @"Apps\Other\d.zip", moves.Apply(@"Apps\Other\d.zip"));
        List<PreviousVersion> kept = moves.Apply([new PreviousVersion(new VersionRef("v0", null, VersionKind.Release), [@"Apps\Games\Ky - ky\Old\e.zip"])]);
        Check("kept versions' files follow too", @"Apps\Games\PC\Ky\Old\e.zip", kept[0].Files[0]);
    }

    /// <summary>A folder of the tree renamed, then moved, in Settings → Filters: what the tab does, without the tab.</summary>
    private static void TestTreeFolderMove()
    {
        Section("Tree folder renamed or moved in Filters");

        long games = FilterStore.Add(null, "MoveGames");
        long nintendo = FilterStore.Add(games, "Nintendo");
        long snes = FilterStore.Add(nintendo, "SNES");
        long nes = FilterStore.Add(nintendo, "NES");
        long other = FilterStore.Add(null, "MoveOther");
        List<FilterFolder> filters = FilterStore.List();

        Site github = SiteStore.List().First(s => s.Host == "github.com");
        var a = new Repo { Name = "TreeA", SiteId = github.Id, Path = "tree/a", Folder = "A", FilterId = snes, KeepPrevious = true };
        var b = new Repo { Name = "TreeB", SiteId = github.Id, Path = "tree/b", Folder = "B", FilterId = nes };
        var c = new Repo { Name = "TreeC", SiteId = github.Id, Path = "tree/c", Folder = "C", FilterId = other };

        foreach (Repo repo in new[] { a, b, c })
        {
            RepoStore.Save(repo);
            string file = Touch(Path.Combine(DownloadPaths.RepoFolder(repo, filters), $"{repo.Folder.ToLowerInvariant()} (v2).zip"));
            RepoStore.SetCurrent(repo.Id, new VersionRef("v2", null, VersionKind.Release), [DownloadPaths.ToRelative(file)]);
        }

        string oldOfA = Touch(Path.Combine(DownloadPaths.RepoFolder(a, filters), "Old", "a (v1).zip"));
        RepoStore.SetCurrent(a.Id, RepoStore.Get(a.Id)!.Current!, RepoStore.Get(a.Id)!.CurrentFiles,
            [new PreviousVersion(new VersionRef("v1", null, VersionKind.Release), [DownloadPaths.ToRelative(oldOfA)])]);

        Check("tree folder on disk", AppsPath("MoveGames", "Nintendo"), DownloadPaths.TreeFolder(games, "Nintendo", filters));
        Check("a repo's folder is its filter's folder plus its own", AppsPath("MoveGames", "Nintendo", "SNES", "A"), DownloadPaths.RepoFolder(a, filters));

        // Renamed: Nintendo -> Consoles.
        string from = DownloadPaths.TreeFolder(games, "Nintendo", filters);
        string to = DownloadPaths.TreeFolder(games, "Consoles", filters);
        Check("renamed on disk", null, RepoFolderMove.Move(from, to));
        var moves = new PathMoves();
        moves.Add(from, to);
        Check("rows changed: two repos and one kept version", 3, RepoStore.ApplyMoves(moves));
        FilterStore.Rename(nintendo, "Consoles");
        filters = FilterStore.List();

        foreach (Repo repo in new[] { a, b })
        {
            Repo stored = RepoStore.Get(repo.Id)!;
            string full = DownloadPaths.ToAbsolute(stored.CurrentFiles[0]);
            Check($"{repo.Name}: saved path inside the renamed folder", true,
                full.StartsWith(AppsPath("MoveGames", "Consoles") + "\\", StringComparison.OrdinalIgnoreCase));
            Check($"{repo.Name}: file found there", true, File.Exists(full));
            Check($"{repo.Name}: and where its next download goes", Path.GetDirectoryName(full), DownloadPaths.RepoFolder(stored, filters));
        }

        string keptA = DownloadPaths.ToAbsolute(RepoStore.PreviousVersions(a.Id)[0].Files[0]);
        Check("kept version's path follows too", AppsPath("MoveGames", "Consoles", "SNES", "A", "Old", "a (v1).zip"), keptA);
        Check("kept version's file there", true, File.Exists(keptA));
        Check("repo in another folder untouched", @"Apps\MoveOther\C\c (v2).zip", RepoStore.Get(c.Id)!.CurrentFiles[0]);
        Check("nothing more to change the second time", 0, RepoStore.ApplyMoves(moves));

        // Moved: Consoles from under MoveGames to the top level.
        from = DownloadPaths.TreeFolder(games, "Consoles", filters);
        to = DownloadPaths.TreeFolder(null, "Consoles", filters);
        Check("moved on disk", null, RepoFolderMove.Move(from, to));
        moves = new PathMoves();
        moves.Add(from, to);
        RepoStore.ApplyMoves(moves);
        FilterStore.MoveTo(nintendo, null);
        filters = FilterStore.List();

        Repo movedB = RepoStore.Get(b.Id)!;
        Check("moved: saved path", @"Apps\Consoles\NES\B\b (v2).zip", movedB.CurrentFiles[0]);
        Check("moved: file there", true, File.Exists(DownloadPaths.ToAbsolute(movedB.CurrentFiles[0])));
        Check("moved: next download goes there too", AppAbsoluteFolder(movedB.CurrentFiles[0]), DownloadPaths.RepoFolder(movedB, filters));
        Check("moved: old place empty", false, Directory.Exists(AppsPath("MoveGames", "Consoles")));

        // The name taken on disk by something else: refused, nothing changes.
        Touch(AppsPath("MoveOther", "Consoles", "someone's.txt"));
        string? refused = RepoFolderMove.Move(to, DownloadPaths.TreeFolder(other, "Consoles", filters));
        Require("taken on disk: refused", refused?.Contains("already there") == true);
        Check("taken on disk: files stay", true, File.Exists(DownloadPaths.ToAbsolute(RepoStore.Get(a.Id)!.CurrentFiles[0])));

        foreach (Repo repo in new[] { a, b, c })
        {
            RepoStore.Delete(repo.Id);
        }

        foreach (long id in new[] { snes, nes, nintendo, games, other })
        {
            FilterStore.Delete(id);
        }

        static string AppAbsoluteFolder(string relative) => Path.GetDirectoryName(DownloadPaths.ToAbsolute(relative))!;
    }

    private static void TestFileCheck()
    {
        Section("Check Files");

        // ----- labels read back from names -----

        Check("label", "v1.2", DownloadPaths.ReadLabel("app (v1.2).zip")?.Tag);
        Check("label without extension", "v1.2.3", DownloadPaths.ReadLabel("app (v1.2.3)")?.Tag);
        Check("label before a double extension", "v1", DownloadPaths.ReadLabel("app (v1).tar.gz")?.Tag);
        Check("earlier build under the same tag: tag", "nightly", DownloadPaths.ReadLabel("app (nightly, 2).zip")?.Tag);
        Check("earlier build under the same tag: copy", 2, DownloadPaths.ReadLabel("app (nightly, 2).zip")?.Counter);
        Check("only the last parentheses", "v1", DownloadPaths.ReadLabel("app (x64) (v1).zip")?.Tag);
        Check("no label", null, DownloadPaths.ReadLabel("app.zip"));
        Check("a download under way is not one", null, DownloadPaths.ReadLabel("app (v1).zip.part"));
        Check("README.md", null, DownloadPaths.ReadLabel("README.md"));
        Check("what LabeledName writes, read back", "release_1",
            DownloadPaths.ReadLabel(DownloadPaths.LabeledName("app.zip", "release/1"))?.Tag);

        Check("same shape: versions", FileCheck.Shape("v2.2.0"), FileCheck.Shape("v2.0.2"));
        Check("same shape: commits", FileCheck.Shape("7fd1a60"), FileCheck.Shape("a1b2c3d"));
        Require("other shape: x64 is no version like v2.2.0", FileCheck.Shape("x64") != FileCheck.Shape("v2.2.0"));

        // ----- earlier downloads recorded -----

        Site github = SiteStore.List().First(s => s.Host == "github.com");
        var repo = new Repo { Name = "Checked", SiteId = github.Id, Path = "check/app", Folder = "Checked", KeepPrevious = true };
        RepoStore.Save(repo);
        string folder = DownloadPaths.RepoFolder(repo, []);

        string Dated(string path, int month)
        {
            Touch(path);
            File.SetLastWriteTimeUtc(path, new DateTime(2026, month, 1, 0, 0, 0, DateTimeKind.Utc));
            return path;
        }

        string current = Dated(Path.Combine(folder, "app (v2.0.0).zip"), 5);
        RepoStore.SetCurrent(repo.Id, new VersionRef("v2.0.0", null, VersionKind.Release), [DownloadPaths.ToRelative(current)]);

        string currentTar = Dated(Path.Combine(folder, "app (v2.0.0).tar.gz"), 5);
        string v1 = Dated(Path.Combine(folder, "app (v1.0.0).zip"), 1);
        string v15 = Dated(Path.Combine(folder, "Old", "app (v1.5.0).zip"), 2);
        string sameTag = Dated(Path.Combine(folder, "Old", "app (v2.0.0, 2).zip"), 3);
        string[] theirs =
        [
            Touch(Path.Combine(folder, "notes (2).txt")),
            Touch(Path.Combine(folder, "Setup (x64).exe")),
            Touch(Path.Combine(folder, "README.md")),
            Touch(Path.Combine(folder, "save.dat")),
            Touch(Path.Combine(folder, "extracted", "game (v9.9.9).exe"))
        ];

        Check("files recorded: 1 of the current version, 3 earlier", 4, FileCheck.AdoptOnce());
        Repo adopted = RepoStore.Get(repo.Id)!;
        Check("current version's other file joins it", true,
            adopted.CurrentFiles.Contains(DownloadPaths.ToRelative(currentTar), StringComparer.OrdinalIgnoreCase));
        Check("earlier versions kept, oldest first", "v1.0.0,v1.5.0,v2.0.0, 2",
            string.Join(",", RepoStore.PreviousVersions(repo.Id).Select(v => v.Version.Tag)));
        Check("the one in Old recorded where it is", DownloadPaths.ToRelative(v15), RepoStore.PreviousVersions(repo.Id)[1].Files[0]);

        var all = new HashSet<string>(
            adopted.CurrentFiles.Concat(RepoStore.PreviousVersions(repo.Id).SelectMany(v => v.Files)).Select(DownloadPaths.ToAbsolute),
            StringComparer.OrdinalIgnoreCase);
        Check("the user's files are not recorded (never deleted by an update)", 0, theirs.Count(all.Contains));
        Check("only once", 0, FileCheck.AdoptOnce());
        Check("nothing new the next time", 0, FileCheck.AdoptDownloads());

        // ----- loose, missing -----

        long tree = FilterStore.Add(null, "CheckTree");
        string stray = Touch(AppsPath("CheckTree", "Stray.txt"));
        string goneRepo = AppsPath("CheckTree", "Deleted repo");
        Touch(Path.Combine(goneRepo, "x (v1).zip"));
        Touch(Path.Combine(goneRepo, "README.md"));
        string desktop = Touch(AppsPath("CheckTree", "desktop.ini"));
        File.SetAttributes(desktop, FileAttributes.Hidden | FileAttributes.System);

        // A repo whose file is still where an older Folder name put it.
        var moved = new Repo { Name = "Moved", SiteId = github.Id, Path = "check/moved", Folder = "New name", FilterId = tree };
        RepoStore.Save(moved);
        string oldPlace = Touch(AppsPath("CheckStale", "Old name", "m (v1).zip"));
        string leftBehind = Touch(AppsPath("CheckStale", "Old name", "README.md"));
        RepoStore.SetCurrent(moved.Id, new VersionRef("v1", null, VersionKind.Release), [DownloadPaths.ToRelative(oldPlace)]);

        File.Delete(v1);
        File.Delete(currentTar);

        FileCheckResult result = FileCheck.Run();
        var missing = result.Missing.Where(m => m.RepoId == repo.Id).ToList();
        Check("missing: the two deleted files", "v1.0.0 app (v1.0.0).zip|v2.0.0 app (v2.0.0).tar.gz",
            string.Join("|", missing.Select(m => $"{m.Version} {Path.GetFileName(m.Path)}").Order(StringComparer.Ordinal)));
        Check("missing: named after the repo", "Checked", missing[0].RepoName);
        Require($"files counted: {result.Checked}", result.Checked >= 6);

        bool IsLoose(string path) => result.Loose.Contains(Path.GetFullPath(path), StringComparer.OrdinalIgnoreCase);
        Check("loose: a file in a tree folder", true, IsLoose(stray));
        Check("loose: a folder of no repo, listed whole", true, IsLoose(goneRepo));
        Check("loose: not file by file", false, IsLoose(Path.Combine(goneRepo, "README.md")));
        Check("loose: a file left beside a recorded one in an old place", true, IsLoose(leftBehind));
        Check("not loose: the recorded file in the old place", false, IsLoose(oldPlace));
        Check("not loose: a tree folder", false, IsLoose(AppsPath("CheckTree")));
        Check("not loose: anything inside a repo's folder", 0, result.Loose.Count(p => p.StartsWith(folder, StringComparison.OrdinalIgnoreCase)));
        Check("not loose: Windows' desktop.ini", false, IsLoose(desktop));

        FileCheck.ForgetMissing(missing);
        Check("forgotten: no longer missing", 0, FileCheck.Run().Missing.Count(m => m.RepoId == repo.Id));
        Check("forgotten: the version left with no file is gone", "v1.5.0,v2.0.0, 2",
            string.Join(",", RepoStore.PreviousVersions(repo.Id).Select(v => v.Version.Tag)));
        Check("forgotten: current version kept", "v2.0.0", RepoStore.Get(repo.Id)!.Current?.Tag);
        Check("forgotten: its file still there kept", 1, RepoStore.Get(repo.Id)!.CurrentFiles.Count);

        RepoStore.Delete(repo.Id);
        RepoStore.Delete(moved.Id);
        FilterStore.Delete(tree);
    }

    /// <summary>"Use custom download folder": where downloads go, and moving them from one folder to another.</summary>
    private static void TestDownloadRoot()
    {
        Section("Custom download folder");

        string Under(string root, params string[] parts) => Path.Combine([root, .. parts]);

        Check("Apps by default", AppPaths.AppsDir, DownloadRoot.Current);
        Check("no custom folder by default", null, DownloadRoot.Custom());

        string outside = Path.Combine(Path.GetTempPath(), "Git-Sync tests", "Custom");
        Check("path in Git-Sync's folder saved relative", @"Apps\Games\x.zip", DownloadPaths.ToRelative(AppsPath("Games", "x.zip")));
        Check("path outside it saved whole", Under(outside, "x.zip"), DownloadPaths.ToRelative(Under(outside, "x.zip")));
        Check("and read back", Under(outside, "x.zip"), DownloadPaths.ToAbsolute(DownloadPaths.ToRelative(Under(outside, "x.zip"))));

        // ----- which folders can be chosen -----

        Require("the same folder refused", DownloadRoot.Problem(AppPaths.AppsDir, AppPaths.AppsDir) is not null);
        Require("a folder inside the current one refused", DownloadRoot.Problem(AppsPath("Games"), AppPaths.AppsDir) is not null);
        Require("Git-Sync's own folder refused", DownloadRoot.Problem(AppPaths.BaseDir, AppPaths.AppsDir) is not null);
        Require("a folder around Git-Sync's refused", DownloadRoot.Problem(Path.GetDirectoryName(AppPaths.BaseDir)!, AppPaths.AppsDir) is not null);
        Check("another folder accepted", null, DownloadRoot.Problem(outside, AppPaths.AppsDir));
        Check("back to Apps accepted", null, DownloadRoot.Problem(AppPaths.AppsDir, outside));

        // ----- the setting -----

        try
        {
            DownloadRoot.SetCustom(outside);
            Check("custom folder in use", outside, DownloadRoot.Current);
            Check("repo folders under it", Under(outside, "Ky"), DownloadPaths.RepoFolder(new Repo { Folder = "Ky" }, []));
            Check("deleting allowed inside it", true, DownloadPaths.IsInsideDownloads(Under(outside, "Ky", "a.zip")));
            Check("and no longer in Apps", false, DownloadPaths.IsInsideDownloads(AppsPath("Ky", "a.zip")));

            DownloadRoot.SetCustom(null);
            Check("unticked: back to Apps", AppPaths.AppsDir, DownloadRoot.Current);
            Check("unticked: the folder forgotten", null, DownloadRoot.Custom());
        }
        finally
        {
            DownloadRoot.SetCustom(null);
        }

        // ----- moving everything, same drive -----

        string a = Path.Combine(AppPaths.BaseDir, "RootTest", "A");
        string b = Path.Combine(AppPaths.BaseDir, "RootTest", "B");
        Touch(Under(a, "Games", "PC", "Ky", "ky (v1).zip"), "ky");
        Touch(Under(a, "Games", "PC", "Ky", "Old", "ky (v0).zip"), "old ky");
        Touch(Under(a, "loose.txt"), "loose");

        Check("nothing in the way", 0, FolderTransfer.Conflicts(a, b).Count);
        Check("same drive: nothing left behind", 0, FolderTransfer.MoveContents(a, b, null).Count);
        Check("same drive: folders keep their places", "ky", File.ReadAllText(Under(b, "Games", "PC", "Ky", "ky (v1).zip")));
        Check("same drive: subfolders too", "old ky", File.ReadAllText(Under(b, "Games", "PC", "Ky", "Old", "ky (v0).zip")));
        Check("same drive: loose files too", "loose", File.ReadAllText(Under(b, "loose.txt")));
        Check("same drive: old folder emptied", false, FolderTransfer.HasContent(a));

        Touch(Under(a, "Games", "other.txt"));
        Check("a name the target has is in the way", "Games", string.Join("|", FolderTransfer.Conflicts(a, b)));

        // ----- moving by copying (another drive) -----

        string c = Path.Combine(AppPaths.BaseDir, "RootTest", "C");
        string dated = Under(b, "Games", "PC", "Ky", "ky (v1).zip");
        File.SetLastWriteTimeUtc(dated, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        var reports = new List<(long Done, long Total)>();
        Check("copied: nothing left behind", 0, FolderTransfer.MoveContents(b, c, new Recorder(reports), alwaysCopy: true).Count);
        Check("copied: files there", "old ky", File.ReadAllText(Under(c, "Games", "PC", "Ky", "Old", "ky (v0).zip")));
        Check("copied: dates kept (earlier downloads are told apart by them)", new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            File.GetLastWriteTimeUtc(Under(c, "Games", "PC", "Ky", "ky (v1).zip")));
        Check("copied: originals deleted", false, FolderTransfer.HasContent(b));
        Check("copied: progress ends at the total", true, reports.Count > 0 && reports[^1].Done == reports[^1].Total);

        // A file that cannot be read: nothing is copied, nothing deleted.
        string held = Under(c, "Games", "PC", "Ky", "ky (v1).zip");

        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            try
            {
                FolderTransfer.MoveContents(c, b, null, alwaysCopy: true);
                Check("copy with a file in use", "an error", "no error");
            }
            catch (IOException)
            {
                Check("copy with a file in use: nothing left in the target", false,
                    Directory.Exists(b) && Directory.EnumerateFiles(b, "*", SearchOption.AllDirectories).Any());
                Check("copy with a file in use: everything still at the source", true, File.Exists(Under(c, "loose.txt")) && File.Exists(held));
            }
        }

        // Renamed on the same drive, with a folder in use: what moved comes back.
        string first = Touch(Under(c, "A-free", "f.txt"));

        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            try
            {
                FolderTransfer.MoveContents(c, b, null);
                Check("rename with a folder in use", "an error", "no error");
            }
            catch (IOException)
            {
                Check("rename with a folder in use: the one moved first is back", true, File.Exists(first));
                Check("rename with a folder in use: target left empty", false, FolderTransfer.HasContent(b));
            }
        }

        // To a real other drive, when the temp folder is on one.
        string tempRoot = Path.GetPathRoot(Path.GetTempPath())!;

        if (!string.Equals(tempRoot, Path.GetPathRoot(AppPaths.BaseDir), StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.Exists(outside))
            {
                Directory.Delete(outside, recursive: true);
            }

            Check($"to drive {tempRoot}: moved", 0, FolderTransfer.MoveContents(c, outside, null).Count);
            Check($"to drive {tempRoot}: files there", "old ky", File.ReadAllText(Under(outside, "Games", "PC", "Ky", "Old", "ky (v0).zip")));
            Check($"to drive {tempRoot}: originals gone", false, FolderTransfer.HasContent(c));
            Directory.Delete(outside, recursive: true);
        }
        else
        {
            Console.WriteLine("       (the temp folder is on the same drive: no move to another drive tested)");
        }

        // ----- the records -----

        Site github = SiteStore.List().First(s => s.Host == "github.com");
        var repo = new Repo { Name = "Rooted", SiteId = github.Id, Path = "root/test", Folder = "Rooted", KeepPrevious = true };
        RepoStore.Save(repo);
        string file = Touch(AppsPath("Rooted", "r (v2).zip"));
        string kept = Touch(AppsPath("Rooted", "Old", "r (v1).zip"));
        RepoStore.SetCurrent(repo.Id, new VersionRef("v2", null, VersionKind.Release), [DownloadPaths.ToRelative(file)],
            [new PreviousVersion(new VersionRef("v1", null, VersionKind.Release), [DownloadPaths.ToRelative(kept)])]);

        var moves = new PathMoves();
        moves.Add(AppPaths.AppsDir, outside);
        RepoStore.ApplyMoves(moves);
        Check("records follow to a folder outside Git-Sync's, saved whole", Under(outside, "Rooted", "r (v2).zip"), RepoStore.Get(repo.Id)!.CurrentFiles[0]);
        Check("kept versions too", Under(outside, "Rooted", "Old", "r (v1).zip"), RepoStore.PreviousVersions(repo.Id)[0].Files[0]);

        try
        {
            // The custom folder's drive not connected: nothing is reported missing.
            DownloadRoot.SetCustom(Path.Combine(outside, "not there"));
            Check("folder not found: the check says so", true, FileCheck.Run().RootMissing);
        }
        finally
        {
            DownloadRoot.SetCustom(null);
        }

        RepoStore.ForgetFilesUnder(outside);
        Check("left behind: no file recorded any more", 0, RepoStore.Get(repo.Id)!.CurrentFiles.Count);
        Check("left behind: no kept version", 0, RepoStore.PreviousVersions(repo.Id).Count);
        Check("left behind: the version itself stays", "v2", RepoStore.Get(repo.Id)!.Current?.Tag);

        RepoStore.Delete(repo.Id);
        Directory.Delete(Path.Combine(AppPaths.BaseDir, "RootTest"), recursive: true);
    }

    /// <summary>Keeps every report as it is made; Progress&lt;T&gt; would deliver them later, on another thread.</summary>
    private sealed class Recorder(List<(long Done, long Total)> reports) : IProgress<(long Done, long Total)>
    {
        public void Report((long Done, long Total) value) => reports.Add(value);
    }

    /// <summary>Subfolders in a repo's Folder, and a repo's own "Custom folder" anywhere.</summary>
    private static void TestRepoFolders()
    {
        Section("Repo folders: subfolders and custom folders");

        Check("one name", true, DownloadPaths.IsValidFolderPath("Ky"));
        Check("subfolders with \\", true, DownloadPaths.IsValidFolderPath(@"Emulators\Ky"));
        Check("subfolders with /", true, DownloadPaths.IsValidFolderPath("Emulators/Ky"));
        Check("spaces around the names", true, DownloadPaths.IsValidFolderPath(" Emulators / Ky "));
        Check("empty part refused", false, DownloadPaths.IsValidFolderPath(@"Emulators\\Ky"));
        Check("leading \\ refused", false, DownloadPaths.IsValidFolderPath(@"\Ky"));
        Check("trailing \\ refused", false, DownloadPaths.IsValidFolderPath(@"Ky\"));
        Check(".. refused", false, DownloadPaths.IsValidFolderPath(@"..\Ky"));
        Check("a drive refused without Custom folder", false, DownloadPaths.IsValidFolderPath(@"C:\Ky"));
        Check("a reserved name refused", false, DownloadPaths.IsValidFolderPath(@"Emulators\CON"));
        Check("saved with \\", @"Emulators\Ky", DownloadPaths.NormalizeFolder(" Emulators / Ky "));
        Check("full path saved without the last \\", @"D:\Emulators\Ky", DownloadPaths.NormalizeFolder(@"D:\Emulators\Ky\"));

        Check("subfolders inside the filter's folder", Path.Combine(AppPaths.AppsDir, "Emulators", "Retro", "Ky"),
            DownloadPaths.RepoFolder(new Repo { Folder = @"Retro\Ky", FilterId = 6 }, SampleTree));
        Check("a full path is a custom folder", true, DownloadPaths.IsCustomFolder(@"D:\Emulators\Ky"));
        Check("a name is not", false, DownloadPaths.IsCustomFolder("Ky"));
        Check("a custom folder is used as it is, whatever the filter", @"D:\Emulators\Ky",
            DownloadPaths.RepoFolder(new Repo { Folder = @"D:\Emulators\Ky", FilterId = 6 }, SampleTree));

        // ----- which custom folders -----

        Require("not a full path", DownloadPaths.CustomFolderProblem("Ky") is not null);
        Require("a whole drive refused", DownloadPaths.CustomFolderProblem(@"D:\")?.Contains("drive") == true);
        Require("Git-Sync's folder refused", DownloadPaths.CustomFolderProblem(AppPaths.BaseDir) is not null);
        Require("a folder around Git-Sync's refused", DownloadPaths.CustomFolderProblem(Path.GetDirectoryName(AppPaths.BaseDir)!) is not null);
        Require("the download folder refused", DownloadPaths.CustomFolderProblem(AppPaths.AppsDir) is not null);
        Require("a bad name in the path refused", DownloadPaths.CustomFolderProblem(@"D:\Emu\a*b") is not null);
        Check("a folder elsewhere accepted", null, DownloadPaths.CustomFolderProblem(Path.Combine(Path.GetTempPath(), "Git-Sync tests", "Ky")));
        Check("a folder inside the download folder accepted", null, DownloadPaths.CustomFolderProblem(AppsPath("Special", "Ky")));

        // ----- one folder inside another -----

        Check("several subfolder levels", Path.Combine(AppPaths.AppsDir, "Emulators", "Retro", "Cores", "Arcade", "Ky"),
            DownloadPaths.RepoFolder(new Repo { Folder = "Retro/Cores/Arcade/Ky", FilterId = 6 }, SampleTree));
        Check("several levels saved", @"Retro\Cores\Arcade\Ky", DownloadPaths.NormalizeFolder("Retro/Cores/Arcade/Ky"));

        var emu = new Repo { Id = 1, Name = "Emu", Folder = "Emu", FilterId = 6 };
        var apart = new Repo { Id = 3, Name = "Apart", Folder = "Other", FilterId = 6 };
        List<Repo> repos = [emu, apart];
        string Folder(string folder) => DownloadPaths.RepoFolder(new Repo { Folder = folder, FilterId = 6 }, SampleTree);

        Check("inside another repo's folder: allowed", null, DownloadPaths.FolderConflict(Folder(@"Emu\Ky"), repos, 0, SampleTree));
        Check("several levels inside it: allowed", null, DownloadPaths.FolderConflict(Folder(@"Emu\Cores\Arcade\Ky"), repos, 0, SampleTree));
        Check("holding another repo's folder: allowed", null, DownloadPaths.FolderConflict(Folder(""), [apart], 0, SampleTree) is null
            ? null : "refused");
        Require("the very same folder: refused",
            DownloadPaths.FolderConflict(Folder("Emu"), repos, 0, SampleTree)?.Contains("already the folder of \"Emu\"") == true);
        Require("another repo's Old: refused",
            DownloadPaths.FolderConflict(Folder(@"Emu\Old"), repos, 0, SampleTree)?.Contains("\"Old\" folder of \"Emu\"") == true);
        Require("inside another repo's Old: refused", DownloadPaths.FolderConflict(Folder(@"Emu\Old\Ky"), repos, 0, SampleTree) is not null);
        Check("a folder named Old with no repo above it: allowed", null, DownloadPaths.FolderConflict(Folder(@"Other2\Old"), repos, 0, SampleTree));
        Require("holding another repo inside its own Old: refused",
            DownloadPaths.FolderConflict(Folder("Emu2"), [new Repo { Id = 5, Name = "In Old", Folder = @"Emu2\Old\X", FilterId = 6 }], 0, SampleTree)
                ?.Contains("this folder's \"Old\"") == true);
        Check("its own folder is no conflict", null, DownloadPaths.FolderConflict(Folder("Emu"), repos, emu.Id, SampleTree));

        // ----- moves -----

        string from = AppsPath("Folders", "Emu", "Ky");
        Touch(Path.Combine(from, "ky (v1).zip"));
        string own = Path.Combine(AppPaths.BaseDir, "OwnFolderTest", "Ky");
        Require("outside the download folder without a custom folder: refused", RepoFolderMove.Move(from, own) is not null);
        Check("to a custom folder: moved", null, RepoFolderMove.Move(from, own, anywhere: true));
        Check("the file there", true, File.Exists(Path.Combine(own, "ky (v1).zip")));
        RepoFolderMove.RemoveEmptyParents(from, AppsPath("Folders"));
        Check("the subfolder left empty goes", false, Directory.Exists(AppsPath("Folders", "Emu")));
        Check("up to the filter's folder, which stays", true, Directory.Exists(AppsPath("Folders")));
        // To a custom folder on another drive, when the temp folder is on one: copied, then deleted.
        string otherDrive = Path.Combine(Path.GetTempPath(), "Git-Sync tests", "Own Ky");

        if (!string.Equals(Path.GetPathRoot(otherDrive), Path.GetPathRoot(own), StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.Exists(otherDrive))
            {
                Directory.Delete(otherDrive, recursive: true);
            }

            Check("to another drive: moved", null, RepoFolderMove.Move(own, otherDrive, anywhere: true));
            Check("to another drive: the file there", true, File.Exists(Path.Combine(otherDrive, "ky (v1).zip")));
            Check("to another drive: nothing left at the old place", false, Directory.Exists(own));
            Check("and back", null, RepoFolderMove.Move(otherDrive, own, anywhere: true));
            Check("back: the file there", true, File.Exists(Path.Combine(own, "ky (v1).zip")));
        }

        // A repo whose folder holds another repo's: only its own entries move.
        string outer = AppsPath("Folders", "Outer");
        string inner = Path.Combine(outer, "Cores", "Inner");
        Touch(Path.Combine(outer, "outer (v1).zip"), "outer");
        Touch(Path.Combine(outer, "Old", "outer (v0).zip"), "outer old");
        Touch(Path.Combine(inner, "inner (v1).zip"), "inner");
        string outer2 = AppsPath("Folders", "Outer2");
        Check("outer moved, inner repo inside: done", null, RepoFolderMove.Move(outer, outer2, otherRepos: [inner]));
        Check("outer's own file moved", true, File.Exists(Path.Combine(outer2, "outer (v1).zip")));
        Check("outer's own Old moved", true, File.Exists(Path.Combine(outer2, "Old", "outer (v0).zip")));
        Check("the inner repo stays where its Folder says", true, File.Exists(Path.Combine(inner, "inner (v1).zip")));
        Check("nothing of the inner repo went along", false, Directory.Exists(Path.Combine(outer2, "Cores")));

        // Into a subfolder of itself ("Outer2" to "Outer2\Retro"): the new folder stays out of its own move.
        string deeper = Path.Combine(outer2, "Retro");
        Check("into a subfolder of itself: done", null, RepoFolderMove.Move(outer2, deeper));
        Check("its file there", true, File.Exists(Path.Combine(deeper, "outer (v1).zip")));
        Check("its Old there", true, File.Exists(Path.Combine(deeper, "Old", "outer (v0).zip")));
        Check("nothing else left beside it", "Retro", string.Join("|", Directory.EnumerateFileSystemEntries(outer2).Select(Path.GetFileName)));

        // Old leaves a nested repo alone: each has its own.
        string nestedOuter = AppsPath("Folders", "OldNest");
        Touch(Path.Combine(nestedOuter, "n (v1).zip"));
        Touch(Path.Combine(nestedOuter, "Sub", "Inner", "i (v1).zip"));
        Touch(Path.Combine(nestedOuter, "Sub", "Inner", "Old", "i (v0).zip"));
        (PathMoves nestMoves, List<string> _) = OldFolder.MoveAside(nestedOuter, [], [Path.Combine(nestedOuter, "Sub", "Inner")]);
        Check("Old: the outer repo's file moved", true, File.Exists(Path.Combine(nestedOuter, "Old", "n (v1).zip")));
        Check("Old: the folder holding the inner repo stays", true, File.Exists(Path.Combine(nestedOuter, "Sub", "Inner", "i (v1).zip")));
        Check("Old: the inner repo keeps its own Old", true, File.Exists(Path.Combine(nestedOuter, "Sub", "Inner", "Old", "i (v0).zip")));
        Check("Old: only the outer file moved", 1, nestMoves.Count);

        Touch(AppsPath("Folders", "Keep", "Ky", "x.txt"));
        RepoFolderMove.RemoveEmptyParents(AppsPath("Folders", "Keep", "Gone"), AppsPath("Folders"));
        Check("a folder with something in it stays", true, Directory.Exists(AppsPath("Folders", "Keep")));

        // ----- a repo downloading into its custom folder -----

        Site github = SiteStore.List().First(s => s.Host == "github.com");
        var custom = new Repo { Name = "Own", SiteId = github.Id, Path = "own/folder", Folder = own };
        RepoStore.Save(custom);
        Check("custom folder saved whole", own, RepoStore.Get(custom.Id)!.Folder);
        string v1 = Path.Combine(own, "ky (v1).zip");
        RepoStore.SetCurrent(custom.Id, new VersionRef("v1", null, VersionKind.Release), [DownloadPaths.ToRelative(v1)]);

        string v2 = Path.Combine(own, "ky (v2).zip");
        Touch(v2 + ".part", "v2");
        List<string> warnings = Downloader.Finish(RepoStore.Get(custom.Id)!, own, [(v2 + ".part", v2)],
            new VersionRef("v2", null, VersionKind.Release), redownload: false);
        Check("update in a custom folder: no warnings", 0, warnings.Count);
        Check("update in a custom folder: the old version's file deleted there", false, File.Exists(v1));
        Check("update in a custom folder: the new one in place", true, File.Exists(v2));

        // Earlier downloads are not looked for in a custom folder.
        Touch(Path.Combine(own, "ky (v0).zip"));
        FileCheck.AdoptDownloads();
        Check("nothing adopted from a custom folder", 1, RepoStore.Get(custom.Id)!.CurrentFiles.Count);

        // Leaving the download folder's files behind keeps those of a custom folder.
        string inApps = Touch(AppsPath("Folders", "InApps", "a (v1).zip"));
        var appsRepo = new Repo { Name = "In Apps", SiteId = github.Id, Path = "own/apps", Folder = "InApps" };
        RepoStore.Save(appsRepo);
        RepoStore.SetCurrent(appsRepo.Id, new VersionRef("v1", null, VersionKind.Release), [DownloadPaths.ToRelative(inApps)]);
        RepoStore.ForgetFilesUnder(AppPaths.AppsDir);
        Check("left behind: the download folder's files forgotten", 0, RepoStore.Get(appsRepo.Id)!.CurrentFiles.Count);
        Check("left behind: the custom folder's still recorded", 1, RepoStore.Get(custom.Id)!.CurrentFiles.Count);

        // A custom folder inside a place that moves (a folder of the tree renamed) goes along.
        string insideApps = AppsPath("Folders", "Tree", "Special");
        var treeRepo = new Repo { Name = "In tree", SiteId = github.Id, Path = "own/tree", Folder = insideApps };
        RepoStore.Save(treeRepo);
        var moves = new PathMoves();
        moves.Add(AppsPath("Folders", "Tree"), AppsPath("Folders", "Renamed"));
        RepoStore.ApplyMoves(moves);
        Check("a custom folder inside a moved folder follows", AppsPath("Folders", "Renamed", "Special"), RepoStore.Get(treeRepo.Id)!.Folder);
        Check("others' custom folders stay", own, RepoStore.Get(custom.Id)!.Folder);

        // ----- export files -----

        const string head = "\"format\":\"Git-Sync data\",\"version\":1";
        string file = "{" + head + ",\"filters\":[],\"repos\":["
            + "{\"name\":\"Sub\",\"site\":\"github.com\",\"api\":\"GitHub\",\"repository\":\"o/sub\",\"folder\":\"Emulators/Ky\"},"
            + "{\"name\":\"Full\",\"site\":\"github.com\",\"api\":\"GitHub\",\"repository\":\"o/full\",\"folder\":\"D:\\\\Emulators\\\\Full\\\\\"}]}";
        ExportData read = DataTransfer.Parse(file);
        Check("imported subfolders saved with \\", @"Emulators\Ky", read.Repos[0].Folder);
        Check("imported custom folder", @"D:\Emulators\Full", read.Repos[1].Folder);

        try
        {
            DataTransfer.Parse(file.Replace("Emulators/Ky", "../Ky", StringComparison.Ordinal));
            Check("a folder climbing out", "refused", "accepted");
        }
        catch (FormatException)
        {
            Check("a folder climbing out refused", true, true);
        }

        foreach (Repo repo in new[] { custom, appsRepo, treeRepo })
        {
            RepoStore.Delete(repo.Id);
        }

        Directory.Delete(Path.Combine(AppPaths.BaseDir, "OwnFolderTest"), recursive: true);
    }

    private static void TestOldFolder()
    {
        Section("Previous versions moved into Old");

        string folder = AppsPath("OldTest", "Ky");
        string v1 = Touch(Path.Combine(folder, "ky (v1).zip"), "v1");
        Touch(Path.Combine(folder, "ky (v1).tar.gz"), "v1 tar");
        Touch(Path.Combine(folder, "notes.txt"), "new notes");
        Touch(Path.Combine(folder, "extracted", "ky.exe"), "exe");
        string readme = Touch(Path.Combine(folder, "README.md"), "readme");
        string part = Touch(Path.Combine(folder, "ky (v2).zip.part"), "v2 arriving");
        Touch(Path.Combine(folder, "Old", "notes.txt"), "older notes");
        string desktop = Touch(Path.Combine(folder, "desktop.ini"), "[.ShellClassInfo]");
        File.SetAttributes(desktop, FileAttributes.Hidden | FileAttributes.System);

        (PathMoves moves, List<string> warnings) = OldFolder.MoveAside(folder, [part]);
        string old = Path.Combine(folder, "Old");

        Check("no warnings", 0, warnings.Count);
        Check("old version's file moved", "v1", File.ReadAllText(Path.Combine(old, "ky (v1).zip")));
        Check("double extension moved whole", "v1 tar", File.ReadAllText(Path.Combine(old, "ky (v1).tar.gz")));
        Check("a file of the user's moved too", true, File.Exists(Path.Combine(old, "notes (2).txt")));
        Check("one already in Old is not overwritten", "older notes", File.ReadAllText(Path.Combine(old, "notes.txt")));
        Check("a folder moved with what is inside", "exe", File.ReadAllText(Path.Combine(old, "extracted", "ky.exe")));
        Check("README.md stays for the new one to replace", true, File.Exists(readme));
        Check("the download under way stays", true, File.Exists(part));
        Check("Windows' desktop.ini stays", true, File.Exists(desktop));
        Check("folder holds only those", "desktop.ini|ky (v2).zip.part|Old|README.md",
            string.Join("|", Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName).Order(StringComparer.OrdinalIgnoreCase)));
        Check("saved path follows its file", DownloadPaths.ToRelative(Path.Combine(old, "ky (v1).zip")), moves.Apply(DownloadPaths.ToRelative(v1)));

        (PathMoves none, List<string> _) = OldFolder.MoveAside(folder, [part]);
        Check("nothing more to move the second time", 0, none.Count);
        (PathMoves missing, List<string> _) = OldFolder.MoveAside(AppsPath("OldTest", "Never"), []);
        Check("folder that does not exist yet", 0, missing.Count);
        Check("Old not created for nothing", false, Directory.Exists(AppsPath("OldTest", "Never")));

        Check("free name for a folder", Path.Combine(old, "extracted (2)"), OldFolder.FreeName(Path.Combine(old, "extracted"), isFolder: true));

        // In use: stays, with a warning, and the rest still moves.
        string folder2 = AppsPath("OldTest", "Busy");
        string busy = Touch(Path.Combine(folder2, "running.exe"));
        Touch(Path.Combine(folder2, "free.zip"));

        using (new FileStream(busy, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            (PathMoves moved, List<string> problems) = OldFolder.MoveAside(folder2, []);
            Check("file in use reported", 1, problems.Count);
            Check("the rest moved", 1, moved.Count);
        }

        Check("file in use stayed", true, File.Exists(busy));
    }

    private static void TestVersionHistory()
    {
        Section("Versions to keep");

        static VersionRef V(int n) => new($"v{n}", new DateTimeOffset(2026, 1, n, 0, 0, 0, TimeSpan.Zero), VersionKind.Release);
        static PreviousVersion P(int n, params string[] files) => new(V(n), [.. files]);
        static bool All(string _) => true;
        static string Tags(IEnumerable<PreviousVersion> list) => string.Join(",", list.Select(v => v.Version.Tag));

        var (keep, delete) = VersionHistory.Plan([], P(1, "a1", "b1"), V(2), ["a2", "b2"], 3, All);
        Check("2nd download: the 1st is kept", "v1", Tags(keep));
        Check("2nd download: nothing deleted", "", Tags(delete));

        (keep, delete) = VersionHistory.Plan([P(1, "a1", "b1")], P(2, "a2", "b2"), V(3), ["a3", "b3"], 3, All);
        Check("3rd download: 1st and 2nd kept", "v1,v2", Tags(keep));

        (keep, delete) = VersionHistory.Plan([P(1, "a1", "b1"), P(2, "a2", "b2")], P(3, "a3", "b3"), V(4), ["a4", "b4"], 3, All);
        Check("4th download with 3 kept: 2nd and 3rd kept", "v2,v3", Tags(keep));
        Check("4th download with 3 kept: the 1st goes", "v1", Tags(delete));
        Check("all of its files go, not a count of files", "a1|b1", string.Join("|", delete[0].Files));

        (keep, delete) = VersionHistory.Plan([P(1, "a1"), P(2, "a2"), P(3, "a3")], P(4, "a4"), V(5), ["a5"], 0, All);
        Check("0 keeps every version", "v1,v2,v3,v4", Tags(keep));
        Check("0 deletes nothing", "", Tags(delete));

        (keep, delete) = VersionHistory.Plan([P(1, "a1"), P(2, "a2")], P(3, "a3"), V(4), ["a4"], 1, All);
        Check("1 keeps only the new one", "", Tags(keep));
        Check("1: every older one goes", "v1,v2,v3", Tags(delete));

        (keep, delete) = VersionHistory.Plan([P(1, "a1"), P(2, "a2"), P(3, "a3"), P(4, "a4")], P(5, "a5"), V(6), ["a6"], 3, All);
        Check("number lowered: the extra oldest go at once", "v1,v2,v3", Tags(delete));
        Check("number lowered: the newest stay", "v4,v5", Tags(keep));

        (keep, delete) = VersionHistory.Plan([P(1, @"Old\a1"), P(2, @"Old\a2")], P(3, "a3"), V(1), [@"a1"], 3, All);
        Check("older version downloaded again: its kept copy goes", "v1", Tags(delete));
        Check("and the replaced one is kept", "v2,v3", Tags(keep));

        (keep, delete) = VersionHistory.Plan([P(1, "same.zip", "a1")], P(2, "a2"), V(3), ["same.zip"], 0, All);
        Check("a file the new version was saved over is not the old one's any more", "a1", string.Join("|", keep[0].Files));

        (keep, delete) = VersionHistory.Plan([P(1, "gone1"), P(2, "a2")], P(3, "a3"), V(4), ["a4"], 3, f => !f.StartsWith("gone", StringComparison.Ordinal));
        Check("a version deleted by hand is forgotten, and does not count", "v2,v3", Tags(keep));
        Check("nothing deleted for it", "", Tags(delete));

        (keep, delete) = VersionHistory.Plan([], P(1, "a1"), new VersionRef("v1", V(1).Date, VersionKind.ReleaseSource), ["a1-source"], 0, All);
        Check("same build with another kind is not kept twice", "", Tags(keep));

        (keep, _) = VersionHistory.Plan([], new PreviousVersion(new VersionRef("nightly", V(1).Date, VersionKind.Release), ["n1"]),
            new VersionRef("nightly", V(2).Date, VersionKind.Release), ["n2"], 0, All);
        Check("a nightly under the same tag is another build: kept", "nightly", Tags(keep));
    }

    /// <summary>
    /// The example of the request, file by file: 3 versions kept, Old on. Two
    /// files per version, a file of the user's and the README in the folder.
    /// </summary>
    private static void TestDownloadSequence()
    {
        Section("Updates keeping 3 versions, with Old");

        Site github = SiteStore.List().First(s => s.Host == "github.com");
        var repo = new Repo { Name = "Seq", SiteId = github.Id, Path = "seq/test", Folder = "Seq", KeepPrevious = true, KeepVersions = 3 };
        long id = RepoStore.Save(repo);
        Check("versions to keep saved", 3, RepoStore.Get(id)?.KeepVersions);

        string folder = DownloadPaths.RepoFolder(repo, []);
        string old = Path.Combine(folder, "Old");
        PreferenceStore.SetMoveOldVersions(true);

        List<string> Download(int n, bool redownload = false, string? tag = null)
        {
            Directory.CreateDirectory(folder);
            tag ??= $"v{n}";
            var version = new VersionRef(tag, new DateTimeOffset(2026, 1, n, 0, 0, 0, TimeSpan.Zero), VersionKind.Release);
            var pending = new List<(string Part, string Final)>();

            foreach (string name in new[] { "app.zip", "app.tar.gz" })
            {
                string final = Path.Combine(folder, DownloadPaths.LabeledName(name, tag));
                pending.Add((Touch(final + ".part", $"{tag} {name}"), final));
            }

            List<string> warnings = Downloader.Finish(RepoStore.Get(id)!, folder, pending, version, redownload);
            Touch(Path.Combine(folder, "README.md"), $"readme of {tag}");
            return warnings;
        }

        string Main() => string.Join("|", Directory.EnumerateFiles(folder).Select(Path.GetFileName).Order(StringComparer.OrdinalIgnoreCase));
        string InOld() => Directory.Exists(old)
            ? string.Join("|", Directory.EnumerateFileSystemEntries(old).Select(Path.GetFileName).Order(StringComparer.OrdinalIgnoreCase))
            : "(no Old)";
        string Kept() => string.Join(",", RepoStore.PreviousVersions(id).Select(v => v.Version.Tag));

        Check("1st download: no warnings", 0, Download(1).Count);
        Check("1st: its files in the folder", "app (v1).tar.gz|app (v1).zip|README.md", Main());
        Check("1st: no Old yet", "(no Old)", InOld());

        Touch(Path.Combine(folder, "my notes.txt"), "mine");

        Download(2);
        Check("2nd: only its files in the folder", "app (v2).tar.gz|app (v2).zip|README.md", Main());
        Check("2nd: the 1st and the user's file in Old", "app (v1).tar.gz|app (v1).zip|my notes.txt", InOld());
        Check("2nd: 1st kept", "v1", Kept());
        Check("README replaced, not moved", "readme of v2", File.ReadAllText(Path.Combine(folder, "README.md")));

        Download(3);
        Check("3rd: only its files in the folder", "app (v3).tar.gz|app (v3).zip|README.md", Main());
        Check("3rd: 1st and 2nd in Old", "app (v1).tar.gz|app (v1).zip|app (v2).tar.gz|app (v2).zip|my notes.txt", InOld());
        Check("3rd: 1st and 2nd kept", "v1,v2", Kept());

        Download(4);
        Check("4th: only its files in the folder", "app (v4).tar.gz|app (v4).zip|README.md", Main());
        Check("4th: the 1st deleted from Old, 2nd and 3rd there", "app (v2).tar.gz|app (v2).zip|app (v3).tar.gz|app (v3).zip|my notes.txt", InOld());
        Check("4th: 2nd and 3rd kept", "v2,v3", Kept());
        Check("current files recorded", @"app (v4).zip|app (v4).tar.gz",
            string.Join("|", RepoStore.Get(id)!.CurrentFiles.Select(Path.GetFileName)));
        Check("kept files recorded where they are now", DownloadPaths.ToRelative(Path.Combine(old, "app (v2).zip")),
            RepoStore.PreviousVersions(id)[0].Files[0]);

        // Redownload of the version there is: nothing moves, nothing is counted.
        Download(4, redownload: true);
        Check("redownload: folder as it was", "app (v4).tar.gz|app (v4).zip|README.md", Main());
        Check("redownload: Old as it was", "app (v2).tar.gz|app (v2).zip|app (v3).tar.gz|app (v3).zip|my notes.txt", InOld());
        Check("redownload: kept list as it was", "v2,v3", Kept());

        // An older version picked and downloaded again.
        Download(2);
        Check("2nd again: in the folder", "app (v2).tar.gz|app (v2).zip|README.md", Main());
        Check("2nd again: its old copy is not kept twice", "app (v3).tar.gz|app (v3).zip|app (v4).tar.gz|app (v4).zip|my notes.txt", InOld());
        Check("2nd again: 3rd and 4th kept", "v3,v4", Kept());

        // Without Old: previous versions stay beside the new one, the oldest still go.
        PreferenceStore.SetMoveOldVersions(false);
        Download(5);
        Check("without Old: previous version stays beside the new one",
            "app (v2).tar.gz|app (v2).zip|app (v5).tar.gz|app (v5).zip|README.md", Main());
        Check("without Old: the oldest kept version still goes", "app (v4).tar.gz|app (v4).zip|my notes.txt", InOld());
        Check("without Old: 4th and 2nd kept", "v4,v2", Kept());

        // Keep all.
        repo = RepoStore.Get(id)!;
        repo.KeepVersions = 0;
        RepoStore.Save(repo);
        Download(6);
        Check("0 = all: nothing deleted", "v4,v2,v5", Kept());

        // Keep previous versions switched off: the version replaced is deleted, the kept ones stay.
        repo = RepoStore.Get(id)!;
        repo.KeepPrevious = false;
        RepoStore.Save(repo);
        Download(7);
        Check("option off: the replaced version deleted", false, File.Exists(Path.Combine(folder, "app (v6).zip")));
        Check("option off: kept ones stay on disk", true, File.Exists(Path.Combine(folder, "app (v5).zip")));
        Check("option off: kept list untouched", "v4,v2,v5", Kept());

        // A file in use is reported, and stays listed to be deleted next time.
        repo = RepoStore.Get(id)!;
        repo.KeepPrevious = true;
        repo.KeepVersions = 2;
        RepoStore.Save(repo);
        string locked = Path.Combine(old, "app (v4).zip");
        List<string> warnings;

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            warnings = Download(8);
        }

        Check("file in use reported", 1, warnings.Count(w => w.Contains("app (v4).zip", StringComparison.Ordinal)));
        Check("file in use stays listed, first", "v4,v7", Kept());
        Download(9);
        Check("deleted on the next update", false, File.Exists(locked));
        Check("then 2 versions in all", "v8", Kept());

        PreferenceStore.SetMoveOldVersions(false);

        // Deleting the repo forgets its kept versions (the files stay, as always).
        RepoStore.Delete(id);
        Check("kept versions forgotten with the repo", 0, RepoStore.PreviousVersions(id).Count);
        Check("files stay on disk", true, File.Exists(Path.Combine(folder, "app (v9).zip")));

        // A database from before the number existed.
        long other = RepoStore.Save(new Repo { Name = "Older", SiteId = github.Id, Path = "older/db", Folder = "Older", KeepPrevious = true });

        using (SqliteConnection connection = Database.Open())
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "ALTER TABLE Repos DROP COLUMN KeepVersions";
            command.ExecuteNonQuery();
        }

        Database.Prepare();
        Check("older database gets the column, 0 = all", 0, RepoStore.Get(other)?.KeepVersions);
        RepoStore.Delete(other);

        Check("move to Old: off by default", false, new Func<bool>(() =>
        {
            PreferenceStore.Set("MoveOldVersions", "");
            return PreferenceStore.MoveOldVersions();
        })());
    }

    private static void TestSearch()
    {
        Section("Search");

        List<FilterFolder> folders =
        [
            new() { Id = 1, ParentId = null, Name = "Games" },
            new() { Id = 2, ParentId = 1, Name = "Nintendo" },
            new() { Id = 3, ParentId = 2, Name = "NES" },
            new() { Id = 4, ParentId = 1, Name = "PC" },
            new() { Id = 5, ParentId = null, Name = "Tools" }
        ];

        List<Repo> repos =
        [
            new() { Id = 1, Name = "The Legend of Zelda: A Link to the Past", Path = "snesrev/zelda3", FilterId = 3 },
            new() { Id = 2, Name = "Super Mario 64", Path = "n64decomp/sm64", FilterId = 2 },
            new() { Id = 3, Name = "Pokémon Emerald", Path = "pret/pokeemerald", FilterId = 2 },
            new() { Id = 4, Name = "OpenTTD", Path = "OpenTTD/OpenTTD", FilterId = 4 },
            new() { Id = 5, Name = "Zelda Tools", Path = "someone/zelda-tools", FilterId = 5 },
            new() { Id = 6, Name = "jq", Path = "jqlang/jq", FilterId = null },
            new() { Id = 7, Name = "GitLab CLI", Path = "gitlab-org/cli", FilterId = 5 }
        ];

        static string Ids(IEnumerable<Repo> list) => string.Join(",", list.Select(r => r.Id));
        string Find(string? query, long? folder = null) => Ids(RepoSearch.Filter(RepoSearch.InFolder(repos, folder, folders), query));

        // ----- the words -----

        Check("nothing typed", 0, RepoSearch.Words(null).Length);
        Check("only spaces", 0, RepoSearch.Words("   ").Length);
        Check("words split on any spaces", "zelda|link", string.Join("|", RepoSearch.Words("  zelda \t link ")));

        // ----- matching, under All -----

        Check("nothing typed keeps everything", "1,2,3,4,5,6,7", Find(""));
        Check("spaces only keep everything", "1,2,3,4,5,6,7", Find("   "));
        Check("part of the name", "1,5", Find("zeld"));
        Check("any case", "1,5", Find("ZELDA"));
        Check("in the repository path only", "2", Find("n64decomp"));
        Check("the path's owner/name together", "4", Find("openttd/openttd"));
        Check("words need not be side by side", "1", Find("zelda past"));
        Check("each word in name or path", "1", Find("snesrev link"));
        Check("every word must match", "", Find("zelda mario"));
        Check("accents ignored", "3", Find("pokemon"));
        Check("accent typed, plain in the path", "3", Find("pokéemerald"));
        Check("a word in the middle", "7", Find("lab"));
        Check("repo outside any folder found under All", "6", Find("jq"));
        Check("nothing matches", "", Find("doom"));

        // ----- only inside the selected folder -----

        Check("All shows every repo", "1,2,3,4,5,6,7", Find(null, null));
        Check("folder includes its subfolders", "1,2,3,4", Find(null, 1));
        Check("search inside Games leaves Tools out", "1", Find("zelda", 1));
        Check("search inside Tools leaves Games out", "5", Find("zelda", 5));
        Check("search inside Nintendo reaches NES below it", "1", Find("zelda", 2));
        Check("search inside NES only", "1", Find("snes", 3));
        Check("found elsewhere, not in this folder", "", Find("openttd", 2));
        Check("repo without a folder only under All", "", Find("jq", 1));

        // The order is the list's job: the search keeps what it was given.
        Check("order kept for the sort", "5,1", Ids(RepoSearch.Filter([repos[4], repos[0]], "zelda")));
    }

    /// <summary>
    /// The lines on screen in the repo list. Every line must show up at some
    /// scroll position: with one control per repo, lines past 32,767 pixels
    /// (about the 528th) never did.
    /// </summary>
    private static void TestListWindow()
    {
        Section("Repo list lines on screen");

        const int Line = 62;
        const int View = 700;

        Check("all fits: no scrolling", 0, ListWindow.MaxOffset(10, Line, View));
        Check("1,368 repos: scrolls to the last line at the bottom", (1368 * Line) - View, ListWindow.MaxOffset(1368, Line, View));
        Check("past 65,535 pixels", true, ListWindow.MaxOffset(1368, Line, View) > 65535);
        Check("clamped above", 0, ListWindow.Clamp(-500, 1368, Line, View));
        Check("clamped below", ListWindow.MaxOffset(1368, Line, View), ListWindow.Clamp(int.MaxValue, 1368, Line, View));
        Check("empty list: nothing to scroll", 0, ListWindow.Clamp(300, 0, Line, View));
        Check("first line and how much of it is above the top", (2, 6), ListWindow.FirstVisible(130, Line));
        Check("rows for a view: the lines that fit, plus one", 13, ListWindow.RowsNeeded(Line, View));
        Check("rows for an exact fit", 3, ListWindow.RowsNeeded(Line, 124));
        Check("rows for a view of nothing", 1, ListWindow.RowsNeeded(Line, 0));

        // Page by page from the top to the bottom, as PageDown does; also at
        // every offset of one line, the rows cover the whole view.
        const int Count = 1368;
        var seen = new HashSet<int>();
        bool covered = true;

        for (int offset = 0; ; offset = ListWindow.Clamp(offset + View, Count, Line, View))
        {
            (int first, int shift) = ListWindow.FirstVisible(offset, Line);
            int rows = ListWindow.RowsNeeded(Line, View);

            for (int i = 0; i < rows && first + i < Count; i++)
            {
                int top = (i * Line) - shift;

                if (top < View && top + Line > 0)
                {
                    seen.Add(first + i);
                }
            }

            if (offset == ListWindow.MaxOffset(Count, Line, View))
            {
                break;
            }
        }

        for (int offset = 0; offset < Line; offset++)
        {
            (_, int shift) = ListWindow.FirstVisible(offset, Line);
            covered &= (ListWindow.RowsNeeded(Line, View) * Line) - shift >= View;
        }

        Check("paging through 1,368 lines shows every one of them", Count, seen.Count);
        Check("the last line is among them", true, seen.Contains(Count - 1));
        Check("at any offset the rows cover the whole view", true, covered);
    }

    private static void TestSorting()
    {
        Section("List order");

        static DateTimeOffset Day(int month, int day) => new(2026, month, day, 0, 0, 0, TimeSpan.Zero);
        var v1 = new VersionRef("v1", Day(10, 1), VersionKind.Release);
        var v2 = new VersionRef("v2", Day(11, 1), VersionKind.Release);
        DateTimeOffset checkedAt = Day(11, 14);

        // Names chosen so name order differs from both group orders.
        List<Repo> repos =
        [
            new() { Id = 1, Name = "delta", Latest = v2, Current = v2, CheckedAt = checkedAt },      // up to date
            new() { Id = 2, Name = "Bravo", Latest = v2, CheckedAt = checkedAt },                    // not downloaded
            new() { Id = 3, Name = "echo", Latest = v2, Current = v1, CheckedAt = checkedAt },       // update
            new() { Id = 4, Name = "alpha", Latest = v2, Current = v2, CheckedAt = checkedAt },      // up to date
            new() { Id = 5, Name = "Charlie", Latest = v2, Current = v1, CheckedAt = checkedAt },    // update
            new() { Id = 6, Name = "foxtrot" },                                                      // never checked, not downloaded
            new() { Id = 7, Name = "alpha", Latest = v2, Current = v2, CheckedAt = checkedAt }       // same name as 4
        ];

        static string Names(List<Repo> list) => string.Join(",", list.Select(r => $"{r.Name}#{r.Id}"));

        Check("by name, any case, ties by id",
            "alpha#4,alpha#7,Bravo#2,Charlie#5,delta#1,echo#3,foxtrot#6",
            Names(RepoSorting.Sort(repos, RepoOrder.Name)));

        Check("not downloaded on top, then by name",
            "Bravo#2,foxtrot#6,alpha#4,alpha#7,Charlie#5,delta#1,echo#3",
            Names(RepoSorting.Sort(repos, RepoOrder.NotDownloadedFirst)));

        Check("updates on top, then by name",
            "Charlie#5,echo#3,alpha#4,alpha#7,Bravo#2,delta#1,foxtrot#6",
            Names(RepoSorting.Sort(repos, RepoOrder.UpdatesFirst)));

        // A pin on an older version is an update too: the button says Update.
        repos[0].Selected = v1;
        Check("pinned to another version counts as update", "Charlie#5,delta#1,echo#3",
            string.Join(",", RepoSorting.Sort(repos, RepoOrder.UpdatesFirst).Take(3).Select(r => $"{r.Name}#{r.Id}")));

        // Selected on Top: delta is pinned below Latest, foxtrot pinned with no
        // Latest known yet; echo's pin is Latest itself, which changes nothing.
        repos[2].Selected = v2;
        repos[5].Selected = v1;
        Check("selected on top: pins other than Latest first, then by name",
            "delta#1,foxtrot#6,alpha#4,alpha#7,Bravo#2,Charlie#5,echo#3",
            Names(RepoSorting.Sort(repos, RepoOrder.SelectedFirst)));
        // "b" pins Latest's own source archive: the same version, so not on top.
        Check("selected on top: Latest as its source archive is still Latest", "a,b",
            string.Join(",", RepoSorting.Sort(
                [
                    new Repo { Id = 20, Name = "b", Latest = v2, Selected = new VersionRef("v2", Day(11, 1), VersionKind.ReleaseSource) },
                    new Repo { Id = 21, Name = "a", Latest = v2 }
                ],
                RepoOrder.SelectedFirst).Select(r => r.Name)));
        Check("selected on top: saved and read back", RepoOrder.SelectedFirst, RepoSorting.Parse(RepoOrder.SelectedFirst.ToString()));
        repos[2].Selected = null;
        repos[5].Selected = null;

        // ----- leading articles -----

        Check("The", "Legend of Zelda", RepoSorting.SortKey("The Legend of Zelda"));
        Check("A", "Link to the Past", RepoSorting.SortKey("A Link to the Past"));
        Check("An", "Untitled Story", RepoSorting.SortKey("An Untitled Story"));
        Check("lower case", "witcher", RepoSorting.SortKey("the witcher"));
        Check("extra spaces", "Matrix", RepoSorting.SortKey("  The   Matrix"));
        Check("word that starts like an article", "Anachronox", RepoSorting.SortKey("Anachronox"));
        Check("Theme is not The", "Theme Hospital", RepoSorting.SortKey("Theme Hospital"));
        Check("Atari is not A", "Atari", RepoSorting.SortKey("Atari"));
        Check("only the first article", "A Team", RepoSorting.SortKey("The A Team"));
        Check("name that is only the article", "The", RepoSorting.SortKey("The"));
        Check("article and spaces only", "The", RepoSorting.SortKey("The   "));

        List<Repo> games =
        [
            new() { Id = 1, Name = "The Legend of Zelda" },
            new() { Id = 2, Name = "Metroid" },
            new() { Id = 3, Name = "A Link to the Past" },
            new() { Id = 4, Name = "Anachronox" },
            new() { Id = 5, Name = "Theme Hospital" },
            new() { Id = 6, Name = "An Untitled Story" },
            new() { Id = 7, Name = "Breath" },
            new() { Id = 8, Name = "Witcher" },
            new() { Id = 9, Name = "the witcher" }
        ];

        Check("by name, articles ignored",
            "Anachronox|Breath|The Legend of Zelda|A Link to the Past|Metroid|Theme Hospital|An Untitled Story|the witcher|Witcher",
            string.Join("|", RepoSorting.Sort(games, RepoOrder.Name).Select(r => r.Name)));

        // Groups still come first; the article rule applies inside each group.
        games[2].Latest = v2;   // A Link to the Past: not downloaded
        games[0].Latest = v2;   // The Legend of Zelda: not downloaded
        foreach (Repo r in games.Where(r => r.Latest is null))
        {
            r.Latest = v2;
            r.Current = v2;
        }

        Check("not downloaded on top, articles ignored inside the group",
            "The Legend of Zelda|A Link to the Past|Anachronox",
            string.Join("|", RepoSorting.Sort(games, RepoOrder.NotDownloadedFirst).Take(3).Select(r => r.Name)));

        Check("saved order read back", RepoOrder.UpdatesFirst, RepoSorting.Parse("UpdatesFirst"));
        Check("unknown saved order falls back to Name", RepoOrder.Name, RepoSorting.Parse("Sideways"));
        Check("nothing saved yet", RepoOrder.Name, RepoSorting.Parse(null));

        Check("no preference yet", null, PreferenceStore.Get(PreferenceStore.OrderKey));
        PreferenceStore.Set(PreferenceStore.OrderKey, "NotDownloadedFirst");
        PreferenceStore.Set(PreferenceStore.OrderKey, "UpdatesFirst");
        Check("preference overwritten, not duplicated", "UpdatesFirst", PreferenceStore.Get(PreferenceStore.OrderKey));
    }

    private static void TestRateLimits()
    {
        Section("Requests left");

        // Samples taken from the real answers of each site.
        RateLimit? github = RateLimits.ParseGitHub("""
            {"resources":{"core":{"limit":60,"remaining":58,"reset":1794681468,"used":2},
                          "search":{"limit":10,"remaining":10,"reset":1794678163,"used":0}},
             "rate":{"limit":60,"remaining":58,"reset":1794681468,"used":2}}
            """);
        Check("GitHub limit", 60, github?.Limit);
        Check("GitHub remaining", 58, github?.Remaining);
        Check("GitHub reset", DateTimeOffset.FromUnixTimeSeconds(1794681468), github?.Reset);
        Check("GitHub answer without core", null, RateLimits.ParseGitHub("""{"message":"x"}"""));

        var now = new DateTimeOffset(2026, 11, 14, 15, 0, 0, TimeSpan.Zero);

        static Func<string, string?> Headers(params (string Name, string Value)[] headers) =>
            name => headers.FirstOrDefault(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase)).Value;

        RateLimit? gitlab = RateLimits.FromHeaders(Headers(
            ("ratelimit-limit", "500"), ("ratelimit-name", "throttle_unauthenticated_api"),
            ("ratelimit-observed", "1"), ("ratelimit-remaining", "499"), ("ratelimit-reset", "1794678120")), now);
        Check("GitLab limit", 500, gitlab?.Limit);
        Check("GitLab remaining", 499, gitlab?.Remaining);
        Check("GitLab reset (Unix time)", DateTimeOffset.FromUnixTimeSeconds(1794678120), gitlab?.Reset);

        RateLimit? codeberg = RateLimits.FromHeaders(Headers(
            ("ratelimit-policy", "\"baseline\";q=2000;w=600"), ("ratelimit", "\"baseline\";r=1999;t=600")), now);
        Check("Codeberg quota", 2000, codeberg?.Limit);
        Check("Codeberg remaining", 1999, codeberg?.Remaining);
        Check("Codeberg reset (seconds from now)", now.AddSeconds(600), codeberg?.Reset);

        RateLimit? spaced = RateLimits.FromHeaders(Headers(
            ("RateLimit-Policy", "\"default\"; q=100; w=60"), ("RateLimit", "\"default\"; r=0; t=12")), now);
        Check("spaces in structured fields", "0/100", $"{spaced?.Remaining}/{spaced?.Limit}");

        Check("no limit headers", null, RateLimits.FromHeaders(Headers(("Content-Type", "application/json")), now));
        Check("reset given as seconds", now.AddSeconds(30),
            RateLimits.FromHeaders(Headers(("X-RateLimit-Limit", "10"), ("X-RateLimit-Remaining", "9"), ("X-RateLimit-Reset", "30")), now)?.Reset);

        Site gh = SampleSites[0];
        Site gl = SampleSites[1];
        Site cb = SampleSites[2];
        Check("GitHub limit address", "https://api.github.com/rate_limit", ApiUrls.RateLimit(gh));
        Check("GitLab limit address", "https://gitlab.com/api/v4/version", ApiUrls.RateLimit(gl));
        Check("Codeberg limit address", "https://codeberg.org/api/v1/version", ApiUrls.RateLimit(cb));
    }

    private static void TestSiteDetection()
    {
        Section("Self-hosted servers");

        static Func<string, string?> NoHeaders() => _ => null;
        static Func<string, string?> GitLabHeaders() =>
            name => string.Equals(name, "X-Gitlab-Meta", StringComparison.OrdinalIgnoreCase)
                ? """{"correlation_id":"01M7DQD87Q7JVJPZZ0D2YRTMZV","version":"1"}"""
                : null;

        // Answers recorded from the real servers.
        Check("Forgejo version answer", true, SiteDetection.IsGitea(200, """{"version":"17.0.1+gitea-1.22.0"}"""));
        Check("Gitea version answer", true, SiteDetection.IsGitea(200, """{"version":"1.22.0"}"""));
        Check("plain-text 404 is not Forgejo", false, SiteDetection.IsGitea(404, "Not found."));
        Check("GitLab sign-in page is not Forgejo", false,
            SiteDetection.IsGitea(200, """<html><body>You are being <a href="https://gitlab.gnome.org/users/sign_in">redirected</a>.</body></html>"""));
        Check("JSON without a version is not Forgejo", false, SiteDetection.IsGitea(200, """{"error":"Not Found"}"""));

        Check("GitLab 401 with its header", true, SiteDetection.IsGitLab(401, GitLabHeaders(), """{"message":"401 Unauthorized"}"""));
        Check("GitLab version answer", true, SiteDetection.IsGitLab(200, NoHeaders(), """{"version":"18.5.0","revision":"abc123"}"""));
        Check("GitHub 404 is not GitLab", false, SiteDetection.IsGitLab(404, NoHeaders(), """{"error":"Not Found"}"""));
        Check("Forgejo 404 is not GitLab", false, SiteDetection.IsGitLab(404, NoHeaders(), "Not found."));

        Check("GitHub Enterprise meta", true,
            SiteDetection.IsGitHubEnterprise(200, """{"verifiable_password_authentication":true,"installed_version":"3.14.2"}"""));
        Check("github.com meta is not Enterprise", false,
            SiteDetection.IsGitHubEnterprise(200, """{"verifiable_password_authentication":true}"""));

        // ----- sites that come and go with their repos -----

        Site added = SiteStore.Add("git.example.org", ApiType.Gitea);
        Check("detected site saved", "git.example.org/Gitea", $"{added.Host}/{added.Type}");
        Check("adding it again gives the same site", added.Id, SiteStore.Add("git.example.org", ApiType.Gitea).Id);
        Check("address of a detected site", "git.example.org", RepoAddress.Parse("https://git.example.org/owner/app", SiteStore.List())?.Site?.Host);

        long repoId = RepoStore.Save(new Repo { Name = "Uses it", SiteId = added.Id, Path = "owner/app", Folder = "Uses it" });
        SiteStore.RemoveUnused();
        Check("site in use stays", true, SiteStore.List().Any(s => s.Id == added.Id));

        RepoStore.Delete(repoId);
        SiteStore.RemoveUnused();
        Check("unused detected site goes away", false, SiteStore.List().Any(s => s.Id == added.Id));
        Check("built-in sites always stay", 3,
            SiteStore.List().Count(s => SiteStore.BuiltInHosts.Contains(s.Host, StringComparer.OrdinalIgnoreCase)));
    }

    private static void TestAutoCheck()
    {
        Section("Automatic check");

        Check("choices in the drop down", "30 minutes|1 hour|3 hours|6 hours|12 hours",
            string.Join("|", AutoCheck.Intervals.Select(i => i.Text)));
        Check("saved period read back", 180, AutoCheck.ParseMinutes("180"));
        Check("period that is not a choice", AutoCheck.DefaultMinutes, AutoCheck.ParseMinutes("45"));
        Check("nothing saved", AutoCheck.DefaultMinutes, AutoCheck.ParseMinutes(null));

        var now = new DateTimeOffset(2026, 11, 14, 18, 0, 0, TimeSpan.Zero);
        Check("never checked: due right away", true, AutoCheck.IsDue(null, 30, now));
        Check("29 minutes after a full check: not yet", false, AutoCheck.IsDue(now.AddMinutes(-29), 30, now));
        Check("30 minutes after: due", true, AutoCheck.IsDue(now.AddMinutes(-30), 30, now));
        Check("11 hours into 12: not yet", false, AutoCheck.IsDue(now.AddHours(-11), 720, now));
        Check("overdue after the app was closed", true, AutoCheck.IsDue(now.AddDays(-2), 360, now));
        Check("next check counts from the last full check", now.AddHours(3), AutoCheck.NextCheck(now, 180, now.AddMinutes(10)));

        // ----- saved settings -----

        AutoCheckSettings fresh = AutoCheck.Load();
        Check("off by default", false, fresh.Enabled);
        Check("1 hour by default", 60, fresh.Minutes);
        Check("never checked by default", null, fresh.LastFullCheck);

        AutoCheck.SetEnabled(true);
        AutoCheck.SetMinutes(360);
        AutoCheck.SetLastFullCheck(now);
        AutoCheckSettings saved = AutoCheck.Load();
        Check("switched on", true, saved.Enabled);
        Check("period saved", 360, saved.Minutes);
        Check("last full check saved", now, saved.LastFullCheck);

        AutoCheck.SetMinutes(999);
        Check("a period that is not a choice is not saved as such", AutoCheck.DefaultMinutes, AutoCheck.Load().Minutes);

        AutoCheck.SetEnabled(false);
        Check("switched off again", false, AutoCheck.Load().Enabled);

        // ----- "Show source code when downloading" -----

        Check("show source code: off by default", false, PreferenceStore.ShowSource());
        PreferenceStore.SetShowSource(true);
        Check("show source code: on", true, PreferenceStore.ShowSource());
        PreferenceStore.SetShowSource(false);
        Check("show source code: off again", false, PreferenceStore.ShowSource());

        // ----- "Download README.md" -----

        Check("download README.md: on by default", true, PreferenceStore.DownloadReadme());
        PreferenceStore.SetDownloadReadme(false);
        Check("download README.md: off", false, PreferenceStore.DownloadReadme());
        PreferenceStore.SetDownloadReadme(true);
        Check("download README.md: on again", true, PreferenceStore.DownloadReadme());
    }

    // ---------- Git-Sync's own updates ----------

    private static void TestConnector() => TestConnectorAsync().GetAwaiter().GetResult();

    /// <summary>
    /// 192.0.2.1 (TEST-NET-1, never routed) stands for an IPv6 address that does
    /// not answer: Windows waits 21 s on it before giving up, as it does on a
    /// network whose IPv6 lets nothing through.
    /// </summary>
    private static async Task TestConnectorAsync()
    {
        Section("Connecting: IPv6 and IPv4 side by side");

        IPAddress v6a = IPAddress.Parse("2001:db8::1"), v6b = IPAddress.Parse("2001:db8::2");
        IPAddress v4a = IPAddress.Parse("198.51.100.1"), v4b = IPAddress.Parse("198.51.100.2");
        static string Show(List<IPAddress> order) => string.Join(" ", order);

        Check("IPv6 listed first: the families alternate", "2001:db8::1 198.51.100.1 2001:db8::2 198.51.100.2",
            Show(Connector.Order([v6a, v6b, v4a, v4b], null)));
        Check("IPv4 listed first stays first", "198.51.100.1 2001:db8::1 198.51.100.2",
            Show(Connector.Order([v4a, v6a, v4b], null)));
        Check("the family that worked before goes first", "198.51.100.1 2001:db8::1 198.51.100.2 2001:db8::2",
            Show(Connector.Order([v6a, v6b, v4a, v4b], AddressFamily.InterNetwork)));
        Check("a remembered family the host lacks changes nothing", "2001:db8::1 2001:db8::2",
            Show(Connector.Order([v6a, v6b], AddressFamily.InterNetwork)));
        Check("no addresses, nothing to try", 0, Connector.Order([], null).Count);

        IPAddress silent = IPAddress.Parse("192.0.2.1");
        using var site = new TcpListener(IPAddress.Loopback, 0);
        site.Start();
        int port = ((IPEndPoint)site.LocalEndpoint).Port;

        var clock = Stopwatch.StartNew();

        using (Socket socket = await Connector.ConnectFirstAsync([silent, IPAddress.Loopback], port, Connector.AttemptDelay, default))
        {
            Check("a silent first address: the next one connects", IPAddress.Loopback, ((IPEndPoint)socket.RemoteEndPoint!).Address);
            Require($"…without waiting it out ({clock.ElapsedMilliseconds} ms)", clock.ElapsedMilliseconds < 3000);
        }

        // Windows takes about 2 s to report a refusal on loopback, so a delay of
        // 30 s shows the next address starting on the failure, not on the clock.
        using var ipv6Only = new TcpListener(IPAddress.IPv6Loopback, 0);
        ipv6Only.Start();
        int port6 = ((IPEndPoint)ipv6Only.LocalEndpoint).Port;
        clock.Restart();

        using (Socket socket = await Connector.ConnectFirstAsync(
            [IPAddress.Loopback, IPAddress.IPv6Loopback], port6, TimeSpan.FromSeconds(30), default))
        {
            Check("a refused first address: the next one connects", IPAddress.IPv6Loopback, ((IPEndPoint)socket.RemoteEndPoint!).Address);
            Require($"…as soon as the first fails ({clock.ElapsedMilliseconds} ms)", clock.ElapsedMilliseconds < 15000);
        }

        ipv6Only.Stop();

        try
        {
            using Socket socket = await Connector.ConnectFirstAsync([IPAddress.Loopback], port6, Connector.AttemptDelay, default);
            Check("nothing listening: the refusal comes out", "refused", "connected");
        }
        catch (SocketException error)
        {
            Check("nothing listening: the refusal comes out", SocketError.ConnectionRefused, error.SocketErrorCode);
        }

        using (var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(500)))
        {
            try
            {
                using Socket socket = await Connector.ConnectFirstAsync([silent], port, Connector.AttemptDelay, deadline.Token);
                Check("a deadline cancels with its own token", "cancelled", "connected");
            }
            catch (OperationCanceledException error)
            {
                Check("a deadline cancels with its own token", true, error.CancellationToken == deadline.Token);
            }
            catch (SocketException error)
            {
                Check("a deadline cancels with its own token", "cancelled", $"192.0.2.1 is not silent here ({error.SocketErrorCode})");
            }
        }

        // The handler turns a ConnectTimeout into "could not connect" only when
        // the cancellation carries its token; anything else reads as a broken connection.
        using (var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(1),
            ConnectCallback = (context, cancel) => Connector.ConnectAsync(context.DnsEndPoint, cancel)
        })
        using (var client = new HttpClient(handler))
        {
            try
            {
                using HttpResponseMessage response = await client.GetAsync($"http://192.0.2.1:{port}/");
                Check("ConnectTimeout through the handler", "timeout", $"{(int)response.StatusCode}");
            }
            catch (OperationCanceledException error)
            {
                Check("ConnectTimeout through the handler is told apart", true, ApiClient.ConnectTimedOut(error));
            }
            catch (HttpRequestException error)
            {
                Check("ConnectTimeout through the handler is told apart", "timeout", error.Message);
            }
        }

        Check("a deadline of the request itself is not a connect timeout", false,
            ApiClient.ConnectTimedOut(new OperationCanceledException()));

        // Git-Sync's own client: "localhost" is ::1 then 127.0.0.1 on Windows,
        // and only the IPv4 one answers here. A listener of its own: the one
        // above still holds the connections of the checks before, never accepted.
        using var web = new TcpListener(IPAddress.Loopback, 0);
        web.Start();
        int webPort = ((IPEndPoint)web.LocalEndpoint).Port;

        Task server = Task.Run(async () =>
        {
            using TcpClient caller = await web.AcceptTcpClientAsync();
            NetworkStream stream = caller.GetStream();
            string request = string.Empty;
            byte[] buffer = new byte[4096];

            // Up to the blank line that ends a GET's headers.
            while (!request.Contains("\r\n\r\n"))
            {
                int read = await stream.ReadAsync(buffer);

                if (read == 0)
                {
                    break;
                }

                request += System.Text.Encoding.ASCII.GetString(buffer, 0, read);
            }

            byte[] answer = "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray();
            await stream.WriteAsync(answer);
        });

        var local = new Site { Host = $"http://localhost:{webPort}", Type = ApiType.Gitea };

        using (var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        using (HttpResponseMessage response = await ApiClient.SendAsync(local, $"http://localhost:{webPort}/x", "*/*", limit.Token))
        {
            Check("Git-Sync's client reaches the address that answers", 200, (int)response.StatusCode);
        }

        await server;
        Check("…and goes through the side-by-side connect, which remembers IPv4",
            AddressFamily.InterNetwork, Connector.Remembered("localhost"));
        Check("…for that host only", null, Connector.Remembered("example.com"));
    }

    private static void TestAppUpdate()
    {
        Section("Git-Sync updates");

        Check("tag with v", new Version(1, 0, 6, 0), AppUpdate.ParseTag("v1.0.6"));
        Check("tag without v", new Version(1, 0, 6, 0), AppUpdate.ParseTag("1.0.6"));
        Check("capital V and spaces", new Version(2, 1, 0, 0), AppUpdate.ParseTag("  V2.1.0 "));
        Check("pre-release suffix dropped", new Version(1, 1, 0, 0), AppUpdate.ParseTag("v1.1.0-beta.2"));
        Check("build metadata dropped", new Version(1, 0, 7, 0), AppUpdate.ParseTag("v1.0.7+abc123"));
        Check("two parts", new Version(1, 1, 0, 0), AppUpdate.ParseTag("v1.1"));
        Check("one part", new Version(2, 0, 0, 0), AppUpdate.ParseTag("v2"));
        Check("four parts kept", new Version(1, 0, 6, 1), AppUpdate.ParseTag("v1.0.6.1"));
        Check("not a version", null, AppUpdate.ParseTag("nightly"));
        Check("empty", null, AppUpdate.ParseTag(""));
        Check("nothing saved", null, AppUpdate.ParseTag(null));

        // "v1.1" against the exe's four parts: without filling in the missing
        // ones, Version says 1.1 < 1.1.0.0 and an equal version looks older.
        Check("v1.1 is the same version as 1.1.0.0", false,
            AppUpdate.ShouldOffer(Release("v1.1"), new Version(1, 1, 0, 0), null));

        Check("shown as three parts", "1.0.5", AppUpdate.Display(new Version(1, 0, 5, 0)));
        Check("fourth part shown when used", "1.0.5.2", AppUpdate.Display(new Version(1, 0, 5, 2)));

        var running = new Version(1, 0, 5, 0);
        Check("newer: offered", true, AppUpdate.ShouldOffer(Release("v1.0.6"), running, null));
        Check("same: not offered", false, AppUpdate.ShouldOffer(Release("v1.0.5"), running, null));
        Check("older: not offered", false, AppUpdate.ShouldOffer(Release("v1.0.4"), running, null));
        Check("10 after 9, not by text", true, AppUpdate.ShouldOffer(Release("v1.0.10"), new Version(1, 0, 9, 0), null));
        Check("skipped: not offered", false, AppUpdate.ShouldOffer(Release("v1.0.6"), running, new Version(1, 0, 6, 0)));
        Check("newer than the skipped one: offered again", true,
            AppUpdate.ShouldOffer(Release("v1.0.7"), running, new Version(1, 0, 6, 0)));
        Check("older than the skipped one: stays skipped", false,
            AppUpdate.ShouldOffer(Release("v1.0.6"), running, new Version(1, 0, 7, 0)));
        Check("skipped one older than this exe: newer still offered", true,
            AppUpdate.ShouldOffer(Release("v1.0.6"), running, new Version(1, 0, 3, 0)));

        // ----- the answer of releases/latest -----

        AppRelease? latest = AppUpdate.ParseLatest("""
            { "tag_name": "v1.0.6", "name": "Git-Sync 1.0.6", "draft": false, "prerelease": false,
              "html_url": "https://github.com/Chrius/Git-Sync/releases/tag/v1.0.6" }
            """);
        Check("tag read", "v1.0.6", latest?.Tag);
        Check("version read", new Version(1, 0, 6, 0), latest?.Version);
        Check("draft ignored", null, AppUpdate.ParseLatest("""{ "tag_name": "v1.0.6", "draft": true }"""));
        Check("pre-release ignored", null, AppUpdate.ParseLatest("""{ "tag_name": "v1.0.6", "prerelease": true }"""));
        Check("tag that is not a version", null, AppUpdate.ParseLatest("""{ "tag_name": "nightly" }"""));
        Check("no tag", null, AppUpdate.ParseLatest("""{ "message": "Not Found" }"""));

        // ----- saved settings -----

        Check("check on launch: on by default", true, AppUpdate.Enabled());
        AppUpdate.SetEnabled(false);
        Check("check on launch: off", false, AppUpdate.Enabled());
        AppUpdate.SetEnabled(true);
        Check("check on launch: on again", true, AppUpdate.Enabled());

        Check("nothing skipped by default", null, AppUpdate.Skipped());
        AppUpdate.Skip(Release("v1.0.6"));
        Check("skipped version saved", new Version(1, 0, 6, 0), AppUpdate.Skipped());
        AppUpdate.Skip(Release("v1.0.7"));
        Check("skipping a newer one replaces it", new Version(1, 0, 7, 0), AppUpdate.Skipped());

        static AppRelease Release(string tag) => new(tag, AppUpdate.ParseTag(tag)!);
    }

    // ---------- export and import ----------

    /// <summary>Keeps every report as it is made; Progress&lt;T&gt; would deliver them later, on another thread.</summary>
    private sealed class ProgressRecorder : IProgress<int>
    {
        public List<int> Values { get; } = [];

        public void Report(int value) => Values.Add(value);
    }

    private static void TestDownloadQueue() => TestDownloadQueueAsync().GetAwaiter().GetResult();

    /// <summary>
    /// Update All's queue: jobs stand for downloads, each held open until the
    /// test lets it end, so what runs when can be checked step by step.
    /// </summary>
    private static async Task TestDownloadQueueAsync()
    {
        Section("Download queue (Update All)");

        static async Task<bool> Until(Func<bool> condition)
        {
            for (var clock = Stopwatch.StartNew(); clock.Elapsed < TimeSpan.FromSeconds(5); await Task.Delay(10))
            {
                if (condition())
                {
                    return true;
                }
            }

            return condition();
        }

        var log = new List<string>();
        var gates = new Dictionary<string, TaskCompletionSource>();
        int running = 0;
        int most = 0;
        int drained = 0;

        string Log()
        {
            lock (log)
            {
                return string.Join("|", log);
            }
        }

        TaskCompletionSource Gate(string job)
        {
            lock (gates)
            {
                if (!gates.TryGetValue(job, out TaskCompletionSource? gate))
                {
                    gates[job] = gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                return gate;
            }
        }

        async Task Job(string job, CancellationToken cancel)
        {
            lock (log)
            {
                log.Add("start " + job);
                most = Math.Max(most, Interlocked.Increment(ref running));
            }

            try
            {
                await Gate(job).Task.WaitAsync(cancel);
            }
            finally
            {
                lock (log)
                {
                    Interlocked.Decrement(ref running);
                    log.Add("end " + job);
                }
            }
        }

        var queue = new DownloadQueue<string>(Job);
        queue.Drained += (_, _) => Interlocked.Increment(ref drained);
        using var cancel = new CancellationTokenSource();

        queue.Add("A", cancel.Token);
        queue.Add("B", cancel.Token);
        queue.Add("C", cancel.Token);
        Check("the first starts at once", "start A", Log());
        Check("the others wait", 2, queue.Waiting);
        Check("remaining counts the one running", 3, queue.Remaining);

        Gate("A").SetResult();
        Require("the next starts when the first ends", await Until(() => Log().EndsWith("start B", StringComparison.Ordinal)));

        // Update clicked on another repo while B downloads: it joins the end.
        queue.Add("D", cancel.Token);
        Check("added while one runs: waits behind the rest", 2, queue.Waiting);
        Gate("B").SetResult();
        Gate("C").SetResult();
        Require("everything added runs", await Until(() => Log().EndsWith("start D", StringComparison.Ordinal)));
        Check("in the order added, one after another", "start A|end A|start B|end B|start C|end C|start D", Log());
        Check("not drained while one still runs", 0, drained);

        Gate("D").SetResult();
        Require("drained after the last", await Until(() => drained == 1));
        Check("never two at once", 1, most);
        Check("nothing remaining", 0, queue.Remaining);

        Gate("E").SetResult();
        queue.Add("E", cancel.Token);
        Require("added after draining: runs again", await Until(() => drained == 2));
        Check("the late one ran", true, Log().EndsWith("start E|end E", StringComparison.Ordinal));

        // Closing the window: the running job stops, the waiting ones never start.
        lock (log)
        {
            log.Clear();
        }

        queue.Add("F", cancel.Token);
        queue.Add("G", cancel.Token);
        await cancel.CancelAsync();
        Require("cancelled: the queue empties", await Until(() => queue.Remaining == 0));
        Check("cancelled: the running one ends, the waiting one never starts", "start F|end F", Log());
        Check("cancelled: no drained", 2, drained);
    }

    private static void TestDataTransfer()
    {
        Section("Export and import");

        // ----- export, from lists: nothing read from or written to the database -----

        var sites = new List<Site>
        {
            new() { Id = 1, Host = "github.com", Type = ApiType.GitHub, Token = "ghp_secret123" },
            new() { Id = 9, Host = "git.example.org", Type = ApiType.Gitea, Token = "gitea-secret" }
        };

        var filters = new List<FilterFolder>
        {
            new() { Id = 1, Name = "Games" },
            new() { Id = 2, ParentId = 1, Name = "PC" },
            new() { Id = 3, ParentId = 1, Name = "Empty" },
            new() { Id = 4, Name = "Tools" },
            new() { Id = 5, ParentId = 4, Name = "Unused" }
        };

        var pinDate = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var repos = new List<Repo>
        {
            new()
            {
                Id = 1, Name = "Pokémon Tool", SiteId = 1, Path = "o/poke", Folder = "Poke", FilterId = 2, KeepPrevious = true, KeepVersions = 3,
                IgnoreOnRefresh = true,
                Selected = new VersionRef("v1.2", pinDate, VersionKind.Release),
                Current = new VersionRef("v1.1-current", null, VersionKind.Release),
                Latest = new VersionRef("v1.3-latest", null, VersionKind.Release),
                CurrentFiles = [@"Apps\Games\PC\Poke\downloaded-file.zip"]
            },
            new() { Id = 2, Name = "Loose", SiteId = 9, Path = "me/loose", Folder = "Loose - loose" },
            new() { Id = 3, Name = "Orphan", SiteId = 77, Path = "x/y", Folder = "Orphan" }
        };

        ExportData collected = DataTransfer.Collect(filters, repos, sites);
        Check("only folders holding repos are exported, with their parents", @"Games|Games\PC",
            string.Join("|", collected.Filters.Select(p => string.Join("\\", p))));

        // The tree's order, not the ids' (folders moved up and down) nor the
        // repos' (alphabetical: Alpha would bring NES first).
        var ordered = new List<FilterFolder>
        {
            new() { Id = 1, Name = "Games" },
            new() { Id = 2, ParentId = 1, Name = "NES", SortOrder = 2 },
            new() { Id = 3, ParentId = 1, Name = "PC", SortOrder = 1 },
            new() { Id = 4, ParentId = 1, Name = "Sony", SortOrder = 0 },
            new() { Id = 5, ParentId = 2, Name = "Hacks" }
        };

        var orderedRepos = new List<Repo>
        {
            new() { Id = 1, Name = "Alpha", SiteId = 1, Path = "o/alpha", Folder = "Alpha", FilterId = 5 },
            new() { Id = 2, Name = "Beta", SiteId = 1, Path = "o/beta", Folder = "Beta", FilterId = 3 },
            new() { Id = 3, Name = "Gamma", SiteId = 1, Path = "o/gamma", Folder = "Gamma", FilterId = 4 }
        };

        Check("folders exported in the tree's order", @"Games|Games\Sony|Games\PC|Games\NES|Games\NES\Hacks",
            string.Join("|", DataTransfer.Collect(ordered, orderedRepos, sites).Filters.Select(p => string.Join("\\", p))));
        Check("repos exported; one whose site is gone is left out", "Pokémon Tool|Loose",
            string.Join("|", collected.Repos.Select(r => r.Name)));

        string json = DataTransfer.ToJson(collected, new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
        Check("no token in the file", false, json.Contains("secret", StringComparison.Ordinal));
        Check("nothing about this install's downloads or checks", false,
            json.Contains("downloaded-file", StringComparison.Ordinal)
            || json.Contains("current", StringComparison.Ordinal)
            || json.Contains("latest", StringComparison.Ordinal));
        Check("names stay readable", true, json.Contains("\"Pokémon Tool\"", StringComparison.Ordinal));
        Check("folder paths written with a slash", true, json.Contains("\"filter\": \"Games/PC\"", StringComparison.Ordinal));
        Check("no folder written as empty text", true, json.Contains("\"filter\": \"\"", StringComparison.Ordinal));

        ExportData read = DataTransfer.Parse(json);
        ExportedRepo poke = read.Repos[0];
        Check("round trip: site", "github.com", poke.Host);
        Check("round trip: API", ApiType.GitHub, poke.Type);
        Check("round trip: repository", "o/poke", poke.Path);
        Check("round trip: download folder", "Poke", poke.Folder);
        Check("round trip: filter", @"Games\PC", string.Join("\\", poke.Filter));
        Check("round trip: keep previous", true, poke.KeepPrevious);
        Check("round trip: versions to keep", 3, poke.KeepVersions);
        Check("round trip: ignore on refresh", true, poke.IgnoreOnRefresh);
        Check("ignore on refresh written for every repo", 2, json.Split("\"ignoreOnRefresh\"").Length - 1);
        Check("versions to keep written for every repo, 0 included", 2, json.Split("versionsToKeep").Length - 1);
        Check("0 written as such", true, json.Contains("\"versionsToKeep\": 0", StringComparison.Ordinal));
        Check("pinned version not exported: whoever imports picks their own", false,
            json.Contains("selectedVersion", StringComparison.Ordinal) || json.Contains("v1.2", StringComparison.Ordinal));

        ExportedRepo loose = read.Repos[1];
        Check("no number: all kept", 0, loose.KeepVersions);
        Check("round trip: self-hosted API", ApiType.Gitea, loose.Type);
        Check("round trip: in no folder", 0, loose.Filter.Count);
        Check("round trip: not kept", false, loose.KeepPrevious);
        Check("round trip: not ignored", false, loose.IgnoreOnRefresh);

        // ----- files that are refused whole -----

        static string Refused(string text)
        {
            try
            {
                DataTransfer.Parse(text);
                return "accepted";
            }
            catch (FormatException error)
            {
                return error.Message;
            }
        }

        const string Head = "\"format\":\"Git-Sync data\",\"version\":1";
        Check("not JSON", "This is not a Git-Sync data file.", Refused("hello"));
        Check("some other JSON", "This is not a Git-Sync data file.", Refused("""{"repos":[]}"""));
        Require("made by a newer Git-Sync", Refused("""{"format":"Git-Sync data","version":99}""").Contains("newer Git-Sync", StringComparison.Ordinal));
        Require("folder name Windows refuses",
            Refused("{" + Head + ""","filters":[["Games","Bad:Name"]]}""").Contains("Bad:Name", StringComparison.Ordinal));
        Require("unknown API type", Refused("{" + Head
            + ""","repos":[{"name":"X","site":"h.org","api":"Svn","repository":"o/r","folder":"X"}]}""").Contains("unknown API", StringComparison.Ordinal));
        Require("invalid repository address", Refused("{" + Head
            + ""","repos":[{"name":"X","site":"github.com","api":"GitHub","repository":"one-part","folder":"X"}]}""").Contains("invalid repository", StringComparison.Ordinal));
        Require("download folder Windows refuses", Refused("{" + Head
            + ""","repos":[{"name":"X","site":"github.com","api":"GitHub","repository":"o/r","folder":"a?b"}]}""").Contains("folder name", StringComparison.Ordinal));
        Require("folder name Windows refuses, in a slash path",
            Refused("{" + Head + ""","filters":["Games/Bad:Name"]}""").Contains("Bad:Name", StringComparison.Ordinal));
        Check("a folder named only by a repo still counts", @"A|A\B", string.Join("|", DataTransfer.Parse("{" + Head
            + ""","repos":[{"name":"X","site":"github.com","api":"GitHub","repository":"o/r","folder":"X","filter":["A","B"]}]}""")
            .Filters.Select(p => string.Join("\\", p))));

        // ----- import of folders and repositories -----

        Site gitlab = SiteStore.List().First(s => s.Host == "gitlab.com");
        long root = FilterStore.Add(null, "importroot");
        var existing = new Repo { Name = "Mine, renamed", SiteId = gitlab.Id, Path = "G/S/Import-C", Folder = "Mine" };
        RepoStore.Save(existing);

        string file = "{" + Head + """
            ,"filters":[["ImportRoot"],["ImportRoot","PC"],["OnlyInFile"]],
             "repos":[
               {"name":"A","site":"GitHub.com","api":"GitHub","repository":"o/import-a","folder":"A - a","filter":["ImportRoot","PC"],
                "keepPreviousVersions":true,"versionsToKeep":4,"ignoreOnRefresh":true,"selectedVersion":{"tag":"v2","kind":"Release"}},
               {"name":"B","site":"git.import-test.org","api":"Gitea","repository":"me/import-b","folder":"B","filter":[]},
               {"name":"C","site":"gitlab.com","api":"GitLab","repository":"g/s/import-c","folder":"C","filter":["ImportRoot"]}
             ]}
            """;

        ExportData both = DataTransfer.Parse(file);
        Check("new repos before importing", 2, both.Repos.Count(r => !DataTransfer.IsAlreadyThere(r, RepoStore.List(), SiteStore.List())));
        Check("new repos counted for the window", 2, DataTransfer.NewRepos(both, RepoStore.List(), SiteStore.List()));

        // Nothing chosen: every folder of the file goes into the same folder here.
        ImportResult first = DataTransfer.Import(both);
        Check("added", 2, first.AddedRepoIds.Count);
        Check("already there, whatever the case of its path", 1, first.AlreadyThere);
        Check("folders added: only PC (ImportRoot matched importroot)", 1, first.FoldersAdded);
        Check("a folder of the file with no repo to import is not created", null,
            DataTransfer.FindFolder(["OnlyInFile"], FilterStore.List()));

        List<FilterFolder> after = FilterStore.List();
        long? pc = DataTransfer.FindFolder(["importroot", "PC"], after);
        Repo a = RepoStore.List().First(r => r.Path == "o/import-a");
        Check("into the same folder as in the file", pc, a.FilterId);
        Check("existing folder reused, not doubled", 1, after.Count(f => f.ParentId is null && f.Name.Equals("importroot", StringComparison.OrdinalIgnoreCase)));
        Check("keep previous imported", true, a.KeepPrevious);
        Check("versions to keep imported", 4, a.KeepVersions);
        Check("ignore on refresh imported", true, a.IgnoreOnRefresh);
        Check("a pin in an older file is ignored", null, a.Selected);
        Check("nothing downloaded yet", null, a.Current);

        Repo b = RepoStore.List().First(r => r.Path == "me/import-b");
        Site newSite = SiteStore.List().First(s => s.Id == b.SiteId);
        Check("unknown server added", "git.import-test.org", newSite.Host);
        Check("with its API type", ApiType.Gitea, newSite.Type);
        Check("and no token", string.Empty, newSite.Token);
        Check("repo of no folder stays in none", null, b.FilterId);
        Check("no ignoreOnRefresh in the file: not ignored", false, b.IgnoreOnRefresh);

        Repo mine = RepoStore.Get(existing.Id)!;
        Check("existing repo keeps its name", "Mine, renamed", mine.Name);
        Check("existing repo keeps its folder", null, mine.FilterId);

        var seen = new ProgressRecorder();
        ImportResult second = DataTransfer.Import(both, progress: seen);
        Check("progress counts every repository of the file, ending at the total", "0,1,2,3", string.Join(",", seen.Values));
        Check("same file again: nothing added", 0, second.AddedRepoIds.Count);
        Check("same file again: all already there", 3, second.AlreadyThere);
        Check("same file again: no folders added", 0, second.FoldersAdded);

        // ----- choosing where each folder goes -----

        string other = "{" + Head + """
            ,"filters":[["Elsewhere"],["Elsewhere","X"]],
             "repos":[
               {"name":"D","site":"github.com","api":"GitHub","repository":"o/import-d","folder":"D","filter":["Elsewhere","X"]},
               {"name":"E","site":"github.com","api":"GitHub","repository":"o/import-e","folder":"E","filter":[]},
               {"name":"F","site":"github.com","api":"GitHub","repository":"o/import-f","folder":"F","filter":["Elsewhere","X"]},
               {"name":"A again","site":"github.com","api":"GitHub","repository":"o/import-a","folder":"A","filter":["Elsewhere"]},
               {"name":"G","site":"github.com","api":"GitHub","repository":"o/import-g","folder":"G","filter":"Skipped/Here"},
               {"name":"H","site":"github.com","api":"GitHub","repository":"o/import-h","folder":"H","filter":"Made/Here"}
             ]}
            """;

        ExportData chosen = DataTransfer.Parse(other);
        Check("window rows: no folder first, then the file's folders with new repos", @"(none):1|Elsewhere\X:2|Skipped\Here:1|Made\Here:1",
            string.Join("|", DataTransfer.SourceFolders(chosen, RepoStore.List(), SiteStore.List())
                .Select(s => (s.Path.Count == 0 ? "(none)" : string.Join("\\", s.Path)) + ":" + s.NewRepos)));

        // Elsewhere\X into one of ours, Skipped\Here left out; (none) and Made\Here keep the default.
        var targets = new Dictionary<string, ImportTarget>
        {
            [DataTransfer.Key(["Elsewhere", "X"])] = ImportTarget.Into(root),
            [DataTransfer.Key(["Skipped", "Here"])] = ImportTarget.DoNotImport
        };

        ImportResult third = DataTransfer.Import(chosen, targets);
        Check("chosen: added", 4, third.AddedRepoIds.Count);
        Check("chosen: the one already there skipped", 1, third.AlreadyThere);
        Check("chosen: Do not import leaves its repos out", 1, third.LeftOut);
        Check("chosen: Made and Made\\Here created for the default", 2, third.FoldersAdded);
        Check("chosen: into the folder picked", root, RepoStore.List().First(r => r.Path == "o/import-d").FilterId);
        Check("chosen: the file's folder of a redirected row is not created", null, DataTransfer.FindFolder(["Elsewhere"], FilterStore.List()));
        Check("chosen: no folder stays no folder", null, RepoStore.List().First(r => r.Path == "o/import-e").FilterId);
        Check("chosen: default goes into the created folder", DataTransfer.FindFolder(["Made", "Here"], FilterStore.List()),
            RepoStore.List().First(r => r.Path == "o/import-h").FilterId);
        Check("chosen: the left-out repo is not there", false, RepoStore.List().Any(r => r.Path == "o/import-g"));
        Check("chosen: the left-out folder is not created", null, DataTransfer.FindFolder(["Skipped"], FilterStore.List()));

        // Left out is not "already there": the same file imported again with the defaults brings it in.
        ImportResult fourth = DataTransfer.Import(chosen);
        Check("left out before, imported now", 1, fourth.AddedRepoIds.Count);
        Check("the rest already there", 5, fourth.AlreadyThere);
        Check("its folders created now", 2, fourth.FoldersAdded);

        // ----- one transaction: a failure keeps nothing -----

        string failing = "{" + Head + """
            ,"repos":[
               {"name":"G","site":"git.rollback-test.org","api":"Gitea","repository":"o/import-g","folder":"G","filter":"Lost"},
               {"name":"H","site":"github.com","api":"GitHub","repository":"o/import-h","folder":"H","filter":"Lost"}
             ]}
            """;

        int reposBefore = RepoStore.List().Count;
        int sitesBefore = SiteStore.List().Count;
        string failure = "none";

        try
        {
            // A destination that is no folder: the foreign key refuses the first repo, after its site was added.
            DataTransfer.Import(DataTransfer.Parse(failing),
                new Dictionary<string, ImportTarget> { [DataTransfer.Key(["Lost"])] = ImportTarget.Into(987654) });
        }
        catch (SqliteException error)
        {
            failure = error.SqliteErrorCode == 19 ? "constraint" : error.Message;
        }

        Check("failing import stops with the database error", "constraint", failure);
        Check("failing import keeps no repository", reposBefore, RepoStore.List().Count);
        Check("failing import keeps no site", sitesBefore, SiteStore.List().Count);

        // ----- a big file -----

        var big = new ExportData([["Big"]], []);

        for (int n = 0; n < 1500; n++)
        {
            big.Repos.Add(new ExportedRepo($"Big {n}", "github.com", ApiType.GitHub, $"big-org/repo-{n}", $"Big {n}", ["Big"], false));
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        ImportResult bigResult = DataTransfer.Import(big);
        clock.Stop();
        Check("1,500 repositories imported", 1500, bigResult.AddedRepoIds.Count);
        Require($"1,500 repositories in one transaction take seconds, not minutes ({clock.Elapsed.TotalSeconds:0.0} s)",
            clock.Elapsed < TimeSpan.FromSeconds(20));

        // ----- folder order -----

        static string ChildrenOf(params string[] path)
        {
            List<FilterFolder> all = FilterStore.List();
            long? id = path.Length == 0 ? null : DataTransfer.FindFolder(path, all);
            return string.Join(",", FilterStore.Children(id, all).Select(f => f.Name));
        }

        // The repositories come alphabetically, as an export writes them,
        // which is not the folders' order: the folders must follow the file.
        string orderFile = "{" + Head + """
            ,"filters":["OrderTop","Order","Order/Zeta","Order/Alpha","Order/Mid","Order/Mid/Two","Order/Mid/One"],
             "repos":[
               {"name":"a","site":"github.com","api":"GitHub","repository":"o/order-a","folder":"a","filter":"Order/Mid/One"},
               {"name":"b","site":"github.com","api":"GitHub","repository":"o/order-b","folder":"b","filter":"Order/Alpha"},
               {"name":"c","site":"github.com","api":"GitHub","repository":"o/order-c","folder":"c","filter":"Order/Zeta"},
               {"name":"d","site":"github.com","api":"GitHub","repository":"o/order-d","folder":"d","filter":"Order/Mid/Two"},
               {"name":"e","site":"github.com","api":"GitHub","repository":"o/order-e","folder":"e","filter":"OrderTop"}
             ]}
            """;

        DataTransfer.Import(DataTransfer.Parse(orderFile));
        Check("new folders in the file's order", "Zeta,Alpha,Mid", ChildrenOf("Order"));
        Check("new subfolders in the file's order", "Two,One", ChildrenOf("Order", "Mid"));
        Check("new top folders in the file's order, after those already here", true,
            ChildrenOf().EndsWith(",OrderTop,Order", StringComparison.Ordinal));

        // Into a tree that has some of them already: those keep their places,
        // and each new one goes next to its neighbour in the file.
        long merge = FilterStore.Add(null, "Merge");
        FilterStore.Add(merge, "Mine");
        FilterStore.Add(merge, "NES");

        string mergeFile = "{" + Head + """
            ,"filters":["Merge","Merge/PC","Merge/NES","Merge/SNES"],
             "repos":[
               {"name":"f","site":"github.com","api":"GitHub","repository":"o/order-f","folder":"f","filter":"Merge/SNES"},
               {"name":"g","site":"github.com","api":"GitHub","repository":"o/order-g","folder":"g","filter":"Merge/PC"}
             ]}
            """;

        DataTransfer.Import(DataTransfer.Parse(mergeFile));
        Check("new folders placed by their neighbours in the file", "Mine,PC,NES,SNES", ChildrenOf("Merge"));

        // Here: Sony(30, 4th in the file), Mine(20, not in it), NES(10, 2nd).
        // New: Atari(2, 0th) before NES, PC(1, 3rd) after NES, N64(3, 5th) after Sony.
        Check("placing among folders ordered unlike the file", "30,3,20,2,10,1",
            string.Join(",", DataTransfer.PlaceAmong([(30, 4), (20, null), (10, 2)], [(1, 3), (2, 0), (3, 5)])));
        Check("placing with nothing here: the file's order", "6,7,5",
            string.Join(",", DataTransfer.PlaceAmong([], [(5, 2), (6, 0), (7, 1)])));

        // Export and import again: the same order on the other side. The
        // folders are reordered with Move Up, so their ids are not their order.
        long trip = FilterStore.Add(null, "Trip");
        long tripOne = FilterStore.Add(trip, "One");
        long tripTwo = FilterStore.Add(trip, "Two");
        long tripThree = FilterStore.Add(trip, "Three");
        FilterStore.Shift(tripThree, -1);
        FilterStore.Shift(tripThree, -1);
        RepoStore.Save(new Repo { Name = "Trip a", SiteId = 1, Path = "o/trip-a", Folder = "Trip a", FilterId = tripTwo });
        RepoStore.Save(new Repo { Name = "Trip b", SiteId = 1, Path = "o/trip-b", Folder = "Trip b", FilterId = tripOne });
        RepoStore.Save(new Repo { Name = "Trip c", SiteId = 1, Path = "o/trip-c", Folder = "Trip c", FilterId = tripThree });
        Check("round trip: the order here", "Three,One,Two", ChildrenOf("Trip"));

        string tripFile = DataTransfer.ToJson(
            DataTransfer.Collect(FilterStore.List(), [.. RepoStore.List().Where(r => r.Path.StartsWith("o/trip-", StringComparison.Ordinal))], SiteStore.List()),
            DateTimeOffset.UtcNow);

        // The same list under another name, as another install would see it.
        DataTransfer.Import(DataTransfer.Parse(tripFile
            .Replace("\"Trip", "\"Trip Copy", StringComparison.Ordinal)
            .Replace("o/trip-", "o/trip-copy-", StringComparison.Ordinal)));

        Check("round trip: the same order there", "Three,One,Two", ChildrenOf("Trip Copy"));

        // ----- exporting changes nothing -----

        static string Snapshot() =>
            string.Join("\n", RepoStore.List().Select(r =>
                $"{r.Id}|{r.Name}|{r.SiteId}|{r.Path}|{r.Folder}|{r.FilterId}|{r.KeepPrevious}|{r.Selected}|{r.Current}|{string.Join(",", r.CurrentFiles)}"))
            + "\n" + string.Join("\n", FilterStore.List().Select(f => $"{f.Id}|{f.ParentId}|{f.Name}|{f.SortOrder}|{f.Expanded}"))
            + "\n" + string.Join("\n", SiteStore.List().Select(s => $"{s.Id}|{s.Host}|{s.Type}|{s.Token}"));

        string before = Snapshot();
        string whole = DataTransfer.ToJson(
            DataTransfer.Collect(FilterStore.List(), RepoStore.List(), SiteStore.List()), DateTimeOffset.UtcNow);
        Check("export leaves the database as it was", before, Snapshot());
        Check("the whole list exported", RepoStore.List().Count, DataTransfer.Parse(whole).Repos.Count);
    }

    // ---------- live APIs ----------

    /// <summary>The first two bytes of a download in hex, or the HTTP status when it fails.</summary>
    private static async Task<string> FirstBytes(Site site, string url)
    {
        // Sites build source archives on request; a big repo can take a while.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        try
        {
            using HttpResponseMessage response = await ApiClient.SendAsync(site, url, "*/*", timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                return $"HTTP {(int)response.StatusCode}";
            }

            await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            byte[] start = new byte[2];
            await stream.ReadExactlyAsync(start, timeout.Token);
            return Convert.ToHexString(start);
        }
        catch (OperationCanceledException)
        {
            return "no answer in 120 s";
        }
    }

    private static async Task TestOnline()
    {
        Section("Live APIs");

        List<Site> sites = SiteStore.List();
        Site github = sites.First(s => s.Host == "github.com");
        Site gitlab = sites.First(s => s.Host == "gitlab.com");

        // The database's Codeberg entry got a fake token in TestDatabaseSeed:
        // that is the wrong-token case, and the real queries go without one.
        Site badToken = sites.First(s => s.Host == "codeberg.org");
        var codeberg = new Site { Id = badToken.Id, Host = "codeberg.org", Type = ApiType.Gitea };

        try
        {
            await ApiClient.ListReleasesAsync(badToken, new Repo { Path = "forgejo/forgejo" }, 1);
            Check("wrong token", "an error", "no error");
        }
        catch (ApiException error)
        {
            Require("wrong token reported: " + error.Message, error.Message.Contains("401", StringComparison.Ordinal));
        }

        async Task Releases(Site site, string path, bool archivesDisabled = false)
        {
            var repo = new Repo { Path = path };

            try
            {
                List<Release> list = await ApiClient.ListReleasesAsync(site, repo, 5);
                Require($"{site.Host} {path}: releases listed", list.Count > 0);

                Release? latest = ReleaseParser.PickLatest(list);
                Require($"{site.Host} {path}: Latest found", latest is not null);
                Require($"{site.Host} {path}: Latest has a date", latest?.Date is not null);

                if (latest is not null)
                {
                    Release again = await ApiClient.GetReleaseAsync(site, repo, latest.ToVersion());
                    Check($"{site.Host} {path}: fetched again by tag", latest.Tag, again.Tag);
                    Require($"{site.Host} {path}: has files", again.Files.Count > 0);
                    Console.WriteLine($"       {latest.Tag}  {latest.Date:yyyy-MM-dd}  {again.Files.Count} file(s), first: {again.Files[0].Name}");

                    byte[]? readme = await ApiClient.GetReadmeAsync(site, repo, latest.Tag);
                    Require($"{site.Host} {path}: README.md at that version", readme is { Length: > 0 });
                    Console.WriteLine($"       README.md: {readme?.Length ?? 0} bytes");

                    // "Show source code": both archive addresses work. Only the
                    // first bytes are read — enough to tell a zip ("PK") from a
                    // gzip (1F 8B), and to catch a .tar.gz unpacked on the way.
                    Check($"{site.Host} {path}: zip and tar.gz offered",
                        $"{RepoAddress.LastSegment(path)}-source.zip|{RepoAddress.LastSegment(path)}-source.tar.gz",
                        string.Join("|", again.Sources.Select(f => f.Name)));

                    // A server with source downloads switched off still lists
                    // the addresses, and they answer 404.
                    foreach (ReleaseFile archive in again.Sources)
                    {
                        Check($"{site.Host} {path}: {archive.Name} answers",
                            archivesDisabled ? "HTTP 404"
                            : archive.Name.EndsWith(".zip", StringComparison.Ordinal) ? "504B" : "1F8B",
                            await FirstBytes(site, archive.Url));
                    }

                    if (archivesDisabled)
                    {
                        var saved = new Repo { Name = path, SiteId = site.Id, Path = path, Folder = "NoArchives" };
                        saved.Id = RepoStore.Save(saved);

                        try
                        {
                            await Downloader.DownloadAsync(site, saved, [], again, again.Sources, null, CancellationToken.None);
                            Check($"{site.Host} {path}: archive download", "refused", "saved");
                        }
                        catch (ApiException error)
                        {
                            Check($"{site.Host} {path}: why the archive failed",
                                $"{again.Sources[0].Name}: this server does not offer source code downloads (404).", error.Message);
                        }

                        string folder = DownloadPaths.RepoFolder(saved, []);
                        Check($"{site.Host} {path}: nothing left behind", 0,
                            Directory.Exists(folder) ? Directory.GetFiles(folder).Length : 0);
                        Check($"{site.Host} {path}: Current unchanged", null, RepoStore.Get(saved.Id)?.Current);
                    }
                }
            }
            catch (ApiException error)
            {
                Check($"{site.Host} {path}", "no error", error.Message);
            }
        }

        await Releases(github, "cli/cli");
        await Releases(gitlab, "gitlab-org/cli");
        await Releases(codeberg, "forgejo/forgejo");

        // Self-hosted servers: what each one runs, found from its answers.
        async Task Detect(string host, ApiType? expected)
        {
            try
            {
                Check($"{host} detected as", expected, await SiteDetection.DetectAsync(host));
            }
            catch (ApiException error)
            {
                Check($"{host} detected", "no error", error.Message);
            }
        }

        await Detect("git.magicbean.dev", ApiType.Gitea);
        await Detect("codeberg.org", ApiType.Gitea);
        await Detect("gitlab.gnome.org", ApiType.GitLab);
        await Detect("gitlab.com", ApiType.GitLab);
        await Detect("example.com", null);

        // Longshot on its own Forgejo, read with the Codeberg code. That server
        // has source downloads switched off.
        Site magicbean = SiteStore.Add("git.magicbean.dev", ApiType.Gitea);
        await Releases(magicbean, "MagicBeanDev/Longshot", archivesDisabled: true);

        // Requests left, as the Sites tab asks for them.
        foreach (Site site in new[] { github, gitlab, codeberg })
        {
            var anonymous = new Site { Id = site.Id, Host = site.Host, Type = site.Type };

            try
            {
                RateLimit? limit = await ApiClient.GetRateLimitAsync(anonymous);
                Require($"{site.Host}: limit reported", limit is not null && limit.Limit > 0 && limit.Remaining <= limit.Limit);
                Console.WriteLine($"       {site.Host}: {limit?.Remaining} of {limit?.Limit}, resets {limit?.Reset?.ToLocalTime():HH:mm}");
            }
            catch (ApiException error)
            {
                Check($"{site.Host}: limit", "no error", error.Message);
            }

            try
            {
                await ApiClient.GetRateLimitAsync(new Site { Id = site.Id, Host = site.Host, Type = site.Type, Token = "not-a-real-token" });
                Check($"{site.Host}: wrong token", "rejected", "accepted");
            }
            catch (ApiException error)
            {
                Require($"{site.Host}: wrong token rejected: {error.Message}", error.Message.Contains("401", StringComparison.Ordinal));
            }
        }

        // A repository without any release: falls back to the branch head.
        var hello = new Repo { Id = 0, Path = "octocat/Hello-World", Folder = "Hello" };

        try
        {
            Release? head = await ApiClient.CheckLatestAsync(github, hello);
            Check("no releases -> commit", VersionKind.Commit, head?.Kind);
            Check("whole commit id", 40, head?.Tag.Length);
            Console.WriteLine($"       {head?.Title}  {head?.Date:yyyy-MM-dd}  {head?.Files.FirstOrDefault()?.Name}");

            if (head is not null)
            {
                // The archive address redirects to another host: this is where
                // the hand-made redirect following is exercised.
                hello.Id = RepoStore.Save(new Repo
                {
                    Name = "Hello", SiteId = github.Id, Path = hello.Path, Folder = "Hello"
                });

                DownloadOutcome outcome = await Downloader.DownloadAsync(
                    github, hello, [], head, head.Files, null, CancellationToken.None);

                string saved = DownloadPaths.ToAbsolute(outcome.Files.Single());
                Require("source archive downloaded", File.Exists(saved) && new FileInfo(saved).Length > 0);
                Check("saved with the commit at the end", $"Hello-World-source ({head.Tag[..7]}).zip", Path.GetFileName(saved));
                Check("Current recorded", head.Tag, RepoStore.Get(hello.Id)?.Current?.Tag);

                // Hello-World's README is a plain "README", not a README.md: nothing saved.
                Check("no README.md when the repo has none", false,
                    File.Exists(Path.Combine(Path.GetDirectoryName(saved)!, "README.md")));

                // With "Show source code" on, a commit offers both archives;
                // downloading both replaces the zip alone.
                string shortSha = head.Tag[..7];
                DownloadOutcome archives = await Downloader.DownloadAsync(
                    github, RepoStore.Get(hello.Id)!, [], head, head.Offered(true), null, CancellationToken.None);
                Check("zip and tar.gz saved", $"Hello-World-source ({shortSha}).zip|Hello-World-source ({shortSha}).tar.gz",
                    string.Join("|", archives.Files.Select(Path.GetFileName)));

                byte[] gzip = new byte[2];
                using (FileStream file = File.OpenRead(DownloadPaths.ToAbsolute(archives.Files[1])))
                {
                    file.ReadExactly(gzip);
                }

                Check("saved tar.gz is still gzip", "1F8B", Convert.ToHexString(gzip));
                Check("Current is the commit", VersionKind.Commit, RepoStore.Get(hello.Id)?.Current?.Kind);

                // "Redownload…" with only the zip ticked: the tar.gz goes, and
                // nothing is renamed "(tag, 2)" even with "Keep previous versions" on.
                Repo keeping = RepoStore.Get(hello.Id)!;
                keeping.KeepPrevious = true;
                RepoStore.Save(keeping);

                DownloadOutcome redone = await Downloader.DownloadAsync(
                    github, RepoStore.Get(hello.Id)!, [], head, [head.Sources[0]], null, CancellationToken.None, redownload: true);
                string helloFolder = Path.GetDirectoryName(DownloadPaths.ToAbsolute(redone.Files.Single()))!;
                Check("redownload: only the ticked file is left", $"Hello-World-source ({shortSha}).zip",
                    string.Join("|", Directory.GetFiles(helloFolder).Select(Path.GetFileName).Where(n => n != "README.md")));
                Check("redownload: Current lists that file alone", 1, RepoStore.Get(hello.Id)?.CurrentFiles.Count);
            }
        }

        catch (ApiException error)
        {
            Check("octocat/Hello-World", "no error", error.Message);
        }

        // A download from a repo that has a README.md saves it beside the files,
        // and a second download writes over it rather than adding another.
        var gitlabCli = new Repo { Name = "glab", SiteId = gitlab.Id, Path = "gitlab-org/cli", Folder = "glab" };
        gitlabCli.Id = RepoStore.Save(gitlabCli);

        try
        {
            Release latestGlab = ReleaseParser.PickLatest(await ApiClient.ListReleasesAsync(gitlab, gitlabCli, 5))!;
            Release glab = await ApiClient.GetReleaseAsync(gitlab, gitlabCli, latestGlab.ToVersion());
            ReleaseFile smallest = glab.Files.OrderBy(f => f.Name.Contains("checksums", StringComparison.OrdinalIgnoreCase) ? 0 : 1).First();

            string folder = DownloadPaths.RepoFolder(gitlabCli, []);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "README.md"), "an old README");

            await Downloader.DownloadAsync(gitlab, gitlabCli, [], glab, [smallest], null, CancellationToken.None);

            string readme = File.ReadAllText(Path.Combine(folder, "README.md"));
            Require("README.md saved beside the files, over the old one", readme.Length > 100 && readme != "an old README");
            Check("only one README in the folder", 1, Directory.GetFiles(folder, "README*").Length);
            Check("README is not one of the version's files", false,
                RepoStore.Get(gitlabCli.Id)!.CurrentFiles.Any(f => f.EndsWith("README.md", StringComparison.OrdinalIgnoreCase)));
            Console.WriteLine($"       {smallest.Name} + README.md ({readme.Length} chars)");

            // "Download README.md" unticked: the next download leaves the README there alone.
            PreferenceStore.SetDownloadReadme(false);
            File.WriteAllText(Path.Combine(folder, "README.md"), "kept as it was");
            await Downloader.DownloadAsync(gitlab, RepoStore.Get(gitlabCli.Id)!, [], glab, [smallest], null, CancellationToken.None);
            Check("README.md option off: the README there is left alone", "kept as it was",
                File.ReadAllText(Path.Combine(folder, "README.md")));
        }
        catch (ApiException error)
        {
            Check("glab download with README", "no error", error.Message);
        }
        finally
        {
            PreferenceStore.SetDownloadReadme(true);
        }

        try
        {
            await ApiClient.ListReleasesAsync(github, new Repo { Path = "this-owner-does-not-exist-9x7/none" }, 5);
            Check("missing repository", "an error", "no error");
        }
        catch (ApiException error)
        {
            Require("missing repository reported: " + error.Message, error.Message.Contains("404", StringComparison.Ordinal));
        }

        // Git-Sync's own newest release, as the launch check asks for it.
        try
        {
            AppRelease? own = await AppUpdate.LatestAsync(CancellationToken.None);
            Require("Git-Sync's newest release found", own is not null);
            Require($"Git-Sync's newest release is at least 1.0.5: {own?.Tag}", own is not null && own.Version >= new Version(1, 0, 5, 0));
        }
        catch (ApiException error)
        {
            Check("Git-Sync's newest release", "no error", error.Message);
        }
    }
}
