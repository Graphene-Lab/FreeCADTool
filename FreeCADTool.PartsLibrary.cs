using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIOrchestrator;

namespace AIOrchestrator.API;

/// <summary>Ready-made complex parts: search the public FreeCAD Parts Library and import the best match, instead of modeling a real-world object from primitives.</summary>
public partial class FreeCADTool
{
    // FreeCAD Parts Library — the community catalog the FreeCAD Addon Manager also indexes.
    // It is ~5.8 GB, so it is NEVER cloned: the file list comes from the GitHub tree API and
    // only the single winning file is downloaded. The library carries no LICENSE file proper
    // (GitHub reports NOASSERTION / "Other"); parts are published for reuse by the project.
    private const string PartsLibraryRepo = "FreeCAD/FreeCAD-library";
    private const string PartsLibraryBranch = "master";
    private const string PartsLibraryRawBase = "https://raw.githubusercontent.com/" + PartsLibraryRepo + "/" + PartsLibraryBranch + "/";
    private const string PartsLibraryIndexPath = "/.freecad-library-index.json";

    // The index is fetched at most once a week and reused from the sandbox cache afterwards.
    private static readonly TimeSpan PartsIndexMaxAge = TimeSpan.FromDays(7);

    // A CAD part big enough to matter is a few MB; this only stops a runaway mesh download.
    private const long PartsMaxDownloadBytes = 200L * 1024 * 1024;

