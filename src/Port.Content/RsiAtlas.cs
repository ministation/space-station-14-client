using System.Text.Json;
using System.Text.Json.Serialization;

namespace Port.Content;

/// <summary>
/// RSI / .rsic atlas: UV crops for state + direction + animated frame.
/// Matches Robust <c>RsiLoading</c> / <c>RSIResource</c> sheet layout.
/// </summary>
public sealed class RsiAtlas
{
    public readonly record struct UvRect(float U0, float V0, float U1, float V1, float FrameW, float FrameH);

    public sealed class Loaded
    {
        public required string SourcePath { get; init; }
        public required int FrameW { get; init; }
        public required int FrameH { get; init; }
        public required int AtlasW { get; init; }
        public required int AtlasH { get; init; }
        public required int DimX { get; init; }
        public required Dictionary<string, StateInfo> States { get; init; }
        public required int[] FrameCounts { get; init; }
        public required string[] StateOrder { get; init; }
    }

    public sealed class StateInfo
    {
        public required string Name { get; init; }
        public required int DirCount { get; init; }
        public required float[][] Delays { get; init; }
        public required int SheetOffset { get; init; } // first frame index in atlas
        public required int TotalFrames { get; init; }
    }

    sealed class MetaDoc
    {
        [JsonPropertyName("size")]
        public SizeXy? Size { get; set; }

        [JsonPropertyName("states")]
        public List<MetaState>? States { get; set; }
    }

    sealed class SizeXy
    {
        [JsonPropertyName("x")] public int X { get; set; } = 32;
        [JsonPropertyName("y")] public int Y { get; set; } = 32;
    }

