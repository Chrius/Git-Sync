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
        TestSorting();
        TestRateLimits();
        TestSiteDetection();

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
        Check("inside Apps", true, DownloadPaths.IsInsideApps(DownloadPaths.ToAbsolute(@"Apps\Games\x.zip")));
        Check("next to the exe is outside Apps", false, DownloadPaths.IsInsideApps(DownloadPaths.ToAbsolute("Git-Sync.db")));
        Check("climbing out of Apps", false, DownloadPaths.IsInsideApps(DownloadPaths.ToAbsolute(@"Apps\..\Git-Sync.db")));
        Check("above the exe", false, DownloadPaths.IsInsideApps(DownloadPaths.ToAbsolute(@"..\outside.txt")));
        Check("Apps itself", false, DownloadPaths.IsInsideApps(AppPaths.AppsDir));
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
                [(newPart, Path.Combine(folder, "app-2.zip")), (samePart, same)], previous);
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
           "assets":[{"name":"app-v1.4.0-win64.zip","size":2048,
                      "url":"https://api.github.com/repos/o/r/releases/assets/1",
                      "browser_download_url":"https://github.com/o/r/releases/download/v1.4.0/app-v1.4.0-win64.zip"},
                     {"name":"app-v1.4.0-linux.tar.gz","size":1024,
                      "url":"https://api.github.com/repos/o/r/releases/assets/2",
                      "browser_download_url":"https://github.com/o/r/releases/download/v1.4.0/app-v1.4.0-linux.tar.gz"}]},
          {"tag_name":"v1.3.0","name":"Source only","draft":false,"prerelease":false,
           "published_at":"2026-10-01T12:00:00Z","zipball_url":"https://api.github.com/repos/o/r/zipball/v1.3.0",
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
                            {"format":"tar.gz","url":"https://gitlab.com/g/s/p/-/archive/v3.1/p-v3.1.tar.gz"}],
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
    }

    private static void TestGiteaParsing()
    {
        Section("Gitea / Forgejo releases");

        const string json = """
            [{"tag_name":"v9.0.1","name":"9.0.1","draft":false,"prerelease":false,
              "published_at":"2026-11-02T00:00:00+01:00","html_url":"https://codeberg.org/o/r/releases/tag/v9.0.1",
              "zipball_url":"https://codeberg.org/o/r/archive/v9.0.1.zip",
              "assets":[{"name":"r-9.0.1.tar.xz","size":4096,
                         "browser_download_url":"https://codeberg.org/o/r/releases/download/v9.0.1/r-9.0.1.tar.xz"}]}]
            """;

        Release release = ReleaseParser.ParseList(ApiType.Gitea, json, "o/r").Single();
        Check("tag", "v9.0.1", release.Tag);
        Check("date in UTC", new DateTimeOffset(2026, 11, 1, 23, 0, 0, TimeSpan.Zero), release.Date);
        Check("file", "r-9.0.1.tar.xz", release.Files.Single().Name);
        Check("size", 4096L, release.Files.Single().Size);
        Check("kind", VersionKind.Release, release.Kind);

        const string single = """{"tag_name":"v1","assets":[],"zipball_url":"https://codeberg.org/o/r/archive/v1.zip","published_at":"2026-01-01T00:00:00Z"}""";
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

        List<string> warnings = Downloader.Commit(
            [(Write("app-win64.zip.part", "v2 build"), reused), (Write("app-v2.zip.part", "v2 versioned"), Path.Combine(folder, "app-v2.zip"))],
            [DownloadPaths.ToRelative(reused), DownloadPaths.ToRelative(versioned)],
            keepAs: "v1");

        Check("no warnings", 0, warnings.Count);
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

    // ---------- live APIs ----------

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

        async Task Releases(Site site, string path)
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

        // Longshot on its own Forgejo, read with the Codeberg code.
        await Releases(new Site { Host = "git.magicbean.dev", Type = ApiType.Gitea }, "MagicBeanDev/Longshot");

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
        }
        catch (ApiException error)
        {
            Check("glab download with README", "no error", error.Message);
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
    }
}
