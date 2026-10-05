#!/usr/bin/env python3
"""Recursively download a NuGet package and all of its dependencies from nuget.org.

Only .nupkg files are saved. Each package's dependencies are read from the
.nuspec embedded inside its .nupkg, so the graph is walked until every
referenced package has been downloaded.

Usage:
    python nuget_download.py <PackageId> [Version] [-o OUTPUT_DIR]
    python nuget_download.py --auto [--scan-dir DIR] [-o OUTPUT_DIR]

When the script is started with no package id on the command line it opens an
interactive menu:

    1) Manual entry  - exactly the behaviour described above (ask for id/version);
    2) Auto detect   - walk the folder the script was started in (nested) for
                       *.csproj and Directory.Packages.props, read every
                       <PackageReference ... /> and <PackageVersion ... /> element,
                       extract Include="Id" / Version="x.y.z" and download each
                       package (plus its dependency tree) into the output folder;
    0) Exit.

The menu reappears after every download, so several packages can be queued. If a
package id *is* given on the command line, that download runs first and the menu
is shown afterwards. --auto skips the menu and runs option 2 straight away;
--no-menu keeps the script non-interactive after the first download.

The version may be given on the command line; if omitted, the script lists the
available versions and asks for one.

Version resolution (mirrors NuGet's default restore behaviour):
  * dependency version constraints are read from each package's .nuspec;
  * a range with a lower bound resolves to the lowest stable version that
    satisfies it (if that file is no longer on the feed, the next candidate
    is tried automatically);
  * a range with only an upper bound, or no constraint at all ("*" / empty),
    resolves to the newest stable version that fits;
  * prereleases are used only when no stable version satisfies the range.

Every .nupkg is saved directly in the output folder as <id>.<version>.nupkg
(e.g. packages/enums.net.5.0.0.nupkg), with no subfolders.

At every start the script scans the output folder and keeps an in-memory list
of the .nupkg files already there. Packages on that list are never downloaded
again, and every successful download is added to the list, so an interrupted
run resumes exactly where it stopped. Nothing but .nupkg files is ever
written to the output folder.
"""

import argparse
import json
import os
import re
import sys
import textwrap
import time
import zipfile
import xml.etree.ElementTree as ET
from collections import deque
from functools import cmp_to_key
from urllib.error import HTTPError, URLError
from urllib.parse import quote
from urllib.request import Request, urlopen

FLAT_CONTAINER = "https://api.nuget.org/v3-flatcontainer"
USER_AGENT = "nuget-recursive-downloader/1.0"
DEFAULT_OUTPUT_DIR = r"packages"  # default output folder (change here if you like)
RETRIES = 3
CHUNK_SIZE = 1 << 16
BAR_WIDTH = 24

# auto-detect mode: file names scanned (nested) and XML tags read from them
PROJECT_FILE_SUFFIX = ".csproj"
DIRECTORY_PACKAGES_PROPS = "directory.packages.props"
AUTO_DETECT_TAGS = ("packagereference", "packageversion")
# framework/SDK placeholders that carry no real package to download
IGNORED_AUTO_PACKAGES = {
    "microsoft.netcore.app",
    "microsoft.netcore.app.ref",
    "netstandard.library",
    "microsoft.netcore.targets",
    "microsoft.netcore.platforms",
}

# ---------------------------------------------------------------- versions

_VERSION_RE = re.compile(
    r"^\s*v?(\d+(?:\.\d+){0,3})"
    r"(?:-([0-9A-Za-z][0-9A-Za-z.-]*))?"
    r"(?:\+[0-9A-Za-z.-]+)?\s*$"
)


def parse_version(text):
    """Parse a NuGet version into (release_tuple_padded_to_4, prerelease_tuple)."""
    match = _VERSION_RE.match(text or "")
    if not match:
        return None
    release = tuple(int(part) for part in match.group(1).split("."))
    release += (0,) * (4 - len(release))
    prerelease = tuple(match.group(2).split(".")) if match.group(2) else ()
    return release, prerelease


def _compare_prerelease(a, b):
    if a == b:
        return 0
    if not a:  # a is stable, b is a prerelease -> stable sorts higher
        return 1
    if not b:
        return -1
    for part_a, part_b in zip(a, b):
        if part_a == part_b:
            continue
        num_a, num_b = part_a.isdigit(), part_b.isdigit()
        if num_a and num_b:
            return -1 if int(part_a) < int(part_b) else 1
        if num_a != num_b:  # numeric identifiers sort below alphanumeric
            return -1 if num_a else 1
        return -1 if part_a < part_b else 1
    return (len(a) > len(b)) - (len(a) < len(b))