    /// <summary>File extensions kept in the index. FCStd is the native parametric document (first choice); step/stp, iges/igs and stl are lower-priority fallbacks imported as an unparametric shape.</summary>
    private static readonly HashSet<string> PartsExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".FCStd", ".step", ".stp", ".iges", ".igs", ".stl" };

    /// <summary>Get a complex ready-made part instead of modeling it: search the public FreeCAD Parts Library (thousands of contributed CAD files) for an English name, download the best match into the workspace and import it into the active document, creating one when none is open. Call this FIRST for any real-world object that primitives cannot reproduce — an astronaut, a spaceship, a robot arm, a gearbox, a bearing — and only then consider modeling. Among the matches it keeps the one whose path matches the name best, then the most complete document (the library publishes no rating, and a bigger file carries more detail). If nothing matches, the method returns "Error:" and building from primitives is the remaining option. The name must be English: translate it first ("astronave" → "spaceship", "vite" → "screw").</summary>
    /// <param name="name">English search name of the part, plain catalog words (e.g. "spur gear", "robot arm", "ball bearing") — not a path or a file name. One or two words work best.</param>
    /// <returns>The imported part with its workspace path, the import into the document, how many candidates were considered, the top alternatives (call again with a different name to pick another) and the object names now in the document — or "Error:" when the library has no match, or the index/download/import failed.</returns>
    public string GetComplexPart(string name)
    {
        Log.LogStep($"FreeCADTool.GetComplexPart: {name}");
        if (string.IsNullOrWhiteSpace(name))
            return "Error: get_complex_part needs an English part name (e.g. \"spur gear\", \"robot arm\", \"ball bearing\").";

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("FreeCADTool/1.0 (+https://github.com/Graphene-Lab/FreeCADTool)");

        var files = LoadPartsIndex(http, out var indexNote, out var indexError);
        if (files == null) return indexError!;
        Log.LogStep($"FreeCADTool.GetComplexPart: index ready ({files.Count} files, {indexNote})");

        // Deterministic ranking:
        //   tier 0 = the file's leaf name IS the query (normalized),
        //   tier 1 = every query token appears in the normalized path,
        //   tier 2 = only some tokens appear in the path,
        // inside a tier MORE MATCHED TOKENS FIRST, then LARGER FILE FIRST — a bigger document means
        // more features and detail, which is the only quality signal this source has: the GitHub tree
        // API exposes no rating/vote, so if the library ever ships one, prefer the best-rated inside
        // the tier instead of size.
        var query = NormalizePartText(name).Trim();
        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToArray();
        var matches = files.Where(f => PartsMatchTier(f.Path, query, tokens) >= 0).ToList();
        if (matches.Count == 0)
            return $"Error: the FreeCAD Parts Library ({PartsLibraryRepo}) has no part matching '{name}' — {files.Count} library files were searched and nothing matched. Nothing was imported; model the object from primitives instead (create_primitive, sketch, feature) or import your own file with import_file.";

        // FCStd first: a native document keeps its parametric feature tree, an imported
        // STEP/IGES/STL arrives as a dead shape.
        var pool = matches.Where(f => f.IsFcstd).ToList();
        var fallback = pool.Count == 0;
        if (fallback) pool = matches;
        var ranked = pool.OrderBy(f => PartsMatchTier(f.Path, query, tokens))
                         .ThenByDescending(f => PartsMatchedTokens(f.Path, tokens))
                         .ThenByDescending(f => f.Size)
                         .ThenBy(f => f.Path, StringComparer.Ordinal)
                         .ToList();

        var winner = ranked[0];
        var leaf = Path.GetFileName(winner.Path);
        var agentTarget = "/" + SanitizeLeafName(leaf);
        string hostTarget;
        try { hostTarget = SandboxPath.Resolve(agentTarget); }
        catch (Exception ex) { return $"Error: cannot place '{leaf}' in the workspace — {ex.Message}"; }

        try { DownloadPartFile(http, winner.Path, hostTarget); }
        catch (Exception ex) { return $"Error: downloading '{winner.Path}' from the FreeCAD Parts Library failed — {ex.Message}"; }
        Log.LogStep($"FreeCADTool.GetComplexPart: downloaded {winner.Path} ({winner.Size} bytes) → {agentTarget}");

        var r = Run($$"""
import os, re
path = {{Py(hostTarget)}}
if not os.path.exists(path): raise FileNotFoundError('downloaded file missing')
ext = os.path.splitext(path)[1].lower()
doc = FreeCAD.ActiveDocument
if doc is None: doc = FreeCAD.newDocument('PartsLibrary')
before = set(o.Name for o in doc.Objects)

def _merge_project():
    doc.mergeProject(path)

def _import_insert():
    import Import
    Import.insert(path, doc.Name)

def _import_mesh():
    import Mesh
    base = re.sub(r'[^A-Za-z0-9_]', '', os.path.splitext(os.path.basename(path))[0])
    o = doc.addObject('Mesh::Feature', 'Imported_' + base)
    o.Mesh = Mesh.read(path)

# A native .FCStd is merged (it keeps its parametric feature tree); STEP/IGES go through the
# Import module; a mesh (.stl) is NOT a format Import registers in a headless session
# ("no supported file format"), so it is read with the Mesh workbench instead.
# Each format falls back to the next attempt when FreeCAD refuses the file.
if ext == '.fcstd': attempts = [('mergeProject', _merge_project), ('Import.insert', _import_insert)]
elif ext == '.stl': attempts = [('Mesh.read', _import_mesh), ('Import.insert', _import_insert)]
else: attempts = [('Import.insert', _import_insert)]
used, last = attempts[0][0], None
for step, fn in attempts:
    try:
        fn(); used = step; last = None; break
    except Exception as e:
        last = e
if last is not None: raise last
try:
    doc.recompute()
    note = ''
except Exception as e:
    note = str(e)
new = [o.Name for o in doc.Objects if o.Name not in before]
_result_ = {'doc': doc.Name, 'new': new, 'total': len(doc.Objects), 'how': used, 'note': note}
""");
        if (!r.Success) return Err(r, $"import '{leaf}' into the document")!;
        Log.LogStep($"FreeCADTool.GetComplexPart: imported '{leaf}' via {Field(r, "how")} into '{Field(r, "doc")}'");

        var exact = matches.Count(f => PartsMatchTier(f.Path, query, tokens) == 0);
        var allTokens = matches.Count(f => PartsMatchTier(f.Path, query, tokens) == 1);
        var alternatives = ranked.Skip(1).Take(3).Select(f => $"{Path.GetFileName(f.Path)} ({Kb(f.Size)})").ToList();

        var sb = new StringBuilder();
        if (fallback)
            sb.Append($"No FreeCAD document (.FCStd) matched, so the {Path.GetExtension(leaf).TrimStart('.').ToUpperInvariant()} fallback was imported — it is a non-parametric shape with no feature history.\n");
        sb.Append($"Imported '{leaf}' ({Kb(winner.Size)}) from the FreeCAD Parts Library into document '{Field(r, "doc")}'.\n");
        sb.Append($"- workspace: {agentTarget}   library: {winner.Path}\n");
        sb.Append($"- match: {MatchText(query, tokens, winner.Path)}\n");
        sb.Append($"- imported objects: {Field(r, "new")} (document now has {Field(r, "total")} objects), via {Field(r, "how")}\n");
        sb.Append($"- candidates: {matches.Count} of {files.Count} indexed files ({exact} exact-name, {allTokens} all-token, {matches.Count - exact - allTokens} partial); {indexNote}\n");
        if (alternatives.Count > 0)
            sb.Append($"- other candidates (best match first): {string.Join(", ", alternatives)} — call get_complex_part again with a different English name if this one is not the right geometry\n");
        if (!string.IsNullOrEmpty(Field(r, "note")))
            sb.Append($"- recompute reported: {Field(r, "note")}\n");
        sb.Append("Next: inspect_object or export the imported objects; adjust them with the normal modeling methods.");
        return sb.ToString().TrimEnd();
    }

    /// <summary>Human description of how well the chosen file matched the query.</summary>
    private static string MatchText(string query, string[] tokens, string path)
    {
        var tier = PartsMatchTier(path, query, tokens);
        if (tier == 0) return "exact name match";
        if (tier == 1) return "all name words present in the library path";
        var hit = tokens.Where(t => ContainsWord(NormalizePartText(path), t)).ToList();
        var miss = tokens.Except(hit).ToList();
        return $"partial — only {string.Join(", ", hit.Select(h => "\"" + h + "\""))} matched" +
               (miss.Count > 0 ? $", {string.Join(", ", miss.Select(m => "\"" + m + "\""))} is not in the library — verify the geometry" : "");
    }

    // ─────────────────────────────────────────────────────────────────────
    // Index (fetch, cache, rank)
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>One indexed library file: path inside the repository and size in bytes.
    /// Short JSON names keep the sandbox cache small.</summary>
    private sealed class PartsIndexEntry
    {
        [JsonPropertyName("p")] public string Path { get; set; } = "";
        [JsonPropertyName("s")] public long Size { get; set; }
        [JsonIgnore] public bool IsFcstd => System.IO.Path.GetExtension(Path).Equals(".FCStd", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>On-disk shape of the sandbox index cache.</summary>
    private sealed class PartsIndex
    {
        public string Fetched { get; set; } = "";
        public string Source { get; set; } = "";
        public List<PartsIndexEntry> Files { get; set; } = new();
    }

    /// <summary>Failure carrying an agent-facing message (already AgentBridge-style, no host paths).</summary>
    private sealed class PartsLibraryException : Exception
    {
        public PartsLibraryException(string message) : base(message) { }
    }

    /// <summary>Return the library index, from the sandbox cache when it is younger than a week,
    /// otherwise by fetching it once from GitHub and re-writing the cache. Never per call.</summary>
    private List<PartsIndexEntry>? LoadPartsIndex(HttpClient http, out string note, out string? error)
    {
        note = "";
        error = null;
        string indexPath;
        try { indexPath = SandboxPath.Resolve(PartsLibraryIndexPath); }
        catch (Exception ex)
        {
            error = $"Error: cannot reach the workspace to cache the FreeCAD Parts Library index — {ex.Message}";
            return null;
        }

        var cached = ReadPartsIndex(indexPath);
        if (cached != null && DateTimeOffset.TryParse(cached.Fetched, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var fetched))
        {
            var age = DateTimeOffset.UtcNow - fetched;
            if (age >= TimeSpan.Zero && age < PartsIndexMaxAge)
            {
                note = $"index reused from cache ({cached.Files.Count} files, fetched {fetched:yyyy-MM-dd HH:mm} UTC)";
                Log.LogStep($"FreeCADTool.GetComplexPart: index cache hit at {PartsLibraryIndexPath} — {cached.Files.Count} files, {age.TotalDays:F1} days old");
                return cached.Files;
            }
        }

        List<PartsIndexEntry> files;
        string source;
        try { files = FetchPartsIndex(http, out source); }
        catch (PartsLibraryException ex) { error = "Error: " + ex.Message; return null; }
        catch (Exception ex) { error = $"Error: reading the FreeCAD Parts Library index failed — {ex.Message}"; return null; }
        if (files.Count == 0) { error = "Error: the FreeCAD Parts Library index came back empty — the library listing changed or is unavailable. Retry later, or model from primitives."; return null; }

        Log.LogStep($"FreeCADTool.GetComplexPart: index fetched from GitHub ({source}, {files.Count} files) → {PartsLibraryIndexPath}");
        note = $"index fetched from GitHub just now ({files.Count} files from {PartsLibraryRepo}@{PartsLibraryBranch})";
        try { File.WriteAllText(indexPath, JsonSerializer.Serialize(new PartsIndex { Fetched = DateTimeOffset.UtcNow.ToString("o"), Source = source, Files = files })); }
        catch (Exception ex) { Log.LogStep($"FreeCADTool.GetComplexPart: index cache write failed — {ex.Message}"); }
        return files;
    }

    /// <summary>Read the sandbox index cache; null when it is missing, unreadable or corrupt (a
    /// damaged cache must never break the call — it is simply refetched).</summary>
    private static PartsIndex? ReadPartsIndex(string indexPath)
    {
        try
        {
            if (!File.Exists(indexPath)) return null;
            var idx = JsonSerializer.Deserialize<PartsIndex>(File.ReadAllText(indexPath));
            return idx != null && idx.Files.Count > 0 ? idx : null;
        }
        catch (Exception ex) { Log.LogStep($"FreeCADTool.GetComplexPart: unusable index cache — {ex.Message}"); return null; }
    }

    /// <summary>Build the file list from GitHub: the recursive tree API in one request, or — when
    /// that listing comes back <c>truncated</c> — one <c>contents</c> request per top-level
    /// directory (shallow, but it keeps the index usable without a repository clone).</summary>
    private static List<PartsIndexEntry> FetchPartsIndex(HttpClient http, out string source)
    {
        var tree = GetPartsJson(http, $"https://api.github.com/repos/{PartsLibraryRepo}/git/trees/{PartsLibraryBranch}?recursive=1");
        var files = new List<PartsIndexEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var topDirs = new List<string>();
        var truncated = tree.TryGetProperty("truncated", out var tr) && tr.ValueKind == JsonValueKind.True;
        if (tree.TryGetProperty("tree", out var entries) && entries.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in entries.EnumerateArray())
            {
                var path = e.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                if (string.IsNullOrEmpty(path)) continue;
                var type = e.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type == "tree")
                {
                    if (path.IndexOf('/') < 0) topDirs.Add(path);
                    continue;
                }
                var size = e.TryGetProperty("size", out var s) && s.TryGetInt64(out var sv) ? sv : 0;
                AddPartFile(files, seen, path, size);
            }
        }
        if (!truncated) { source = "GitHub tree API"; return files; }

        Log.LogStep($"FreeCADTool.GetComplexPart: tree listing truncated — falling back to the GitHub contents API over {topDirs.Count} top-level directories");
        foreach (var dir in topDirs)
        {
            try
            {
                var listing = GetPartsJson(http, $"https://api.github.com/repos/{PartsLibraryRepo}/contents/{Uri.EscapeDataString(dir)}?ref={PartsLibraryBranch}");
                if (listing.ValueKind != JsonValueKind.Array) continue;
                foreach (var e in listing.EnumerateArray())
                {
                    if ((e.TryGetProperty("type", out var t) ? t.GetString() : null) != "file") continue; // sub-directories stay uncrawled: the fallback is one level deep
                    var path = e.TryGetProperty("path", out var p) ? p.GetString() : null;
                    if (string.IsNullOrEmpty(path)) continue;
                    var size = e.TryGetProperty("size", out var s) && s.TryGetInt64(out var sv) ? sv : 0;
                    AddPartFile(files, seen, path, size);
                }
            }
            catch (PartsLibraryException ex) { Log.LogStep($"FreeCADTool.GetComplexPart: skipping '{dir}' — {ex.Message}"); }
        }
        source = "GitHub contents API (tree listing was truncated)";
        return files;
    }

    /// <summary>Keep a library entry when its extension is one of <see cref="PartsExtensions"/> and it is not a duplicate.</summary>
    private static bool AddPartFile(List<PartsIndexEntry> files, HashSet<string> seen, string path, long size)
    {
        if (!PartsExtensions.Contains(Path.GetExtension(path))) return false;
        if (!seen.Add(path)) return false;
        files.Add(new PartsIndexEntry { Path = path, Size = size });
        return true;
    }

    /// <summary>GET a GitHub API document, cloned out of its parser. Failures become
    /// <see cref="PartsLibraryException"/> with a clear, host-path-free message; the anonymous
    /// rate limit (60 requests/hour) is called out explicitly because it is the one failure the
    /// agent can only fix by waiting.</summary>
    private static JsonElement GetPartsJson(HttpClient http, string url)
    {
        using var resp = http.GetAsync(url).GetAwaiter().GetResult();
        if (!resp.IsSuccessStatusCode)
        {
            var status = (int)resp.StatusCode;
            var remaining = resp.Headers.TryGetValues("X-RateLimit-Remaining", out var v) ? v.FirstOrDefault() : null;
            if (status is 403 or 429 && (remaining == null || remaining == "0"))
                throw new PartsLibraryException($"GitHub rate limit reached while reading the parts library index (HTTP {status}, anonymous callers get 60 requests/hour). Wait a few minutes and retry — the index is cached in the workspace for a week, so this only happens on the first call.");
            throw new PartsLibraryException($"GitHub returned HTTP {status} while reading the parts library index ({url}).");
        }
        using var doc = JsonDocument.Parse(resp.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        return doc.RootElement.Clone();
    }

    /// <summary>Download the winning library file to its sandbox path. Path segments are
    /// URL-encoded individually (the library uses spaces in directory names).</summary>
    private static void DownloadPartFile(HttpClient http, string libraryPath, string hostTarget)
    {
        var url = PartsLibraryRawBase + string.Join("/", libraryPath.Split('/').Select(Uri.EscapeDataString));
        using var resp = http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        if (!resp.IsSuccessStatusCode) throw new IOException($"HTTP {(int)resp.StatusCode} from raw.githubusercontent.com");
        var length = resp.Content.Headers.ContentLength;
        if (length > PartsMaxDownloadBytes)
            throw new IOException($"the file is {length / (1024 * 1024)} MB — above the {PartsMaxDownloadBytes / (1024 * 1024)} MB import limit");
        Directory.CreateDirectory(Path.GetDirectoryName(hostTarget)!);
        using var src = resp.Content.ReadAsStream();
        using var dst = File.Create(hostTarget);
        src.CopyTo(dst);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Name matching helpers
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Fold a name or path for matching: lowercase, with spaces, underscores, hyphens,
    /// dots and directory separators all treated as word separators.</summary>
    private static string NormalizePartText(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s.ToLowerInvariant()) sb.Append(c is ' ' or '_' or '-' or '.' or '/' or '\\' ? ' ' : c);
        return sb.ToString();
    }

    /// <summary>True when a query token occurs in the normalized path. Tokens of at least four
    /// characters also match on that prefix, so "robot" finds "Robots/" and "gears" finds "gear"
    /// — library folders are plural more often than not.</summary>
    private static bool ContainsWord(string normalizedText, string token)
    {
        if (normalizedText.Contains(token, StringComparison.Ordinal)) return true;
        return token.Length >= 4 && normalizedText.Contains(token[..4], StringComparison.Ordinal);
    }

    /// <summary>How many query tokens the path satisfies (used to order partial matches:
    /// a path matching two name words beats a path matching one, whatever their sizes).</summary>
    private static int PartsMatchedTokens(string path, string[] tokens)
    {
        var normalized = NormalizePartText(path);
        return tokens.Count(t => ContainsWord(normalized, t));
    }

    /// <summary>Ranking tier of a library path against the query: 0 exact leaf-name match,
    /// 1 every query token present, 2 only some tokens present, -1 no match.</summary>
    private static int PartsMatchTier(string path, string query, string[] tokens)
    {
        var normalized = NormalizePartText(path);
        if (NormalizePartText(Path.GetFileName(path)).Trim() == query) return 0;
        var hits = tokens.Count(t => ContainsWord(normalized, t));
        if (hits == tokens.Length) return 1;
        return hits > 0 ? 2 : -1;
    }

    /// <summary>File-system-safe file name (library file names are already plain, this only
    /// guards the rare odd character).</summary>
    private static string SanitizeLeafName(string leaf)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(leaf.Length);
        foreach (var c in leaf) sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return sb.ToString();
    }

    /// <summary>Byte count as a short agent-facing KB/MB string.</summary>
    private static string Kb(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / (1024.0 * 1024.0):F1} MB"
        : $"{bytes / 1024.0:F0} KB";
}
