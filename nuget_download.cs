#!/usr/bin/env dotnet
// The #:property lines below stop the *surrounding project* from hijacking this
// file-based app. A solution-level Directory.Build.props that sets
// RuntimeIdentifier / RuntimeIdentifiers (self-contained, win-x64, AOT) would
// otherwise force the generated nuget_download.cs.csproj to restore
// Microsoft.NETCore.App.Runtime.win-x64 & friends, which fails with NU1101 when
// nuget.org is not among the configured package sources. Clearing them keeps the
// build a plain framework-dependent console app, and the extra source makes the
// restore work even if the folder's NuGet.config <clear/>s the default feeds.
#:property RuntimeIdentifier=
#:property RuntimeIdentifiers=
#:property SelfContained=false
#:property PublishAotUsingRuntimePack=false
#:property PublishReadyToRun=false
#:property RestoreAdditionalProjectSources=https://api.nuget.org/v3/index.json
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

// Recursively download a NuGet package and all of its dependencies from nuget.org.
// C# port of nuget_download.py (same behaviour, same output layout).
//
// Run with:  dotnet run nuget_download.cs -- <PackageId> [Version] [-o OUTPUT_DIR]
//            dotnet run nuget_download.cs -- --auto [--scan-dir DIR] [-o OUTPUT_DIR]
// With no package id an interactive menu is shown (1 = manual, 2 = auto detect, 0 = exit).

return await App.RunAsync(args);

static class App
{
    const string FlatContainer = "https://api.nuget.org/v3-flatcontainer";
    const string UserAgent = "nuget-recursive-downloader/1.0";
    const string DefaultOutputDir = "packages";
    const int Retries = 3;
    const int ChunkSize = 1 << 16;
    const int BarWidth = 24;

    const string ProjectFileSuffix = ".csproj";
    const string DirectoryPackagesProps = "directory.packages.props";

    static readonly string[] AutoDetectTags = { "packagereference", "packageversion" };

