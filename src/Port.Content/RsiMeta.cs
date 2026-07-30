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
            if (File.Exists(c))
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
                var exact = Path.Combine(c, preferredState + ".png");
                if (!File.Exists(exact))
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
        var preferred = doc.States.FirstOrDefault(s =>
            string.Equals(s.Name, preferredState, StringComparison.OrdinalIgnoreCase));
        if (preferred is null)
            return null;
        var preferredPng = Path.Combine(rsiPathOrDirectory, preferred.Name + ".png");
        if (!File.Exists(preferredPng))
            return null;
        var state = preferred;

        var png = Path.Combine(rsiPathOrDirectory, state.Name + ".png");
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

    /// <summary>IconSmooth connection keys like wall12 / window3 — must not load under the wrong PNG.</summary>
    public static bool LooksLikeIconSmoothStateName(string? stateName)
    {
        if (string.IsNullOrWhiteSpace(stateName) || stateName.Length < 2)
            return false;
        var i = stateName.Length - 1;
        while (i >= 0 && char.IsDigit(stateName[i]))
            i--;
        if (i < 0 || i == stateName.Length - 1)
            return false;
        var digits = stateName.Length - 1 - i;
        if (digits is < 1 or > 2)
            return false;
        var bas = stateName[..(i + 1)];
        return bas.Equals("wall", StringComparison.OrdinalIgnoreCase)
               || bas.Equals("window", StringComparison.OrdinalIgnoreCase)
               || bas.Equals("reinforced", StringComparison.OrdinalIgnoreCase)
               || bas.Equals("reinforced_window", StringComparison.OrdinalIgnoreCase)
               || bas.Equals("grille", StringComparison.OrdinalIgnoreCase)
               || bas.Equals("diagonal", StringComparison.OrdinalIgnoreCase)
               || bas.StartsWith("diagonal_", StringComparison.OrdinalIgnoreCase);
    }
}
