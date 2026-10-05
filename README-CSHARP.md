# NuGet Recursive Downloader — C#

Download a NuGet package **and all of its dependencies** from nuget.org as
`.nupkg` files.

The script opens every downloaded package, reads its dependency list from the
`.nuspec` inside it, resolves each dependency's version, downloads those too,
and repeats until nothing is missing. Only `.nupkg` files are saved.

## Requirements

- The **.NET 10 SDK** — `nuget_download.cs` is a *file-based app*: it runs
  directly from the single `.cs` file, with no `.csproj` and no packages to
  restore.
- Internet access to nuget.org

Check your SDK with `dotnet --version`.

## How to run

Open a terminal in the folder that contains `nuget_download.cs`.

> **Note the `--`** before the arguments. It tells `dotnet run` that everything
> after it belongs to the app, not to the CLI host. Without it, options like
> `--auto` are swallowed by `dotnet run` itself.
>
> **If the folder already contains a `.csproj`** (a real project), use the
> explicit `--file` form instead, so `dotnet run` cannot pick the wrong project:
>
> ```bash
> dotnet run --file nuget_download.cs -- --auto
> ```
>
> The examples below use the short form. Swap in `--file nuget_download.cs`
> whenever a project file sits next to the script.
> See [Running it inside a real project](#running-it-inside-a-real-project).

**1. Package id and version on the command line:**

```bash
dotnet run nuget_download.cs -- Enums.NET 5.0.0
# in a folder that also has a .csproj:
dotnet run --file nuget_download.cs -- Enums.NET 5.0.0
```

**2. Ask for the version interactively** (the script lists every published
version and you type the one you want):

```bash
dotnet run nuget_download.cs -- Enums.NET
```

**3. Fully interactive** — the script shows the menu first:

```bash
dotnet run nuget_download.cs
```

**4. Download into a different folder** (`-o` / `--output`):

```bash
dotnet run nuget_download.cs -- Castle.Core 5.1.1 -o D:\nuget-mirror
```

**5. Auto detect without the menu** (`--auto`, optional `--scan-dir`):

```bash
dotnet run nuget_download.cs -- --auto --scan-dir D:\src\MySolution
```

**6. Non-interactive one-shot** (`--no-menu`, for scheduled runs):

```bash
dotnet run nuget_download.cs -- Enums.NET 5.0.0 --no-menu
```

**Show the built-in help:**

```bash
dotnet run nuget_download.cs -- --help
```

**Scanning a project from a separate tools folder** (the recommended layout —
nothing in your solution is touched):

```bash
dotnet run --file C:\tools\nuget\nuget_download.cs -- --auto --scan-dir C:\src\MySolution -o C:\tools\nuget\packages
```

## Running it inside a real project

A file-based app is a *single file that the SDK turns into a project behind the
scenes*. When you drop it next to a real `.csproj`, two things from the
surrounding project can break the build. Both are already handled by the
`#:property` lines at the top of `nuget_download.cs`, but here is what they do
and how to run it safely anyway.

### Use `--file` whenever a `.csproj` is in the folder

```bash
dotnet run --file nuget_download.cs -- --auto
```

`dotnet run <file>.cs` is ambiguous in a folder that contains a project — the
CLI can bind the argument to the project instead of the file. `--file` removes
the ambiguity.

Even with `--file`, a sibling `.csproj` may still *compile* `nuget_download.cs`
as one of its own sources and choke on the `#!/` and `#:` directives
(`error CS9314` / `error CS9298`). That happens only if the script sits in a
folder that a real project globs with the default `**/*.cs` pattern. The clean
fix is to keep the script in its own folder:

```text
C:\tools\nuget\
    nuget_download.cs            <- the script
C:\src\MySolution\               <- your actual project, unchanged
    MySolution.csproj
```

then run it from anywhere:

```bash
dotnet run --file C:\tools\nuget\nuget_download.cs -- --auto --scan-dir C:\src\MySolution -o C:\tools\nuget\packages
```

### NU1101: "Unable to find package Microsoft.NETCore.App.Runtime.win-x64"

You may see this instead:

```text
<path-to-your-project>\nuget_download.cs.csproj : error NU1101: Unable to find
package Microsoft.NETCore.App.Runtime.win-x64. No packages exist with this id in
source(s): <the feeds configured in your NuGet.config>
```

This is **not a bug in the script** — it is your project leaking into it:

1. a solution-level `Directory.Build.props` (or the generated project's
   defaults) sets `RuntimeIdentifier` / `RuntimeIdentifiers` to `win-x64` and
   enables self-contained or AOT publishing, so the generated project wants the
   **runtime packs** `Microsoft.NETCore.App.Runtime.win-x64`,
   `Microsoft.WindowsDesktop.App.Runtime.win-x64`, etc.;
2. your `NuGet.config` restricts the feeds to internal ones
   (`<clear />` plus a private source), so those packs cannot be restored.

The first six lines of `nuget_download.cs` fix it:

```csharp
#:property RuntimeIdentifier=
#:property RuntimeIdentifiers=
#:property SelfContained=false
#:property PublishAotUsingRuntimePack=false
#:property PublishReadyToRun=false
#:property RestoreAdditionalProjectSources=https://api.nuget.org/v3/index.json
```

They clear the inherited runtime identifiers (back to a plain framework-dependent
console app) and add nuget.org as a restore source, so the script builds no
matter how the surrounding project is configured — no need to edit your
`Directory.Build.props` or `NuGet.config`.

If you ever see this after updating the script, make sure those lines are still
the first thing in the file, right after the `#!/usr/bin/env dotnet` shebang
(they must not be moved below any `using` or code line).

## Making the command shorter

The first run compiles the file, which takes a few seconds. Publish it once
into a standalone executable and then call it directly:

```bash
dotnet publish nuget_download.cs -c Release -r win-x64 --self-contained -o dist
dist\nuget_download.exe Enums.NET 5.0.0
```

Or add a tiny wrapper batch file next to it:

```bat
@echo off
dotnet run "%~dp0nuget_download.cs" -- %*
```

## Configuration

The same settings exist as constants at the top of the `static class App` in
`nuget_download.cs`:

| Constant                   | Default      | Meaning                                        |
|----------------------------|--------------|------------------------------------------------|
| `DefaultOutputDir`         | `"packages"` | output folder used when `-o` is not given      |
| `Retries`                  | `3`          | network attempts per request                   |
| `ChunkSize`                | 64 KB        | download buffer size                           |
| `BarWidth`                 | `24`         | width of the progress bar                      |
| `ProjectFileSuffix`        | `".csproj"`  | project files read by auto detect              |
| `DirectoryPackagesProps`   | `directory.packages.props` | Central Package Management file |
| `AutoDetectTags`           | `PackageReference`, `PackageVersion` | XML tags read by auto detect |
| `IgnoredAutoPackages`      | framework ids | skipped by auto detect (they are not packages) |
| `SkipFolders`              | `bin`, `obj`, `.git`, `.vs`, `node_modules`, `packages` | folders not descended into |

## The menu

Running the script with no package id on the command line opens the menu. It
reappears after every download, so you can queue as many packages as you like:

```text
=================== NuGet downloader ===================
  1) Manual entry   - type a package id and version
  2) Auto detect    - read every .csproj / Directory.Packages.props here
  0) Exit
========================================================
Choose 1, 2 or 0:
```

- **`1` Manual entry** — it asks for the package id, lists the published
  versions, asks which one to download, then walks the whole dependency tree.
  Press Enter without typing an id to go back to the menu.
- **`2` Auto detect** — scans for projects and downloads everything they
  reference (see below).
- **`0` Exit** — leaves the script (Enter on an empty line does the same).

If you *do* pass a package id on the command line
(`dotnet run nuget_download.cs -- Enums.NET 5.0.0`), that download runs first
and the menu is shown afterwards. Aliases `m`, `auto` and `q` are accepted;
anything else just prints "not one of the menu options" and shows the menu
again.

Pass `--no-menu` to keep the script non-interactive (useful for scheduled runs),
or `--auto` to jump straight into auto detect without any menu.

## Auto detect mode

Auto detect walks **the folder you started the script in** (the current working
directory), nested, and opens these files:

- every `*.csproj`
- every `Directory.Packages.props` (Central Package Management)

It reads each file one by one and collects every

- `<PackageReference Include="Serilog.AspNetCore" Version="8.0.0" />` and
- `<PackageVersion Include="FluentResults" Version="4.0.0" />`

element, taking the id from `Include`/`Update` and the version from `Version`.
XML namespaces are ignored, so files using `<PackageReference>` inside a
namespaced project file work as well.

Then every extracted `id` + `version` pair is passed to the same download
routine the manual option uses, so each package **and its whole dependency tree**
is downloaded into the output folder. Already-present `.nupkg` files are skipped,
so re-running is free.

Details:

- references are **deduplicated by package id** (case-insensitive); the same
  package referenced by five projects is downloaded once;
- a `<PackageReference Include="X" />` with no version (Central Package
  Management) gets its version from the matching
  `<PackageVersion Include="X" Version="..." />` in `Directory.Packages.props`;
  if no props file declares it, the newest stable version is used;
- framework/SDK placeholders that are not real packages (`Microsoft.NETCore.App`,
  `NETStandard.Library`, `Microsoft.NETCore.Targets`, `Microsoft.NETCore.Platforms`)
  are skipped — remove an entry from `IgnoredAutoPackages` to keep it;
- `bin`, `obj`, `.git`, `.vs`, `node_modules` and `packages` folders are not
  descended into;
- a file that is not valid XML is reported and skipped, the scan continues.

Example output:

```text
Found 3 project file(s) under 'D:\src\MySolution':
  - Directory.Packages.props
  - src\Api\Api.csproj
  - src\Lib\Lib.csproj
    Directory.Packages.props: 1 reference(s)
    src\Api\Api.csproj: 2 reference(s)
    src\Lib\Lib.csproj: 2 reference(s)

Detected 3 package reference(s):
  Castle.Core 5.1.1
  FluentResults 4.0.0
  Serilog.AspNetCore 8.0.0
```

## What you will see

```text
Downloading Enums.NET 5.0.0 and every dependency to 'packages'

Found 100 existing .nupkg file(s) in 'packages'.
+ System.Runtime 4.3.0 [############------------]  50.3% 3.5 MB / 6.9 MB
+ System.Runtime 4.3.0 (6.9 MB)
= Enums.NET 5.0.0 (already downloaded, 405.9 KB)

Done: 115 package(s) under 'packages' (1 downloaded now, 114 already present).
```

- `+` — the package was downloaded now (with a live progress bar while it
  streams; the bar is only drawn when the output is a real terminal, so
  redirected logs stay clean).
- `=` — the package was already in the output folder, so it was skipped.
- A final summary counts everything; if any dependency could not be resolved,
  it is listed at the end and the script exits with code `1`.

## Where the files go

Every package is saved **directly** in the output folder as a single file,
with no subfolders:

```text
packages/
├── enums.net.5.0.0.nupkg
├── newtonsoft.json.13.0.3.nupkg
├── system.runtime.4.3.0.nupkg
└── ...
```

Default folder is `packages` (next to where you run the command). Change it
per run with `-o`, or permanently by editing `DefaultOutputDir` at the top of
`nuget_download.cs`. Relative paths resolve from your current working
directory; absolute paths like `D:\nuget-mirror` or `D:/nuget-mirror` both
work.

## Download once, never again

At every start the script scans the output folder and keeps an in-memory list
of the `.nupkg` files already there:

- packages on that list are **never downloaded again**;
- every successful download is added to the list;
- an interrupted run (Ctrl+C, network drop) simply resumes where it stopped —
  just run the same command again.

Partial downloads are written to `<file>.part` and only renamed to the final
name when complete, so an interrupted transfer never leaves a half-written
`.nupkg` behind.

## How dependency versions are chosen

A `.nuspec` usually contains a version *range*, not one exact version. The
script resolves it the same way NuGet's restore does:

- range with a lower bound (e.g. `>= 4.3.0`, `[4.3.0]`, plain `4.3.0`) →
  the **lowest stable version** that satisfies it;
- range with only an upper bound (e.g. `(, 2.0.0]`) or no constraint (`*`) →
  the **newest stable version** that fits;
- prereleases are used only when no stable version satisfies the range;
- if the chosen file is no longer on the feed, the next candidate is tried
  automatically.

## Troubleshooting

- **`dotnet run` prints its own help instead of running the script** — you
  forgot the `--` separator. Use
  `dotnet run nuget_download.cs -- Enums.NET 5.0.0`.
- **`warning NU1101 ... Microsoft.NETCore.App.Runtime.win-x64` or
  `error CS9314 / CS9298`** — the surrounding project is interfering; see
  [Running it inside a real project](#running-it-inside-a-real-project). Short
  version: use `dotnet run --file nuget_download.cs -- ...`, and keep the script
  out of folders a real `.csproj` globs.
- **Build / restore errors** — the file-based app needs the **.NET 10 SDK**;
  check `dotnet --version`.
- **`nuget.org is unreachable: ...`** — the connection to the internet is
  failing (VPN/proxy off, firewall, no network). Test any website in a browser;
  when the connection is back, just run the same command again — nothing is
  downloaded twice.
- **`Version 'x.y.z' ... was not found`** — the script prints all available
  versions; pick one of them (or run without a version to choose
  interactively).
- **`Package '...' was not found on nuget.org`** — check the package id
  spelling (ids are case-insensitive).
- **Ctrl+C** exits with code `130`.

## Tip: use the folder as a local NuGet source

Because the output folder contains plain `.nupkg` files, you can add it as a
local package source in Visual Studio / `nuget.exe` / `dotnet`:

```bash
dotnet nuget add source "C:\path\to\packages" --name Mirror
```