    // framework/SDK placeholders that carry no real package to download
    static readonly HashSet<string> IgnoredAutoPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "microsoft.netcore.app",
        "microsoft.netcore.app.ref",
        "netstandard.library",
        "microsoft.netcore.targets",
        "microsoft.netcore.platforms",
    };

    static readonly HashSet<string> SkipFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".vs", "node_modules", "packages"
    };

    // ------------------------------------------------------------ versions

    public static async Task<int> RunAsync(string[] args)
    {
        string? package = null;
        string? version = null;
        string output = DefaultOutputDir;
        string? scanDir = null;
        bool auto = false;
        bool noMenu = false;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "-o":
                case "--output":
                    output = i + 1 < args.Length ? args[++i] : output;
                    break;
                case "--scan-dir":
                    scanDir = i + 1 < args.Length ? args[++i] : null;
                    break;
                case "--auto":
                    auto = true;
                    break;
                case "--no-menu":
                    noMenu = true;
                    break;
                case "-h":
                case "--help":
                    PrintUsage();
                    return 0;
                default:
                    if (a.StartsWith("--output=")) { output = a[9..]; break; }
                    if (a.StartsWith("--scan-dir=")) { scanDir = a[11..]; break; }
                    if (a.StartsWith("-o") && a.Length > 2) { output = a[2..]; break; }
                    if (a.StartsWith("-")) { Console.Error.WriteLine($"Unknown option '{a}'."); PrintUsage(); return 2; }
                    if (package is null) package = a; else if (version is null) version = a;
                    break;
            }
        }

        try
        {
            if (auto)
            {
                int autoResult = await RunAutoDetectAsync(output, scanDir);
                return autoResult;
            }

            if (package is not null)
            {
                await RunManualAsync(output, package, version);
                if (noMenu) return 0;
            }

            return await ShowMenuAsync(output, scanDir);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("\nInterrupted.");
            return 130;
        }
    }

    public static void PrintUsage()
    {
        Console.WriteLine("""
        Recursively download a NuGet package and all of its dependencies from
        nuget.org (only .nupkg files are saved, flat in the output folder).

        Usage:
          nuget_download.cs <PackageId> [Version] [-o OUTPUT_DIR]
          nuget_download.cs --auto [--scan-dir DIR] [-o OUTPUT_DIR]

        Options:
          -o, --output DIR      output directory (default: ./packages)
              --scan-dir DIR    folder to scan for projects (default: current folder)
              --auto            skip the menu and run auto detect on this folder
              --no-menu         do not show the menu when the download finishes
        """);
    }

    // ------------------------------------------------------------ versions

    sealed class NuVersion : IComparable<NuVersion>
    {
        public int[] Release { get; }
        public string[] Pre { get; }

        private NuVersion(int[] release, string[] pre)
        {
            Release = release;
            Pre = pre;
        }

        public static readonly Regex VersionRe = new(
            @"^\s*v?(\d+(?:\.\d+){0,3})(?:-([0-9A-Za-z][0-9A-Za-z.-]*))?(?:\+[0-9A-Za-z.-]+)?\s*$",
            RegexOptions.Compiled);

        public static NuVersion? TryParse(string? text)
        {
            if (text is null) return null;
            var m = VersionRe.Match(text);
            if (!m.Success) return null;
            var parts = m.Groups[1].Value.Split('.').Select(int.Parse).ToArray();
            var release = new int[4];
            for (int i = 0; i < parts.Length && i < 4; i++) release[i] = parts[i];
            var pre = m.Groups[2].Success ? m.Groups[2].Value.Split('.') : Array.Empty<string>();
            return new NuVersion(release, pre);
        }

        private static int ComparePre(string[] a, string[] b)
        {
            if (a.Length == 0 && b.Length == 0) return 0;
            if (a.Length == 0) return 1;   // stable sorts above prerelease
            if (b.Length == 0) return -1;
            int n = Math.Max(a.Length, b.Length);
            for (int i = 0; i < n; i++)
            {
                if (i >= a.Length) return -1;
                if (i >= b.Length) return 1;
                if (a[i] == b[i]) continue;
                bool na = int.TryParse(a[i], out int ia);
                bool nb = int.TryParse(b[i], out int ib);
                if (na && nb) return ia < ib ? -1 : 1;
                if (na != nb) return na ? -1 : 1;  // numeric identifiers sort below
                return string.CompareOrdinal(a[i], b[i]) < 0 ? -1 : 1;
            }
            return 0;
        }

        public int CompareTo(NuVersion? other)
        {
            if (other is null) return 1;
            for (int i = 0; i < 4; i++)
            {
                if (Release[i] != other.Release[i]) return Release[i] < other.Release[i] ? -1 : 1;
            }
            return ComparePre(Pre, other.Pre);
        }
    }

    sealed class VersionRange
    {
        public NuVersion? Lo;
        public NuVersion? Hi;
        public bool LoInclusive;
        public bool HiInclusive;

        public static readonly VersionRange Any = new()
        { Lo = null, Hi = null, LoInclusive = true, HiInclusive = true };

        public static readonly Regex BracketRe = new(
            @"^([\[(])\s*(.*?)\s*,\s*(.*?)\s*([\])])$", RegexOptions.Compiled);

        /// <summary>Returns null when the constraint cannot be recognised.</summary>
        public static VersionRange? TryParse(string? constraint)
        {
            string text = (constraint ?? "").Trim();
            if (text is "" or "*") return Any;

            var m = BracketRe.Match(text);
            if (m.Success)
            {
                string loText = m.Groups[2].Value, hiText = m.Groups[3].Value;
                return new VersionRange
                {
                    Lo = loText.Length > 0 ? NuVersion.TryParse(loText) : null,
                    Hi = hiText.Length > 0 ? NuVersion.TryParse(hiText) : null,
                    LoInclusive = m.Groups[1].Value == "[",
                    HiInclusive = m.Groups[4].Value == "]"
                };
            }

            if (text.EndsWith(".*", StringComparison.Ordinal))  // floating: 1.2.* -> [1.2, 1.3)
            {
                string baseText = text[..^2];
                var b = NuVersion.TryParse(baseText);
                if (b is null) return Any;
                var parts = baseText.TrimStart('v').Split('.');
                NuVersion? hi = null;
                if (int.TryParse(parts[^1], out int last))
                {
                    parts[^1] = (last + 1).ToString();
                    hi = NuVersion.TryParse(string.Join(".", parts));
                }
                return new VersionRange { Lo = b, Hi = hi, LoInclusive = true, HiInclusive = false };
            }

            var exact = NuVersion.TryParse(text);
            if (exact is null) return null;   // unrecognised constraint
            // A bare version is an inclusive minimum; lowest-match selection makes it
            // behave as an exact pin whenever that version is still published.
            return new VersionRange { Lo = exact, Hi = null, LoInclusive = true, HiInclusive = true };
        }

        public bool Satisfies(NuVersion v)
        {
            if (Lo is not null)
            {
                int c = v.CompareTo(Lo);
                if (c < 0 || (c == 0 && !LoInclusive)) return false;
            }
            if (Hi is not null)
            {
                int c = v.CompareTo(Hi);
                if (c > 0 || (c == 0 && !HiInclusive)) return false;
            }
            return true;
        }
    }

    /// <summary>Candidate versions for a dependency constraint, most preferred first.</summary>
    public static List<string> ResolveCandidates(string? constraint, IReadOnlyList<string> available)
    {
        var rng = VersionRange.TryParse(constraint) ?? VersionRange.Any;

        var parsed = new List<(NuVersion V, string Raw)>();
        foreach (var raw in available)
        {
            var v = NuVersion.TryParse(raw);
            if (v is not null) parsed.Add((v, raw));
        }

        var pool = parsed.Where(p => p.V.Pre.Length == 0 && rng.Satisfies(p.V)).ToList();
        if (pool.Count == 0)
            pool = parsed.Where(p => rng.Satisfies(p.V)).ToList();
        pool.Sort((a, b) => a.V.CompareTo(b.V));

        var ascending = pool.Select(p => p.Raw).ToList();
        if (rng.Lo is null) ascending.Reverse();   // no lower bound: newest that fits
        return ascending;
    }

    /// <summary>Match a user-supplied version to the exact string nuget.org publishes.</summary>
    public static string? NormalizeVersion(string requested, IReadOnlyList<string> available)
    {
        requested = requested.Trim();
        foreach (var raw in available)
            if (string.Equals(raw, requested, StringComparison.OrdinalIgnoreCase)) return raw;

        var wanted = NuVersion.TryParse(requested);
        if (wanted is null) return null;
        foreach (var raw in available)
        {
            var published = NuVersion.TryParse(raw);
            if (published is not null && published.CompareTo(wanted) == 0) return raw;
        }
        return null;
    }

    // ---------------------------------------------------------------- http

    // Version lists used during this run, kept in memory only.
    static readonly Dictionary<string, string[]> VersionCache = new(StringComparer.OrdinalIgnoreCase);
    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All
        })
        { Timeout = TimeSpan.FromSeconds(120) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        return client;
    }

    sealed class HttpStatusError : Exception
    {
        public int Code { get; }
        public HttpStatusError(int code, string message) : base(message) => Code = code;
    }

    public static async Task<byte[]> HttpGetBytesAsync(string url, CancellationToken ct = default)
    {
        Exception? lastError = null;
        for (int attempt = 1; attempt <= Retries; attempt++)
        {
            try
            {
                using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct)
                                           .ConfigureAwait(false);
                int code = (int)response.StatusCode;
                if (code is 404 or 410) throw new HttpStatusError(code, $"{url}: HTTP {code}");
                if (!response.IsSuccessStatusCode) throw new HttpStatusError(code, $"{url}: HTTP {code}");
                return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            }
            catch (HttpStatusError) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                lastError = ex;
            }
            if (attempt < Retries) await Task.Delay(TimeSpan.FromSeconds(1 << attempt), ct).ConfigureAwait(false);
        }
        throw new IOException($"{url}: {lastError?.Message}", lastError);
    }

    public static async Task<string[]> GetAvailableVersionsAsync(string packageId, CancellationToken ct = default)
    {
        string key = packageId.ToLowerInvariant();
        if (VersionCache.TryGetValue(key, out var cached)) return cached;

        string url = $"{FlatContainer}/{Uri.EscapeDataString(key)}/index.json";
        string[] versions;
        try
        {
            byte[] data = await HttpGetBytesAsync(url, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(data);
            versions = doc.RootElement.TryGetProperty("versions", out var arr)
                ? arr.EnumerateArray().Select(e => e.GetString() ?? "").ToArray()
                : Array.Empty<string>();
        }
        catch (HttpStatusError ex) when (ex.Code is 404 or 410)
        {
            versions = Array.Empty<string>();
        }
        VersionCache[key] = versions;
        return versions;
    }

    /// <summary>Lowercase filenames of the .nupkg files already in the output folder.</summary>
    public static HashSet<string> ScanExistingFiles(string outputDir)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(outputDir)) return existing;
        foreach (string path in Directory.EnumerateFiles(outputDir))
        {
            if (!path.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase)) continue;
            if (new FileInfo(path).Length > 0) existing.Add(Path.GetFileName(path));
        }
        return existing;
    }

    public static string HumanSize(long numBytes)
    {
        double size = numBytes;
        string[] units = { "B", "KB", "MB", "GB" };
        for (int i = 0; i < units.Length; i++)
        {
            if (size < 1024 || i == units.Length - 1)
                return units[i] == "B" ? $"{size:N0} B" : $"{size:N1} {units[i]}";
            size /= 1024;
        }
        return $"{size:N1} GB";
    }

    static string ShortLabel(string label, int maxLength = 50)
        => label.Length <= maxLength ? label : label[..(maxLength - 3)] + "...";

    static int DrawProgress(string label, long doneBytes, long totalBytes, int previousWidth)
    {
        if (Console.IsOutputRedirected) return 0;   // keep redirected logs clean
        label = ShortLabel(label);
        string text;
        if (totalBytes > 0)
        {
            double ratio = Math.Min((double)doneBytes / totalBytes, 1.0);
            int filled = (int)Math.Round(ratio * BarWidth);
            string bar = new string('#', filled) + new string('-', BarWidth - filled);
            text = $"{label} [{bar}] {ratio,6:P1} {HumanSize(doneBytes)} / {HumanSize(totalBytes)}";
        }
        else
        {
            text = $"{label} {HumanSize(doneBytes)}";
        }
        Console.Write("\r" + text + new string(' ', Math.Max(previousWidth - text.Length, 0)));
        return text.Length;
    }

    /// <summary>Download one .nupkg unless already listed; returns (path, alreadyHadIt).</summary>
    public static async Task<(string Path, bool Already)> DownloadNupkgAsync(
        string packageId, string version, string outputDir, HashSet<string> existing, CancellationToken ct = default)
    {
        string idLower = packageId.ToLowerInvariant();
        string filename = $"{idLower}.{version}.nupkg";
        string path = Path.Combine(outputDir, filename);
        if (existing.Contains(filename)) return (path, true);

        string url = $"{FlatContainer}/{Uri.EscapeDataString(idLower)}/{Uri.EscapeDataString(version)}/{Uri.EscapeDataString(filename)}";
        Directory.CreateDirectory(outputDir);

        Exception? lastError = null;
        for (int attempt = 1; attempt <= Retries; attempt++)
        {
            try
            {
                using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                                           .ConfigureAwait(false);
                int code = (int)response.StatusCode;
                if (code is 404 or 410) throw new HttpStatusError(code, $"{url}: HTTP {code}");
                response.EnsureSuccessStatusCode();

                long total = response.Content.Headers.ContentLength ?? 0;
                string part = path + ".part";
                long done = 0;
                int drawnWidth = 0;
                DateTime lastDraw = DateTime.MinValue;
                try
                {
                    await using var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    await using var target = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None,
                                                            ChunkSize, useAsync: true);
                    byte[] buffer = new byte[ChunkSize];
                    int read;
                    while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        done += read;
                        DateTime now = DateTime.UtcNow;
                        if ((now - lastDraw).TotalMilliseconds >= 100)
                        {
                            drawnWidth = Math.Max(drawnWidth,
                                DrawProgress($"+ {packageId} {version}", done, total, drawnWidth));
                            lastDraw = now;
                        }
                    }
                }
                finally
                {
                    if (drawnWidth > 0) Console.Write("\r" + new string(' ', drawnWidth) + "\r");
                }

                File.Move(part, path, overwrite: true);
                existing.Add(filename);   // successful download: update the in-memory list
                return (path, false);
            }
            catch (HttpStatusError) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                lastError = ex;
            }
            TryDelete(path + ".part");
            if (attempt < Retries) await Task.Delay(TimeSpan.FromSeconds(1 << attempt), ct).ConfigureAwait(false);
        }
        throw new IOException($"{url}: {lastError?.Message}", lastError);
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
    }

    // ---------------------------------------------------------------- nuspec

    /// <summary>(dependencyId, versionConstraint) pairs from the .nuspec inside a .nupkg.</summary>
    public static List<(string Id, string Constraint)> ExtractDependencies(string nupkgPath)
    {
        var dependencies = new List<(string, string)>();
        using var archive = ZipFile.OpenRead(nupkgPath);
        var entry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
        if (entry is null) return dependencies;

        XDocument doc;
        using (var stream = entry.Open())
        {
            try
            {
                doc = XDocument.Load(stream);
            }
            catch (System.Xml.XmlException ex)
            {
                Console.Error.WriteLine($"  ! could not parse .nuspec in {nupkgPath}: {ex.Message}");
                return dependencies;
            }
        }

        foreach (var element in doc.Descendants())
        {
            if (element.Name.LocalName != "dependency") continue;
            string? depId = element.Attribute("id")?.Value;
            if (!string.IsNullOrEmpty(depId))
                dependencies.Add((depId, element.Attribute("version")?.Value ?? "*"));
        }
        return dependencies;
    }

    // ---------------------------------------------------------------- graph walk

    public static async Task<(int Downloaded, int Reused, List<(string Label, string Reason)> Failures)>
        DownloadTreeAsync(string rootId, string rootVersion, string outputDir, CancellationToken ct = default)
    {
        var existing = ScanExistingFiles(outputDir);
        if (existing.Count > 0)
            Console.WriteLine($"Found {existing.Count} existing .nupkg file(s) in '{outputDir}'.");

        var pending = new Queue<(string Id, string Version)>();
        pending.Enqueue((rootId, rootVersion));
        var visited = new HashSet<(string, string)>();
        int downloaded = 0, reused = 0;
        var failures = new List<(string, string)>();

        while (pending.Count > 0)
        {
            var (packageId, version) = pending.Dequeue();
            var key = (packageId.ToLowerInvariant(), version);
            if (!visited.Add(key)) continue;

            string label = $"{packageId} {version}";
            string path;
            bool already;
            try
            {
                (path, already) = await DownloadNupkgAsync(packageId, version, outputDir, existing, ct)
                                                 .ConfigureAwait(false);
            }
            catch (HttpStatusError ex)
            {
                Console.WriteLine($"  ! {label}: not downloadable (HTTP {ex.Code})");
                failures.Add((label, $"HTTP {ex.Code}"));
                continue;
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException)
            {
                Console.WriteLine($"  ! {label}: {ex.Message}");
                failures.Add((label, ex.Message));
                continue;
            }

            if (already)
            {
                reused++;
                Console.WriteLine($"= {label} (already downloaded, {HumanSize(new FileInfo(path).Length)})");
            }
            else
            {
                downloaded++;
                Console.WriteLine($"+ {label} ({HumanSize(new FileInfo(path).Length)})");
            }

            List<(string Id, string Constraint)> dependencies;
            try
            {
                dependencies = ExtractDependencies(path);
            }
            catch (InvalidDataException ex)
            {
                Console.WriteLine($"  ! {label}: unreadable .nupkg ({ex.Message})");
                failures.Add((label, "corrupt .nupkg"));
                continue;
            }

            foreach (var (depId, constraint) in dependencies)
            {
                string depLabel = $"{depId} (needed by {packageId})";
                string[] available;
                try
                {
                    available = await GetAvailableVersionsAsync(depId, ct).ConfigureAwait(false);
                }
                catch (HttpStatusError ex)
                {
                    Console.WriteLine($"  ! {depLabel}: lookup failed, HTTP {ex.Code}");
                    failures.Add((depLabel, $"HTTP {ex.Code}"));
                    continue;
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException)
                {
                    Console.WriteLine($"  ! {depLabel}: {ex.Message}");
                    failures.Add((depLabel, ex.Message));
                    continue;
                }

                if (available.Length == 0)
                {
                    string note = "package not found on nuget.org";
                    Console.WriteLine($"  ! {depLabel}: {note}");
                    failures.Add((depLabel, note));
                    continue;
                }

                var candidates = ResolveCandidates(constraint, available);
                string? chosen = candidates.FirstOrDefault(c => !visited.Contains((depId.ToLowerInvariant(), c)));
                if (chosen is null)
                {
                    if (candidates.Count > 0) continue;   // every candidate was already processed
                    string note = $"no published version satisfies '{constraint}'";
                    Console.WriteLine($"  ! {depLabel}: {note}");
                    failures.Add((depLabel, note));
                    continue;
                }
                pending.Enqueue((depId, chosen));
            }
        }

        return (downloaded, reused, failures);
    }

    // ---------------------------------------------------------------- auto detect

    /// <summary>Every .csproj and Directory.Packages.props under root, nested, sorted.</summary>
    public static List<string> FindProjectFiles(string root)
    {
        var matches = new List<string>();
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root)) return matches;
        foreach (string dir in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
        {
            if (SkipFolders.Contains(Path.GetFileName(dir))) continue;
            matches.AddRange(FindProjectFiles(dir));
        }
        foreach (string file in Directory.EnumerateFiles(root).OrderBy(f => f, StringComparer.Ordinal))
        {
            string lower = Path.GetFileName(file).ToLowerInvariant();
            if (lower.EndsWith(ProjectFileSuffix, StringComparison.Ordinal) || lower == DirectoryPackagesProps)
                matches.Add(file);
        }
        return matches;
    }

    static string LocalTag(XElement element) => element.Name.LocalName.ToLowerInvariant();

    /// <summary>(id, versionOrNull, sourceFile) triples declared in one XML file.</summary>
    public static List<(string Id, string? Version, string Path)> ReadPackagesFromProject(string path)
    {
        var found = new List<(string, string?, string)>();
        XDocument root;
        try
        {
            root = XDocument.Load(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            Console.Error.WriteLine($"  ! skipped {path}: {ex.Message}");
            return found;
        }

        foreach (var element in root.Descendants())
        {
            if (!AutoDetectTags.Contains(LocalTag(element))) continue;
            string packageId = (element.Attribute("Include")?.Value
                               ?? element.Attribute("Update")?.Value
                               ?? element.Attribute("id")?.Value
                               ?? "").Trim();
            string version = (element.Attribute("Version")?.Value ?? "").Trim();
            if (packageId.Length == 0) continue;
            // version is null for Central Package Management, where the csproj only says
            // <PackageReference Include="X" /> and Directory.Packages.props holds the version.
            found.Add((packageId, version.Length == 0 ? null : version, path));
        }
        return found;
    }

    /// <summary>id.lower() -> version, from every Directory.Packages.props under root.</summary>
    public static Dictionary<string, string> ReadCentralVersions(string root)
    {
        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in FindProjectFiles(root))
        {
            if (!Path.GetFileName(path).Equals(DirectoryPackagesProps, StringComparison.OrdinalIgnoreCase)) continue;
            XDocument propsRoot;
            try
            {
                propsRoot = XDocument.Load(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                continue;
            }
            foreach (var element in propsRoot.Descendants())
            {
                if (LocalTag(element) != "packageversion") continue;
                string packageId = (element.Attribute("Include")?.Value ?? element.Attribute("Update")?.Value ?? "").Trim();
                string version = (element.Attribute("Version")?.Value ?? "").Trim();
                if (packageId.Length > 0 && version.Length > 0)
                    versions.TryAdd(packageId.ToLowerInvariant(), version);
            }
        }
        return versions;
    }

    /// <summary>Unique (packageId, version) pairs declared under root.</summary>
    public static List<(string Id, string? Version)> CollectProjects(string root)
    {
        root = Path.GetFullPath(root);
        var projects = FindProjectFiles(root);
        if (projects.Count == 0)
        {
            Console.WriteLine($"No {ProjectFileSuffix} or {DirectoryPackagesProps} file found under '{root}'.");
            return new();
        }

        Console.WriteLine($"Found {projects.Count} project file(s) under '{root}':");
        foreach (string path in projects)
            Console.WriteLine($"  - {Path.GetRelativePath(root, path)}");

        var pairs = new Dictionary<string, (string Id, string? Version)>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in projects)
        {
            var entries = ReadPackagesFromProject(path);
            if (entries.Count == 0) continue;
            Console.WriteLine($"    {Path.GetRelativePath(root, path)}: {entries.Count} reference(s)");
            foreach (var (packageId, version, _) in entries)
            {
                if (IgnoredAutoPackages.Contains(packageId)) continue;
                if (!pairs.TryGetValue(packageId, out var slot))
                    pairs[packageId] = (packageId, version);
                else if (slot.Version is null && version is not null)
                    pairs[packageId] = (slot.Id, version);   // prefer a file that pins a version
            }
        }

        if (pairs.Count == 0)
        {
            Console.WriteLine("\nNo <PackageReference> or <PackageVersion> entries were found.");
            return new();
        }

        var missing = pairs.Where(p => p.Value.Version is null).Select(p => p.Key).ToList();
        if (missing.Count > 0)
        {
            Console.WriteLine($"\nResolving {missing.Count} version-less reference(s) from {DirectoryPackagesProps} ...");
            var central = ReadCentralVersions(root);
            foreach (string key in missing)
            {
                var slot = pairs[key];
                pairs[key] = (slot.Id, central.TryGetValue(key, out var v) ? v : null);  // null -> newest stable
            }
        }

        Console.WriteLine($"\nDetected {pairs.Count} package reference(s):");
        foreach (string key in pairs.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            var (packageId, version) = pairs[key];
            Console.WriteLine($"  {packageId} {version ?? "(newest)"}");
        }
        return pairs.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => p.Value).ToList();
    }

    // ---------------------------------------------------------------- cli

    /// <summary>Read one answer; returns null when the input stream is closed.</summary>
    static string? Ask(string prompt)
    {
        Console.Write(prompt);
        Console.Out.Flush();
        string? line = Console.ReadLine();
        if (line is null)
        {
            Console.WriteLine();
            return null;
        }
        return line.Trim();
    }

    static string Wrap(string text, int width, string indent)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        int lineLen = indent.Length;
        foreach (string word in words)
        {
            if (lineLen + word.Length + 1 > width && lineLen > indent.Length)
            {
                sb.AppendLine();
                sb.Append(indent);
                lineLen = indent.Length;
            }
            else if (lineLen > indent.Length)
            {
                sb.Append(' ');
                lineLen++;
            }
            sb.Append(word);
            lineLen += word.Length;
        }
        return sb.ToString();
    }

    /// <summary>Manual entry: ask for (or accept) a package id + version, download it.</summary>
    public static async Task<int?> RunManualAsync(string outputDir, string? packageId, string? version)
    {
        packageId ??= Ask("Package id: ");
        if (string.IsNullOrEmpty(packageId))
        {
            Console.WriteLine("No package id given.\n");
            return null;
        }

        string[] available;
        try
        {
            available = await GetAvailableVersionsAsync(packageId).ConfigureAwait(false);
        }
        catch (HttpStatusError ex)
        {
            Console.WriteLine($"nuget.org lookup for '{packageId}' failed: HTTP {ex.Code}");
            return 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            Console.WriteLine($"nuget.org is unreachable: {ex.Message}");
            return 1;
        }

        if (available.Length == 0)
        {
            Console.WriteLine($"Package '{packageId}' was not found on nuget.org.");
            return 1;
        }

        if (!string.IsNullOrEmpty(version))
        {
            string? resolved = NormalizeVersion(version, available);
            if (resolved is null)
            {
                Console.WriteLine($"Version '{version}' of {packageId} was not found.\nAvailable versions:\n" +
                                  Wrap(string.Join(", ", available), 88, "  "));
                return 1;
            }
            version = resolved;
        }
        else
        {
            Console.WriteLine($"Available versions of {packageId}:\n" +
                              Wrap(string.Join(", ", available), 88, "  ") + "\n");
            version = null;
            while (version is null)
            {
                string? requested = Ask($"Version to download for {packageId}: ");
                if (string.IsNullOrEmpty(requested))
                {
                    Console.WriteLine("No version given.\n");
                    return null;
                }
                version = NormalizeVersion(requested, available);
                if (version is null) Console.WriteLine($"'{requested}' is not a published version, try again.");
            }
        }

        Console.WriteLine($"\nDownloading {packageId} {version} and every dependency to '{outputDir}'\n");
        var (downloaded, reused, failures) = await DownloadTreeAsync(packageId, version, outputDir).ConfigureAwait(false);

        Console.WriteLine($"\nDone: {downloaded + reused} package(s) under '{outputDir}' " +
                          $"({downloaded} downloaded now, {reused} already present).");
        ReportFailures(failures);
        return failures.Count > 0 ? 1 : 0;
    }

    /// <summary>Scan .csproj / Directory.Packages.props under scanRoot and download all.</summary>
    public static async Task<int> RunAutoDetectAsync(string outputDir, string? scanRoot)
    {
        scanRoot ??= Directory.GetCurrentDirectory();
        var references = CollectProjects(scanRoot);
        if (references.Count == 0) return 1;

        var failures = new List<(string, string)>();
        foreach (var (packageId, packageVersion) in references)
        {
            string version = packageVersion!;
            if (version is null)   // no version anywhere: take the newest stable one
            {
                string[] available;
                try
                {
                    available = await GetAvailableVersionsAsync(packageId).ConfigureAwait(false);
                }
                catch (HttpStatusError ex)
                {
                    string note = $"lookup failed, HTTP {ex.Code}";
                    Console.WriteLine($"  ! {packageId}: {note}");
                    failures.Add((packageId, note));
                    continue;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException)
                {
                    Console.WriteLine($"  ! {packageId}: {ex.Message}");
                    failures.Add((packageId, ex.Message));
                    continue;
                }
                var candidates = ResolveCandidates(null, available);
                version = candidates.Count > 0 ? candidates[0] : "";
                if (version.Length == 0)
                {
                    string note = "package not found on nuget.org";
                    Console.WriteLine($"  ! {packageId}: {note}");
                    failures.Add((packageId, note));
                    continue;
                }
            }
            Console.WriteLine($"\nDownloading {packageId} {version} and every dependency to '{outputDir}'\n");
            var (_, _, fails) = await DownloadTreeAsync(packageId, version, outputDir).ConfigureAwait(false);
            failures.AddRange(fails);
        }

        Console.WriteLine($"\nDone: {references.Count} reference(s) processed under '{outputDir}'.");
        ReportFailures(failures);
        return failures.Count > 0 ? 1 : 0;
    }

    public static void ReportFailures(List<(string Label, string Reason)> failures)
    {
        if (failures.Count == 0) return;
        Console.WriteLine($"{failures.Count} item(s) could not be resolved:");
        foreach (var (label, reason) in failures)
            Console.WriteLine($"  - {label}: {reason}");
    }

    const string MenuText = """
    =================== NuGet downloader ===================
      1) Manual entry   - type a package id and version
      2) Auto detect    - read every .csproj / Directory.Packages.props here
      0) Exit
    ========================================================
    """;

    /// <summary>Loop until the user picks an option (or input is closed / redirected).</summary>
    public static async Task<int> ShowMenuAsync(string outputDir, string? scanDir)
    {
        while (true)
        {
            Console.WriteLine(MenuText);
            string? choice = Ask("Choose 1, 2 or 0: ");
            if (choice is null) return 0;   // closed input
            choice = choice.ToLowerInvariant();

            if (choice is "" or "0" or "q" or "quit" or "exit")
            {
                Console.WriteLine("Bye.");
                return 0;
            }
            if (choice is "1" or "m" or "manual")
            {
                await RunManualAsync(outputDir, null, null).ConfigureAwait(false);
            }
            else if (choice is "2" or "a" or "auto" or "auto detect" or "autodetect")
            {
                await RunAutoDetectAsync(outputDir, scanDir).ConfigureAwait(false);
            }
            else
            {
                Console.WriteLine($"'{choice}' is not one of the menu options.\n");
            }
        }
    }
}
