# Git-Sync

A small Windows app that keeps track of new releases on GitHub, GitLab, Codeberg and
self-hosted Forgejo / Gitea / GitLab servers, sorts the repositories you follow into
folders of your own, and downloads the releases you pick.

![Git-Sync main window](docs/screenshot.png)

## Features

- **Your own folder tree.** Create folders and subfolders in Settings (for example
  *Games › PC*) and file each repository under one. Selecting a folder lists its
  repositories and those of every folder below it.
- **Latest / Current at a glance.** Refresh checks every repository (or only the selected
  folder) and shows the newest version next to the one you have. Repositories that publish
  no files are marked **SOURCE** and get their source archive instead; a repository that
  could not be checked shows a red ✕ with the reason.
- **Download and Update.** A release with one file downloads straight away; with several,
  you pick which ones, and the next update pre-selects the same files. **Update All** walks
  through every repository with a newer version, one at a time.
- **Version in every file name.** Files are saved as `name (build-212).zip`, so builds that
  only differ by a hash can be told apart. The repository's `README.md` is saved beside them.
- **Keep previous versions** per repository, or let each update replace the old files.
  Only files Git-Sync downloaded itself are ever deleted.
- **Pin a version.** Right-click → *Versions* lists every release on the site; pick an older
  one to stay on it.
- **Add by link.** Drag a repository link from the browser onto the window, or press
  **Ctrl+V** with one copied. Self-hosted servers are recognised automatically.
- **Tokens and limits.** Settings → Sites takes a token per site and shows how many API
  requests are left.
- **Portable.** One self-contained `Git-Sync.exe` (no .NET install needed). Its database,
  `Git-Sync.db`, and the downloads, under `Apps\<folder path>\<repository folder>\`, live
  beside it.

> `Git-Sync.db` holds your tokens in plain text. Keep it private when you copy the folder.

## Building

Requires Windows and the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```
dotnet publish Git-Sync.csproj -c Release -o <output folder>
```

The output folder must be outside this folder (see `Directory.Build.props`).

## Tests

The `tests` folder holds a console test bench that compiles the same source files.

```
cd tests
dotnet run                # offline checks
dotnet run -- --online    # also queries GitHub, GitLab, Codeberg and a self-hosted Forgejo
```