def compare_versions(a, b):
    if a[0] != b[0]:
        return -1 if a[0] < b[0] else 1
    return _compare_prerelease(a[1], b[1])


def parse_range(constraint):
    """Parse a NuGet version range into (lo, hi, lo_inclusive, hi_inclusive)."""
    text = (constraint or "").strip()
    if text in ("", "*"):
        return None, None, True, True

    bracketed = re.match(r"^([\[(])\s*(.*?)\s*,\s*(.*?)\s*([\])])$", text)
    if bracketed:
        lo_text, hi_text = bracketed.group(2), bracketed.group(3)
        lo = parse_version(lo_text) if lo_text else None
        hi = parse_version(hi_text) if hi_text else None
        return lo, hi, bracketed.group(1) == "[", bracketed.group(4) == "]"

    if text.endswith(".*"):  # floating, e.g. 1.2.* -> [1.2, 1.3)
        base_text = text[:-2]
        base = parse_version(base_text)
        if base is None:
            return None, None, True, True
        parts = base_text.lstrip("v").split(".")
        hi = None
        if parts[-1].isdigit():
            parts[-1] = str(int(parts[-1]) + 1)
            hi = parse_version(".".join(parts))
        return base, hi, True, False

    exact = parse_version(text)
    if exact is None:
        raise ValueError(f"unrecognised version constraint {constraint!r}")
    # A bare version is an inclusive minimum; lowest-match selection makes it
    # behave as an exact pin whenever that version is still published.
    return exact, None, True, True


def satisfies(version, rng):
    lo, hi, lo_inclusive, hi_inclusive = rng
    if lo is not None:
        order = compare_versions(version, lo)
        if order < 0 or (order == 0 and not lo_inclusive):
            return False
    if hi is not None:
        order = compare_versions(version, hi)
        if order > 0 or (order == 0 and not hi_inclusive):
            return False
    return True


def resolve_candidates(constraint, available):
    """Candidate versions for a dependency constraint, most preferred first."""
    try:
        rng = parse_range(constraint)
    except ValueError:
        rng = None, None, True, True

    parsed = []
    for raw in available:
        version = parse_version(raw)
        if version is not None:
            parsed.append((version, raw))

    stable = [(v, raw) for v, raw in parsed if not v[1] and satisfies(v, rng)]
    pool = stable or [(v, raw) for v, raw in parsed if satisfies(v, rng)]
    pool.sort(key=cmp_to_key(lambda a, b: compare_versions(a[0], b[0])))

    ascending = [raw for _, raw in pool]
    if rng[0] is None:  # no lower bound: prefer the newest version that fits
        ascending.reverse()
    return ascending


def normalize_version(requested, available):
    """Match a user-supplied version to the exact string nuget.org publishes."""
    requested = requested.strip()
    for raw in available:
        if raw.lower() == requested.lower():
            return raw
    wanted = parse_version(requested)
    if wanted is None:
        return None
    for raw in available:
        published = parse_version(raw)
        if published is not None and compare_versions(published, wanted) == 0:
            return raw
    return None

# ---------------------------------------------------------------- http

# Version lists used during this run, kept in memory only.
_version_cache = {}


def http_get_bytes(url):
    last_error = None
    for attempt in range(1, RETRIES + 1):
        try:
            request = Request(url, headers={"User-Agent": USER_AGENT})
            with urlopen(request, timeout=60) as response:
                return response.read()
        except HTTPError as error:
            if error.code in (404, 410):
                raise
            last_error = error
        except (URLError, OSError) as error:
            last_error = error
        if attempt < RETRIES:
            time.sleep(2 ** attempt)
    raise ConnectionError(f"{url}: {last_error}") from last_error


def get_available_versions(package_id):
    key = package_id.lower()
    if key not in _version_cache:
        url = f"{FLAT_CONTAINER}/{quote(key)}/index.json"
        try:
            data = json.loads(http_get_bytes(url).decode("utf-8"))
        except HTTPError as error:
            if error.code in (404, 410):
                data = {}
            else:
                raise
        _version_cache[key] = data.get("versions", [])
    return _version_cache[key]