    sealed class MetaState
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("directions")] public int? Directions { get; set; }
        [JsonPropertyName("delays")] public List<List<float>>? Delays { get; set; }
    }

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    static readonly Dictionary<string, Loaded?> Cache = new(StringComparer.OrdinalIgnoreCase);
    static readonly object Gate = new();

    public static Loaded? TryLoad(string rsiPathOrDirectory)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(rsiPathOrDirectory, out var hit))
                return hit;
        }

        Loaded? loaded = null;
        try
        {
            if (File.Exists(rsiPathOrDirectory)
                && rsiPathOrDirectory.EndsWith(".rsic", StringComparison.OrdinalIgnoreCase))
                loaded = LoadRsic(rsiPathOrDirectory);
            else if (Directory.Exists(rsiPathOrDirectory))
                loaded = LoadFolder(rsiPathOrDirectory);
        }
        catch
        {
            loaded = null;
        }

        lock (Gate) Cache[rsiPathOrDirectory] = loaded;
        return loaded;
    }

    static Loaded? LoadRsic(string path)
    {
        var json = PngTextChunk.TryReadText(path, "robusttoolbox_rsic_meta");
        if (json is null) return null;
        using var fs = File.OpenRead(path);
        // Peek PNG size via Android-less approach: read IHDR.
        if (!TryReadPngSize(fs, out var aw, out var ah))
            return null;
        return BuildFromMeta(path, json, aw, ah);
    }

    static Loaded? LoadFolder(string dir)
    {
        var metaPath = Path.Combine(dir, "meta.json");
        if (!File.Exists(metaPath)) return null;
        var json = File.ReadAllText(metaPath);
        // Folder RSI: use first state PNG to infer; atlas dim from GenerateAtlas formula after load.
        // We don't blit here — caller still uses per-state PNGs for folder mode.
        // For folder mode, DimX is computed as if states were packed (for UV on single-state sheets).
        var doc = JsonSerializer.Deserialize<MetaDoc>(json, JsonOpts);
        if (doc?.States is null || doc.States.Count == 0) return null;
        var fw = doc.Size?.X > 0 ? doc.Size.X : 32;
        var fh = doc.Size?.Y > 0 ? doc.Size.Y : 32;
        // Prefer first state png dimensions
        var firstPng = Path.Combine(dir, doc.States[0].Name + ".png");
        var aw = fw;
        var ah = fh;
        if (File.Exists(firstPng) && TryReadPngSize(File.OpenRead(firstPng), out var pw, out var ph))
        {
            aw = pw;
            ah = ph;
        }

        return BuildFromMeta(dir, json, aw, ah, folderMode: true);
    }

    static Loaded? BuildFromMeta(string source, string json, int atlasW, int atlasH, bool folderMode = false)
    {
        var doc = JsonSerializer.Deserialize<MetaDoc>(json, JsonOpts);
        if (doc?.States is null || doc.States.Count == 0) return null;
        var fw = Math.Max(1, doc.Size?.X ?? 32);
        var fh = Math.Max(1, doc.Size?.Y ?? 32);

        var order = new string[doc.States.Count];
        var counts = new int[doc.States.Count];
        var map = new Dictionary<string, StateInfo>(StringComparer.OrdinalIgnoreCase);
        var sheetOffset = 0;
        for (var i = 0; i < doc.States.Count; i++)
        {
            var st = doc.States[i];
            var name = st.Name ?? $"state{i}";
            var dirs = st.Directions is 1 or 4 or 8 ? st.Directions.Value : 1;
            var delays = NormalizeDelays(st.Delays, dirs);
            var total = delays.Sum(d => d.Length);
            order[i] = name;
            counts[i] = total;
            map[name] = new StateInfo
            {
                Name = name,
                DirCount = dirs,
                Delays = delays,
                SheetOffset = sheetOffset,
                TotalFrames = total,
            };
            sheetOffset += total;
        }

        int dimX;
        if (folderMode)
        {
            // Per-state sheet: dimX = frames-across in that PNG (handled in Sample for folder).
            dimX = Math.Max(1, atlasW / fw);
        }
        else
        {
            // .rsic atlas: DimX = width / frameW (same as RSIResource.LoadPreTextureRsic).
            dimX = Math.Max(1, atlasW / fw);
        }

        return new Loaded
        {
            SourcePath = source,
            FrameW = fw,
            FrameH = fh,
            AtlasW = atlasW,
            AtlasH = atlasH,
            DimX = dimX,
            States = map,
            FrameCounts = counts,
            StateOrder = order,
        };
    }

    static float[][] NormalizeDelays(List<List<float>>? delays, int dirs)
    {
        var result = new float[dirs][];
        for (var d = 0; d < dirs; d++)
        {
            if (delays is not null && d < delays.Count && delays[d] is { Count: > 0 } list)
                result[d] = list.Select(x => x <= 0 ? 0.1f : x).ToArray();
            else
                result[d] = new[] { 1f };
        }

        return result;
    }

    public static UvRect Sample(
        Loaded atlas,
        string? stateName,
        float rotationRadians,
        double timeSeconds,
        bool folderPerStateSheet = false)
    {
        StateInfo state;
        if (!string.IsNullOrWhiteSpace(stateName) && atlas.States.TryGetValue(stateName, out var hit))
            state = hit;
        else if (TryPreferredState(atlas, out var preferred))
            state = preferred;
        else if (atlas.StateOrder.Length > 0 && atlas.States.TryGetValue(atlas.StateOrder[0], out var first))
            state = first;
        else
            return FullFrame(atlas);

        var dir = DirectionIndex(rotationRadians, state.DirCount);
        var delays = state.Delays[Math.Clamp(dir, 0, state.Delays.Length - 1)];
        var frame = AnimatedFrame(delays, timeSeconds);

        // Frame index within state: all frames of dir0, then dir1, ...
        var indexInState = 0;
        for (var d = 0; d < dir; d++)
            indexInState += state.Delays[d].Length;
        indexInState += frame;

        if (folderPerStateSheet)
        {
            // Single-state PNG layout (directions*frames left-to-right, wrap).
            var dimX = Math.Max(1, atlas.AtlasW / atlas.FrameW);
            var col = indexInState % dimX;
            var row = indexInState / dimX;
            return UvFromCell(atlas, col, row);
        }

        var sheetIndex = state.SheetOffset + indexInState;
        var scol = sheetIndex % atlas.DimX;
        var srow = sheetIndex / atlas.DimX;
        return UvFromCell(atlas, scol, srow);
    }

    static UvRect UvFromCell(Loaded atlas, int col, int row)
    {
        var u0 = (col * atlas.FrameW) / (float)Math.Max(1, atlas.AtlasW);
        var v0 = (row * atlas.FrameH) / (float)Math.Max(1, atlas.AtlasH);
        var u1 = ((col + 1) * atlas.FrameW) / (float)Math.Max(1, atlas.AtlasW);
        var v1 = ((row + 1) * atlas.FrameH) / (float)Math.Max(1, atlas.AtlasH);
        return new UvRect(u0, v0, Math.Min(1f, u1), Math.Min(1f, v1), atlas.FrameW, atlas.FrameH);
    }

    static bool TryPreferredState(Loaded atlas, out StateInfo state)
    {
        foreach (var name in new[] { "full", "icon", "animated", "0", "default", "normal" })
        {
            if (atlas.States.TryGetValue(name, out state!))
                return true;
        }

        state = null!;
        return false;
    }

    static UvRect FullFrame(Loaded atlas) =>
        new(0, 0, Math.Min(1f, atlas.FrameW / (float)Math.Max(1, atlas.AtlasW)),
            Math.Min(1f, atlas.FrameH / (float)Math.Max(1, atlas.AtlasH)),
            atlas.FrameW, atlas.FrameH);

    public static int DirectionIndex(float theta, int dirCount)
    {
        if (dirCount <= 1) return 0;
        // Robust Angle 0 = East (+X). RSI order: S, N, E, W [,SE,SW,NE,NW]
        var twoPi = MathF.PI * 2f;
        var a = theta % twoPi;
        if (a < 0) a += twoPi;

        if (dirCount == 4)
        {
            // sectors centered on E,N,W,S
            var sector = (int)MathF.Floor(((a + MathF.PI / 4f) % twoPi) / (MathF.PI / 2f));
            return sector switch
            {
                0 => 2, // East
                1 => 1, // North
                2 => 3, // West
                _ => 0, // South
            };
        }

        // 8-dir
        var sector8 = (int)MathF.Floor(((a + MathF.PI / 8f) % twoPi) / (MathF.PI / 4f));
        // math: 0=E 1=NE 2=N 3=NW 4=W 5=SW 6=S 7=SE
        // RSI: 0=S 1=N 2=E 3=W 4=SE 5=SW 6=NE 7=NW
        return sector8 switch
        {
            0 => 2, // E
            1 => 6, // NE
            2 => 1, // N
            3 => 7, // NW
            4 => 3, // W
            5 => 5, // SW
            6 => 0, // S
            _ => 4, // SE
        };
    }

    static int AnimatedFrame(float[] delays, double timeSeconds)
    {
        if (delays.Length <= 1) return 0;
        var total = 0.0;
        foreach (var d in delays) total += d;
        if (total <= 0) return 0;
        var t = timeSeconds % total;
        if (t < 0) t += total;
        for (var i = 0; i < delays.Length; i++)
        {
            t -= delays[i];
            if (t < 0) return i;
        }

        return delays.Length - 1;
    }

    static bool TryReadPngSize(Stream stream, out int w, out int h)
    {
        w = h = 0;
        try
        {
            Span<byte> buf = stackalloc byte[24];
            if (stream.Read(buf) < 24) return false;
            if (buf[0] != 0x89 || buf[1] != (byte)'P') return false;
            // IHDR length+type at 8, width/height at 16
            w = (buf[16] << 24) | (buf[17] << 16) | (buf[18] << 8) | buf[19];
            h = (buf[20] << 24) | (buf[21] << 16) | (buf[22] << 8) | buf[23];
            return w > 0 && h > 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { stream.Dispose(); } catch { /* ignore */ }
        }
    }
}
