using System.Text.Json;
using System.Text.Json.Serialization;

namespace Port.Content;

/// <summary>
/// Minimal RSI folder reader (meta.json + PNG). Spec-compatible with Robust RSI, no Clyde.
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
    /// Resolve an RSI path like <c>Structures/Walls/walls.rsi</c> under a content files root.
    /// </summary>
    public static string? FindRsiDirectory(string contentFilesRoot, string rsiRelative)
    {
        if (string.IsNullOrWhiteSpace(contentFilesRoot) || string.IsNullOrWhiteSpace(rsiRelative))
            return null;

        var rel = rsiRelative.Replace('\\', '/').TrimStart('/');
        if (rel.StartsWith("Textures/", StringComparison.OrdinalIgnoreCase))
            rel = rel["Textures/".Length..];
        if (!rel.EndsWith(".rsi", StringComparison.OrdinalIgnoreCase))
            rel += ".rsi";

        var candidates = new[]
        {
            Path.Combine(contentFilesRoot, "Textures", rel.Replace('/', Path.DirectorySeparatorChar)),
            Path.Combine(contentFilesRoot, rel.Replace('/', Path.DirectorySeparatorChar)),
        };

        foreach (var c in candidates)
        {
            if (Directory.Exists(c) && File.Exists(Path.Combine(c, "meta.json")))
                return c;
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

    /// <summary>
    /// Pick the first state's PNG (state name.png or sheet). Good enough for ghost preview.
    /// </summary>
    public static FrameInfo? TryGetPreviewFrame(string rsiDirectory)
    {
        var doc = TryLoad(rsiDirectory);
        if (doc is null)
            return null;

        var fw = doc.Size?.X > 0 ? doc.Size.X : 32;
        var fh = doc.Size?.Y > 0 ? doc.Size.Y : 32;
        var state = doc.States.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.Name))
                    ?? doc.States.FirstOrDefault();
        if (state is null)
            return null;

        var png = Path.Combine(rsiDirectory, state.Name + ".png");
        if (!File.Exists(png))
        {
            // Some RSIs use a single sheet named after the RSI folder.
            var sheet = Path.Combine(rsiDirectory, Path.GetFileNameWithoutExtension(rsiDirectory.TrimEnd('/', '\\')) + ".png");
            if (File.Exists(sheet))
                png = sheet;
            else
            {
                var any = Directory.GetFiles(rsiDirectory, "*.png").FirstOrDefault();
                if (any is null)
                    return null;
                png = any;
            }
        }

        return new FrameInfo(png, fw, fh, 0);
    }
}