def _discard(path):
    try:
        os.remove(path)
    except OSError:
        pass


def scan_existing_files(output_dir):
    """Lowercase filenames of the .nupkg files already in the output folder."""
    existing = set()
    if os.path.isdir(output_dir):
        for name in os.listdir(output_dir):
            if not name.lower().endswith(".nupkg"):
                continue
            path = os.path.join(output_dir, name)
            if os.path.isfile(path) and os.path.getsize(path) > 0:
                existing.add(name.lower())
    return existing


def _short_label(label, max_length=50):
    if len(label) <= max_length:
        return label
    return label[: max_length - 3] + "..."


def _draw_progress(label, done_bytes, total_bytes, previous_width):
    """Redraw one progress line; returns its width so the next draw can erase it."""
    if not sys.stdout.isatty():
        return 0  # output is redirected: keep logs clean, no live redraws
    label = _short_label(label)
    if total_bytes:
        ratio = min(done_bytes / total_bytes, 1.0)
        filled = int(round(ratio * BAR_WIDTH))
        bar = "#" * filled + "-" * (BAR_WIDTH - filled)
        text = (f"{label} [{bar}] {ratio:5.1%} "
                f"{human_size(done_bytes)} / {human_size(total_bytes)}")
    else:
        text = f"{label} {human_size(done_bytes)}"
    padding = " " * max(previous_width - len(text), 0)
    sys.stdout.write("\r" + text + padding)
    sys.stdout.flush()
    return len(text)


def _stream_with_progress(response, target, total_bytes, label):
    """Copy response -> target in chunks, redrawing a progress line (TTY only)."""
    done = 0
    drawn_width = 0
    last_draw = 0.0
    try:
        while True:
            chunk = response.read(CHUNK_SIZE)
            if not chunk:
                return done
            target.write(chunk)
            done += len(chunk)
            now = time.monotonic()
            if now - last_draw >= 0.1:
                drawn_width = max(drawn_width,
                                  _draw_progress(label, done, total_bytes, drawn_width))
                last_draw = now
    finally:
        if drawn_width:  # wipe the progress line; the caller prints the result
            sys.stdout.write("\r" + " " * drawn_width + "\r")
            sys.stdout.flush()


def download_nupkg(package_id, version, output_dir, existing):
    """Download one .nupkg unless it is already listed; returns (path, already_had_it)."""
    id_lower = package_id.lower()
    filename = f"{id_lower}.{version}.nupkg"
    path = os.path.join(output_dir, filename)
    if filename in existing:
        return path, True

    url = f"{FLAT_CONTAINER}/{quote(id_lower)}/{quote(version)}/{quote(filename)}"
    os.makedirs(output_dir, exist_ok=True)

    last_error = None
    for attempt in range(1, RETRIES + 1):
        try:
            request = Request(url, headers={"User-Agent": USER_AGENT})
            with urlopen(request, timeout=120) as response:
                total = int(response.headers.get("Content-Length") or 0)
                with open(path + ".part", "wb") as target:
                    _stream_with_progress(response, target, total,
                                          f"+ {package_id} {version}")
            os.replace(path + ".part", path)
            existing.add(filename)  # successful download: update the in-memory list
            return path, False
        except HTTPError as error:
            if error.code in (404, 410):
                raise
            last_error = error
        except (URLError, OSError) as error:
            last_error = error
        _discard(path + ".part")
        if attempt < RETRIES:
            time.sleep(2 ** attempt)
    raise ConnectionError(f"{url}: {last_error}") from last_error

# ---------------------------------------------------------------- nuspec

def extract_dependencies(nupkg_path):
    """(dependency_id, version_constraint) pairs from the .nuspec inside a .nupkg."""
    with zipfile.ZipFile(nupkg_path) as archive:
        nuspec_name = next(
            (name for name in archive.namelist() if name.lower().endswith(".nuspec")),
            None,
        )
        if nuspec_name is None:
            return []
        nuspec_bytes = archive.read(nuspec_name)

    try:
        root = ET.fromstring(nuspec_bytes)
    except ET.ParseError as error:
        print(f"  ! could not parse .nuspec in {nupkg_path}: {error}", file=sys.stderr)
        return []

    dependencies = []
    for element in root.iter():
        tag = element.tag.rsplit("}", 1)[-1] if isinstance(element.tag, str) else ""
        if tag == "dependency":
            dep_id = element.get("id")
            if dep_id:
                dependencies.append((dep_id, element.get("version") or "*"))
    return dependencies

