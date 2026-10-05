# NuGet Offline Downloader

Download a NuGet package **and all of its dependencies** from nuget.org as
`.nupkg` files — for offline, air-gapped or mirror scenarios.

The tool opens every downloaded package, reads its dependency list from the
`.nuspec` inside it, resolves each dependency's version, downloads those too,
and repeats until nothing is missing. Only `.nupkg` files are saved.

## Pick your language

This repository contains **two complete implementations** of the same tool. They
behave identically — same options, same output folder layout, same `+` / `=`
output, same exit codes — so use whichever fits your environment, or keep both.

| Language | Script | Documentation | Requirements | Run |
|----------|--------|---------------|--------------|-----|
| **Python** | [`nuget_download.py`](nuget_download.py) | **[README-PYTHON.md](README-PYTHON.md)** | Python 3.6+, standard library only, nothing to install | `python nuget_download.py Enums.NET 5.0.0` |
| **C#** | [`nuget_download.cs`](nuget_download.cs) | **[README-CSHARP.md](README-CSHARP.md)** | .NET 10 SDK, nothing to restore | `dotnet run --file nuget_download.cs -- Enums.NET 5.0.0` |

> **Running the C# script inside a real project?** Use `dotnet run --file ...`
> and preferably keep the script out of folders a real `.csproj` globs. A project
> with `RuntimeIdentifier` or a restricted `NuGet.config` can otherwise break the
> build with `NU1101: Unable to find package Microsoft.NETCore.App.Runtime.win-x64`
> (the script already defends itself, but the `--file` form avoids the ambiguity
> entirely). Details in
> [README-CSHARP.md → Running it inside a real project](README-CSHARP.md#running-it-inside-a-real-project).

## Quick start (either version)

**Download a package and its whole dependency tree:**

```bash
python nuget_download.py Enums.NET 5.0.0
dotnet run --file nuget_download.cs -- Enums.NET 5.0.0
```

**Let it ask which version, or show the interactive menu:**

```bash
python nuget_download.py Enums.NET
python nuget_download.py
```

**Scan a solution for `.csproj` / `Directory.Packages.props` and download
everything it references:**

```bash
python nuget_download.py --auto --scan-dir C:\src\MySolution
dotnet run --file nuget_download.cs -- --auto --scan-dir C:\src\MySolution
```

Packages land flat in `./packages` as `<id>.<version>.nupkg`, and anything
already there is never downloaded twice — so an interrupted run just resumes
where it stopped when you run the same command again.

## Features

- **Recursive dependency download** — reads each package's `.nuspec` and walks
  the graph until nothing is missing.
- **Resume** — the output folder is scanned at start-up; existing `.nupkg` files
  are never re-downloaded, so an interrupted run continues where it stopped.
- **Auto detect** — scans a solution for `*.csproj` and
  `Directory.Packages.props`, reads every `<PackageReference>` /
  `<PackageVersion>`, and downloads them plus their dependency trees.
- **Central Package Management** — version-less references are resolved from
  `Directory.Packages.props`.
- **NuGet-style version resolution** — version ranges are resolved the same way
  NuGet's restore does (lowest stable match when there is a lower bound, newest
  stable otherwise, prereleases only as a last resort).
- **Flat output** — one `<id>.<version>.nupkg` per package, no subfolders, so
  the folder can be used directly as a local NuGet source.
- **Interactive menu** — queue as many packages as you like, or run headless
  with `--auto` / `--no-menu`.

## Documentation

Full documentation, including every option, the auto-detect rules, the
version-resolution behaviour and troubleshooting, lives in the two files linked
in the table above.
