using System.Text.Json;
using System.Text.Json.Serialization;

namespace Port.Content;

/// <summary>
/// Minimal RSI reader: exploded .rsi/ folders OR packed .rsic (PNG atlas + embedded meta).
/// </summary>
public static class RsiMeta
{
    public sealed class Document
    {
        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("size")]
        public SizeXy? Size { get; set; }

        [JsonPropertyName("states")]
        public List<State> States { get; set; } = new();
    }

    public sealed class SizeXy
    {
        [JsonPropertyName("x")]
        public int X { get; set; } = 32;

        [JsonPropertyName("y")]
        public int Y { get; set; } = 32;
    }

    public sealed class State
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("directions")]
        public int Directions { get; set; } = 1;

        [JsonPropertyName("delays")]
        public List<List<float>>? Delays { get; set; }
    }

    public readonly record struct FrameInfo(string PngPath, int FrameW, int FrameH, int StateIndex);

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Resolve RSI preview source under content root.
    /// Prefers packed <c>.rsic</c> (MinStation/ACZ), then exploded <c>.rsi/</c> folders.
    /// </summary>
    public static string? FindRsiDirectory(string contentFilesRoot, string rsiRelative)
        => FindRsiSource(contentFilesRoot, rsiRelative)?.Path;

    public readonly record struct RsiSource(string Path, bool IsRsic);

    public static RsiSource? FindRsiSource(
        string contentFilesRoot, string rsiRelative, string? preferredState = null)
    {
        if (string.IsNullOrWhiteSpace(contentFilesRoot) || string.IsNullOrWhiteSpace(rsiRelative))
            return null;

        var rel = rsiRelative.Replace('\\', '/').TrimStart('/');
        if (rel.StartsWith("Textures/", StringComparison.OrdinalIgnoreCase))
            rel = rel["Textures/".Length..];

        var noExt = rel;
        if (noExt.EndsWith(".rsic", StringComparison.OrdinalIgnoreCase))
            noExt = noExt[..^5];
        else if (noExt.EndsWith(".rsi", StringComparison.OrdinalIgnoreCase))
            noExt = noExt[..^4];

        var rsicCandidates = new[]
        {
            Path.Combine(contentFilesRoot, "Textures", noExt.Replace('/', Path.DirectorySeparatorChar) + ".rsic"),
            Path.Combine(contentFilesRoot, "Resources", "Textures", noExt.Replace('/', Path.DirectorySeparatorChar) + ".rsic"),
            Path.Combine(contentFilesRoot, noExt.Replace('/', Path.DirectorySeparatorChar) + ".rsic"),
        };
        foreach (var c in rsicCandidates)
        {
            if (!File.Exists(c))
                continue;
            // If packed atlas parses but lacks the requested state, fall through to folder
            // PNGs (IconSmooth solidN / furniture states). Do not trap on a useless .rsic.
            if (!string.IsNullOrWhiteSpace(preferredState))
            {
                var atlas = RsiAtlas.TryLoad(c);
                if (atlas is not null && !AtlasHasAnyState(atlas, preferredState))
                    continue;
                // Do NOT reject 1-dir numbered states (grille_damaged_0, gsensor0, …).
                // IconSmooth 4-dir DirOverride is handled in RsiAtlas.Sample / GLES ResolveUv.
            }

            return new RsiSource(c, IsRsic: true);
        }

        var dirRel = noExt + ".rsi";
        var dirCandidates = new[]
        {
            Path.Combine(contentFilesRoot, "Textures", dirRel.Replace('/', Path.DirectorySeparatorChar)),
            Path.Combine(contentFilesRoot, "Resources", "Textures", dirRel.Replace('/', Path.DirectorySeparatorChar)),
            Path.Combine(contentFilesRoot, dirRel.Replace('/', Path.DirectorySeparatorChar)),
        };
        foreach (var c in dirCandidates)
        {
            if (!Directory.Exists(c) || !File.Exists(Path.Combine(c, "meta.json")))
                continue;

            // Prefer packed .rsic when the folder is a partial ACZ stub (often only full.png).
            // IconSmooth needs solid0..7 — accepting incomplete folders traps us forever.
            if (!string.IsNullOrWhiteSpace(preferredState))
            {
                if (ResolveExistingStatePng(c, preferredState) is null)
                    continue;
                return new RsiSource(c, IsRsic: false);
            }

            var pngCount = 0;
            try
            {
                foreach (var _ in Directory.EnumerateFiles(c, "*.png"))
                {
                    pngCount++;
                    if (pngCount >= 2) break;
                }
            }
            catch
            {
                /* ignore */
            }

            // Single-PNG folders are almost always editor "full" stubs — wait for .rsic.
            if (pngCount >= 2)
                return new RsiSource(c, IsRsic: false);
        }

        return null;
    }

    public static Document? TryLoad(string rsiDirectory)
    {
        try
        {
            var meta = Path.Combine(rsiDirectory, "meta.json");
            if (!File.Exists(meta))
                return null;
            var json = File.ReadAllText(meta);
            return JsonSerializer.Deserialize<Document>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Pick a preview frame from .rsic file or exploded .rsi folder.</summary>
    public static FrameInfo? TryGetPreviewFrame(string rsiPathOrDirectory, string? preferredState = null)
    {
        if (File.Exists(rsiPathOrDirectory)
            && rsiPathOrDirectory.EndsWith(".rsic", StringComparison.OrdinalIgnoreCase))
        {
            // If meta parsed and lacks the state → refuse (caller / FindRsiSource can use folder).
            // If meta is not yet readable (null) → still bind the PNG so sprites appear;
            // ResolveUv falls back to SingleCellUv until atlas meta succeeds.
            if (!string.IsNullOrWhiteSpace(preferredState))
            {
                var atlas = RsiAtlas.TryLoad(rsiPathOrDirectory);
                if (atlas is not null && !AtlasHasAnyState(atlas, preferredState))
                    return null;
            }

            var size = PngTextChunk.TryReadRsicFrameSize(rsiPathOrDirectory) ?? (32, 32);
            return new FrameInfo(rsiPathOrDirectory, size.W, size.H, 0);
        }

        if (!Directory.Exists(rsiPathOrDirectory))
            return null;

        var doc = TryLoad(rsiPathOrDirectory);
        if (doc is null)
            return null;

        var fw = doc.Size?.X > 0 ? doc.Size.X : 32;
        var fh = doc.Size?.Y > 0 ? doc.Size.Y : 32;
        // Preferred state is required for folder RSI — never pick the first PNG (chairs→sofa).
        if (string.IsNullOrWhiteSpace(preferredState))
            return null;
        var resolvedPng = ResolveExistingStatePng(rsiPathOrDirectory, preferredState);
        if (resolvedPng is null)
            return null;
        var stateName = Path.GetFileNameWithoutExtension(resolvedPng);
        var preferred = doc.States.FirstOrDefault(s =>
            string.Equals(s.Name, stateName, StringComparison.OrdinalIgnoreCase));
        if (preferred is null)
            return null;
        var state = preferred;

        var png = resolvedPng;
        if (!File.Exists(png))
            return null;

        return new FrameInfo(png, fw, fh, 0);
    }

    /// <summary>
    /// Resolve preview frame; if missing, request ACZ on-demand download for .rsic candidates.
    /// </summary>
    public static FrameInfo? TryGetPreviewFrameOrFetch(
        string contentFilesRoot,
        string rsiRelative,
        AczOnDemandFetcher? fetcher,
        Action<string>? log = null,
        string? preferredState = null)
    {
        var src = FindRsiSource(contentFilesRoot, rsiRelative, preferredState);
        if (src is { } s)
            return TryGetPreviewFrame(s.Path, preferredState);

        if (fetcher is null || !fetcher.IsReady)
            return null;

        foreach (var candidate in AczOnDemandFetcher.CandidateTexturePaths(rsiRelative, preferredState))
        {
            var local = fetcher.EnsureFile(candidate, log);
            if (local is null)
                continue;
            var frame = TryGetPreviewFrame(local, preferredState);
            if (frame is not null)
                return frame;
            // meta.json alone — wait for folder pngs; ignore
        }

        return null;
    }

    /// <summary>IconSmooth connection keys like solid3 / riveted12 / wall7 — any numbered base.</summary>
    public static bool LooksLikeIconSmoothStateName(string? stateName)
    {
        if (!IconSmoothInfer.TrySplitNumbered(stateName ?? "", out var bas, out var n))
            return false;
        if (n is < 0 or > 15)
            return false;
        if (bas.Equals("full", StringComparison.OrdinalIgnoreCase)
            || bas.Equals("icon", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    /// <summary>
    /// Goob/CD windows: YAML IconSmooth base is often <c>window</c> while PNGs are <c>rwindowN</c>.
    /// Lights: PC Appearance maps On→<c>base</c>; Goob RSI states use <c>normal</c>.
    /// Damage layers: server may emit <c>grille_damaged_4</c> while RSI only ships 0..3.
    /// </summary>
    public static IEnumerable<string> PreferredStateAlternates(string? preferredState)
    {
        if (string.IsNullOrWhiteSpace(preferredState))
            yield break;
        yield return preferredState;
        if (preferredState.StartsWith("window", StringComparison.OrdinalIgnoreCase)
            && !preferredState.StartsWith("rwindow", StringComparison.OrdinalIgnoreCase))
            yield return "r" + preferredState;
        else if (preferredState.StartsWith("rwindow", StringComparison.OrdinalIgnoreCase)
                 && preferredState.Length > 1)
            yield return preferredState[1..];
        else if (preferredState.Equals("base", StringComparison.OrdinalIgnoreCase))
            yield return "normal";
        else if (preferredState.Equals("normal", StringComparison.OrdinalIgnoreCase))
            yield return "base";
        else if (preferredState.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            yield return "base";
            yield return "normal";
        }

        // Clamp damage tiers (grille_damaged_4 → _3.._0) without touching IconSmooth solidN keys.
        if (IconSmoothInfer.TrySplitNumbered(preferredState, out var stateBase, out var tier)
            && tier > 0
            && stateBase.Contains("damaged", StringComparison.OrdinalIgnoreCase))
        {
            for (var i = tier - 1; i >= 0; i--)
                yield return stateBase + i;
            if (stateBase.StartsWith("grille_damaged", StringComparison.OrdinalIgnoreCase))
            {
                yield return "grille_broken";
                yield return "grille";
            }
        }
    }

    static bool AtlasHasAnyState(RsiAtlas.Loaded atlas, string preferredState)
    {
        foreach (var alt in PreferredStateAlternates(preferredState))
        {
            if (atlas.States.ContainsKey(alt))
                return true;
        }

        return false;
    }

    static string? ResolveExistingStatePng(string rsiDirectory, string preferredState)
    {
        foreach (var alt in PreferredStateAlternates(preferredState))
        {
            var png = Path.Combine(rsiDirectory, alt + ".png");
            if (File.Exists(png))
                return png;
        }

        return null;
    }
}