# ---------------------------------------------------------------- graph walk

def human_size(num_bytes):
    size = float(num_bytes)
    for unit in ("B", "KB", "MB", "GB"):
        if size < 1024 or unit == "GB":
            return f"{size:,.0f} {unit}" if unit == "B" else f"{size:,.1f} {unit}"
        size /= 1024


def download_tree(root_id, root_version, output_dir):
    existing = scan_existing_files(output_dir)
    if existing:
        print(f"Found {len(existing)} existing .nupkg file(s) in '{output_dir}'.")

    pending = deque([(root_id, root_version)])
    visited = set()
    downloaded = 0
    reused = 0
    failures = []

    while pending:
        package_id, version = pending.popleft()
        key = (package_id.lower(), version)
        if key in visited:
            continue
        visited.add(key)

        label = f"{package_id} {version}"
        try:
            path, already = download_nupkg(package_id, version, output_dir, existing)
        except HTTPError as error:
            print(f"  ! {label}: not downloadable (HTTP {error.code})")
            failures.append((label, f"HTTP {error.code}"))
            continue
        except (ConnectionError, OSError) as error:
            print(f"  ! {label}: {error}")
            failures.append((label, str(error)))
            continue

        if already:
            reused += 1
            print(f"= {label} (already downloaded, {human_size(os.path.getsize(path))})")
        else:
            downloaded += 1
            print(f"+ {label} ({human_size(os.path.getsize(path))})")

        try:
            dependencies = extract_dependencies(path)
        except zipfile.BadZipFile as error:
            print(f"  ! {label}: unreadable .nupkg ({error})")
            failures.append((label, "corrupt .nupkg"))
            continue

        for dep_id, constraint in dependencies:
            dep_label = f"{dep_id} (needed by {package_id})"
            try:
                available = get_available_versions(dep_id)
            except HTTPError as error:
                print(f"  ! {dep_label}: lookup failed, HTTP {error.code}")
                failures.append((dep_label, f"HTTP {error.code}"))
                continue
            except ConnectionError as error:
                print(f"  ! {dep_label}: {error}")
                failures.append((dep_label, str(error)))
                continue

            if not available:
                note = "package not found on nuget.org"
                print(f"  ! {dep_label}: {note}")
                failures.append((dep_label, note))
                continue

            candidates = resolve_candidates(constraint, available)
            chosen = next(
                (c for c in candidates if (dep_id.lower(), c) not in visited),
                None,
            )
            if chosen is None:
                if candidates:
                    continue  # every candidate was already processed
                note = f"no published version satisfies '{constraint}'"
                print(f"  ! {dep_label}: {note}")
                failures.append((dep_label, note))
                continue
            pending.append((dep_id, chosen))

    return downloaded, reused, failures

# ---------------------------------------------------------------- auto detect

SKIP_FOLDERS = {"bin", "obj", ".git", ".vs", "node_modules", "packages"}


def find_project_files(root):
    """Every .csproj and Directory.Packages.props under root, nested, sorted."""
    matches = []
    root = os.path.abspath(root)
    for current, dirs, files in os.walk(root):
        dirs[:] = sorted(d for d in dirs if d not in SKIP_FOLDERS)
        for name in sorted(files):
            lower = name.lower()
            if lower.endswith(PROJECT_FILE_SUFFIX) or lower == DIRECTORY_PACKAGES_PROPS:
                matches.append(os.path.join(current, name))
    return matches


def _local_tag(element):
    tag = element.tag
    if not isinstance(tag, str):
        return ""
    return tag.rsplit("}", 1)[-1].lower()


def _package_version_of(element, path):
    """Read id/version out of a <PackageReference>/<PackageVersion> element."""
    package_id = (element.get("Include") or element.get("Update")
                  or element.get("id") or "").strip()
    version = (element.get("Version") or "").strip()
    if not package_id:
        return None
    # version is None for Central Package Management, where the csproj only
    # says <PackageReference Include="X" /> and Directory.Packages.props
    # holds the real version.
    return (package_id, version or None, path)


