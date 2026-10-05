# NuGet Recursive Downloader — Python

Download a NuGet package **and all of its dependencies** from nuget.org as
`.nupkg` files.

The script opens every downloaded package, reads its dependency list from the
`.nuspec` inside it, resolves each dependency's version, downloads those too,
and repeats until nothing is missing. Only `.nupkg` files are saved.

## Requirements

- Python 3.6 or newer — standard library only, **nothing to install**
  (tested on Python 3.14)
- Internet access to nuget.org

## How to run

Open a terminal (CMD, PowerShell or Git Bash) in the folder that contains
`nuget_download.py`.

**1. Package id and version on the command line:**

```bash
python nuget_download.py Enums.NET 5.0.0
```

**2. Ask for the version interactively** (the script lists every published
version and you type the one you want):

```bash
python nuget_download.py Enums.NET
```

**3. Fully interactive** — the script shows the menu first:

```bash
python nuget_download.py
```

**4. Download into a different folder** (`-o` / `--output`):

```bash
python nuget_download.py Castle.Core 5.1.1 -o D:\nuget-mirror
```

**5. Auto detect without the menu** (`--auto`, optional `--scan-dir`):

```bash
python nuget_download.py --auto --scan-dir D:\src\MySolution
```

**6. Non-interactive one-shot** (`--no-menu`, for scheduled runs):

```bash
python nuget_download.py Enums.NET 5.0.0 --no-menu
```

> On Windows, if the command `python` is not found, try `py` instead:
> `py nuget_download.py Enums.NET 5.0.0`

**Built-in help:**

```bash
python nuget_download.py --help
```

## Configuration

All settings are constants at the top of `nuget_download.py`:

| Constant                  | Default      | Meaning                                        |
|---------------------------|--------------|------------------------------------------------|
| `DEFAULT_OUTPUT_DIR`      | `"packages"` | output folder used when `-o` is not given      |
| `RETRIES`                 | `3`          | network attempts per request                   |
| `CHUNK_SIZE`              | 64 KB        | download buffer size                           |
| `BAR_WIDTH`               | `24`         | width of the progress bar                      |
| `PROJECT_FILE_SUFFIX`     | `".csproj"`  | project files read by auto detect              |
| `DIRECTORY_PACKAGES_PROPS`| `directory.packages.props` | Central Package Management file |
| `AUTO_DETECT_TAGS`        | `PackageReference`, `PackageVersion` | XML tags read by auto detect |
| `IGNORED_AUTO_PACKAGES`   | framework ids | skipped by auto detect (they are not packages) |
| `SKIP_FOLDERS`            | `bin`, `obj`, `.git`, `.vs`, `node_modules`, `packages` | folders not descended into |

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
(`python nuget_download.py Enums.NET 5.0.0`), that download runs first and the
menu is shown afterwards. Aliases `m`, `auto` and `q` are accepted; anything
else just prints "not one of the menu options" and shows the menu again.

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
  are skipped — remove an entry from `IGNORED_AUTO_PACKAGES` to keep it;
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
  streams).
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
per run with `-o`, or permanently by editing `DEFAULT_OUTPUT_DIR` at the top of
the script. Relative paths resolve from your current working directory;
absolute paths like `D:/nuget-mirror` work too (in the script file use forward
slashes or a raw string `r"D:\nuget-mirror"`).

## Download once, never again

At every start the script scans the output folder and keeps an in-memory list
of the `.nupkg` files already there:

- packages on that list are **never downloaded again**;
- every successful download is added to the list;
- an interrupted run (Ctrl+C, network drop) simply resumes where it stopped —
  just run the same command again.

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

- **`nuget.org is unreachable ... SSL: UNEXPECTED_EOF_WHILE_READING`** — the
  connection to the internet is failing (VPN/proxy off, firewall, no network).
  Test any website in a browser; when the connection is back, just run the
  same command again — nothing is downloaded twice.
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