def read_packages_from_project(path):
    """(id, version_or_None, source_file) triples declared in one XML file."""
    found = []
    try:
        with open(path, "rb") as handle:
            root = ET.fromstring(handle.read())
    except (OSError, ET.ParseError) as error:
        print(f"  ! skipped {path}: {error}", file=sys.stderr)
        return found

    for element in root.iter():
        if _local_tag(element) in AUTO_DETECT_TAGS:
            entry = _package_version_of(element, path)
            if entry:
                found.append(entry)
    return found


def read_central_versions(root):
    """id.lower() -> version, from every Directory.Packages.props under root."""
    versions = {}
    for path in find_project_files(root):
        if os.path.basename(path).lower() != DIRECTORY_PACKAGES_PROPS:
            continue
        try:
            with open(path, "rb") as handle:
                props_root = ET.fromstring(handle.read())
        except (OSError, ET.ParseError):
            continue
        for element in props_root.iter():
            if _local_tag(element) != "packageversion":
                continue
            package_id = (element.get("Include")
                          or element.get("Update") or "").strip()
            version = (element.get("Version") or "").strip()
            if package_id and version:
                versions.setdefault(package_id.lower(), version)
    return versions


def collect_projects(root):
    """Unique (package_id, version) pairs declared under root."""
    root = os.path.abspath(root)
    projects = find_project_files(root)
    if not projects:
        print(f"No {PROJECT_FILE_SUFFIX} or {DIRECTORY_PACKAGES_PROPS} file "
              f"found under '{root}'.")
        return []

    print(f"Found {len(projects)} project file(s) under '{root}':")
    for path in projects:
        print(f"  - {os.path.relpath(path, root)}")

    pairs = {}  # id.lower() -> [id, version or None]
    for path in projects:
        entries = read_packages_from_project(path)
        if not entries:
            continue
        print(f"    {os.path.relpath(path, root)}: {len(entries)} reference(s)")
        for package_id, version, _source in entries:
            key = package_id.lower()
            if key in IGNORED_AUTO_PACKAGES:
                continue
            slot = pairs.get(key)
            if slot is None:
                pairs[key] = [package_id, version]
            elif slot[1] is None and version is not None:
                slot[1] = version  # prefer a file that actually pins a version

    if not pairs:
        print("\nNo <PackageReference> or <PackageVersion> entries were found.")
        return []

    missing = [key for key, (_id, version) in pairs.items() if version is None]
    if missing:
        print(f"\nResolving {len(missing)} version-less reference(s) from "
              f"{DIRECTORY_PACKAGES_PROPS} ...")
        central = read_central_versions(root)
        for key in missing:
            pairs[key][1] = central.get(key)  # stays None -> newest stable later

    print(f"\nDetected {len(pairs)} package reference(s):")
    for key in sorted(pairs):
        package_id, version = pairs[key]
        print(f"  {package_id} {version or '(newest)'}")
    return [tuple(pairs[key]) for key in sorted(pairs)]

# ---------------------------------------------------------------- cli

def ask(prompt):
    """Read one answer; returns None when the input stream is closed."""
    try:
        return input(prompt).strip()
    except (EOFError, KeyboardInterrupt):
        print()
        return None


def run_manual(output_dir, package_id=None, version=None):
    """Manual entry: ask for (or accept) a package id + version, download it.

    Returns 0 on success, 1 on error and None when the user gave no answer
    (Ctrl+C / closed input), which simply sends the caller back to the menu.
    """
    if not package_id:
        package_id = ask("Package id: ")
    if not package_id:
        print("No package id given.\n")
        return None

    try:
        available = get_available_versions(package_id)
    except HTTPError as error:
        print(f"nuget.org lookup for '{package_id}' failed: HTTP {error.code}")
        return 1
    except (URLError, ConnectionError, OSError) as error:
        print(f"nuget.org is unreachable: {error}")
        return 1

    if not available:
        print(f"Package '{package_id}' was not found on nuget.org.")
        return 1

    if version:
        resolved = normalize_version(version, available)
        if resolved is None:
            listed = textwrap.fill(", ".join(available), width=88,
                                   initial_indent="  ", subsequent_indent="  ")
            print(f"Version '{version}' of {package_id} was not found.\n"
                  f"Available versions:\n{listed}")
            return 1
        version = resolved
    else:
        listed = textwrap.fill(", ".join(available), width=88,
                               initial_indent="  ", subsequent_indent="  ")
        print(f"Available versions of {package_id}:\n{listed}\n")
        version = None
        while version is None:
            requested = ask(f"Version to download for {package_id}: ")
            if not requested:
                print("No version given.\n")
                return None
            version = normalize_version(requested, available)
            if version is None:
                print(f"'{requested}' is not a published version, try again.")

    print(f"\nDownloading {package_id} {version} and every dependency "
          f"to '{output_dir}'\n")
    downloaded, reused, failures = download_tree(package_id, version, output_dir)

    print(f"\nDone: {downloaded + reused} package(s) under '{output_dir}' "
          f"({downloaded} downloaded now, {reused} already present).")
    report_failures(failures)
    return 1 if failures else 0


def run_auto_detect(output_dir, scan_root=None):
    """Scan .csproj / Directory.Packages.props under scan_root and download all."""
    scan_root = scan_root or os.getcwd()
    references = collect_projects(scan_root)
    if not references:
        return 1

    failures = []
    for package_id, version in references:
        if version is None:  # no version anywhere: take the newest stable one
            try:
                available = get_available_versions(package_id)
            except HTTPError as error:
                note = f"lookup failed, HTTP {error.code}"
                print(f"  ! {package_id}: {note}")
                failures.append((package_id, note))
                continue
            except (URLError, ConnectionError, OSError) as error:
                print(f"  ! {package_id}: {error}")
                failures.append((package_id, str(error)))
                continue
            candidates = resolve_candidates(None, available)
            version = candidates[0] if candidates else None
            if version is None:
                note = "package not found on nuget.org"
                print(f"  ! {package_id}: {note}")
                failures.append((package_id, note))
                continue
        print(f"\nDownloading {package_id} {version} and every dependency "
              f"to '{output_dir}'\n")
        _d, _r, fails = download_tree(package_id, version, output_dir)
        failures.extend(fails)

    print(f"\nDone: {len(references)} reference(s) processed under '{output_dir}'.")
    report_failures(failures)
    return 1 if failures else 0


def report_failures(failures):
    if not failures:
        return
    print(f"{len(failures)} item(s) could not be resolved:")
    for label, reason in failures:
        print(f"  - {label}: {reason}")


MENU = """
=================== NuGet downloader ===================
  1) Manual entry   - type a package id and version
  2) Auto detect    - read every .csproj / Directory.Packages.props here
  0) Exit
========================================================
"""


def show_menu(output_dir, scan_dir=None):
    """Loop until the user picks an option (or input is closed / redirected)."""
    while True:
        print(MENU)
        choice = ask("Choose 1, 2 or 0: ")
        if choice is None:  # Ctrl+C or closed input
            return 0
        choice = choice.lower()

        if choice in ("", "0", "q", "quit", "exit"):
            print("Bye.")
            return 0
        if choice in ("1", "m", "manual"):
            run_manual(output_dir)
        elif choice in ("2", "a", "auto", "auto detect", "autodetect"):
            run_auto_detect(output_dir, scan_dir)
        else:
            print(f"'{choice}' is not one of the menu options.\n")


def main():
    parser = argparse.ArgumentParser(
        description="Recursively download a NuGet package and all of its "
                    "dependencies from nuget.org (.nupkg files only)."
    )
    parser.add_argument("package", nargs="?", help="package id, e.g. Castle.Core")
    parser.add_argument("version", nargs="?",
                        help="package version, e.g. 5.1.1 (prompted for if omitted)")
    parser.add_argument("-o", "--output", default=DEFAULT_OUTPUT_DIR,
                        help=f"output directory (default: ./{DEFAULT_OUTPUT_DIR})")
    parser.add_argument("--auto", action="store_true",
                        help="skip the menu and run auto detect on this folder")
    parser.add_argument("--scan-dir", default=None,
                        help="folder to scan for projects (default: current folder)")
    parser.add_argument("--no-menu", action="store_true",
                        help="do not show the menu when the download finishes")
    args = parser.parse_args()

    if args.auto:  # menu option 2 straight away, no questions asked
        sys.exit(run_auto_detect(args.output, args.scan_dir) or 0)

    if args.package:  # a package was given on the command line: do it first...
        run_manual(args.output, args.package, args.version)
        if args.no_menu:
            sys.exit(0)

    # ...otherwise (and always afterwards) go straight to the menu.
    sys.exit(show_menu(args.output, args.scan_dir))


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        print("\nInterrupted.", file=sys.stderr)
        sys.exit(130)
