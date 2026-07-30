using System.Text;

namespace Port.Content;

/// <summary>
/// Slim extract from ACZ manifest: only Assemblies / Prototypes / Textures*.rsic.
/// Avoids holding multi-million path lists in RAM.
/// </summary>
public sealed class ManifestPlan
{
    public required string Header { get; init; }
    public int TotalEntries { get; init; }
    public long SourceBytes { get; init; }
    public required List<(int Index, string Path)> Assemblies { get; init; }
    public required List<(int Index, string Path)> Prototypes { get; init; }
    public required List<(int Index, string Path)> TexturesRsic { get; init; }
    /// <summary>Floor/wall tile PNGs (SS14 ContentTileDefinition.Sprite).</summary>
    public required List<(int Index, string Path)> TexturesTilePng { get; init; }
    /// <summary>Metadata and state sheets for RSIs explicitly excluded from .rsic packing.</summary>
    public required List<(int Index, string Path)> TexturesRsiFiles { get; init; }

    public Dictionary<string, int> BuildTextureIndex()
    {
        var map = new Dictionary<string, int>(
            TexturesRsic.Count + TexturesTilePng.Count + TexturesRsiFiles.Count,
            StringComparer.OrdinalIgnoreCase);
        foreach (var (index, path) in TexturesRsic)
            map[path.Replace('\\', '/')] = index;
        foreach (var (index, path) in TexturesTilePng)
            map[path.Replace('\\', '/')] = index;
        foreach (var (index, path) in TexturesRsiFiles)
            map[path.Replace('\\', '/')] = index;
        return map;
    }

    [Obsolete("Use BuildTextureIndex; it also contains exploded RSI metadata/state sheets.")]
    public Dictionary<string, int> BuildRsicIndex() => BuildTextureIndex();

    public static ManifestPlan Extract(ReadOnlySpan<byte> utf8Bytes)
    {
        using var ms = new MemoryStream(utf8Bytes.ToArray(), writable: false);
        using var reader = new StreamReader(ms, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024);
        var header = reader.ReadLine() ?? throw new InvalidDataException("empty manifest");
        if (!header.StartsWith("Robust Content Manifest", StringComparison.Ordinal))
            throw new InvalidDataException($"unexpected manifest header: {header}");

        var assemblies = new List<(int, string)>(64);
        var prototypes = new List<(int, string)>(4096);
        var textures = new List<(int, string)>(16384);
        var tilePng = new List<(int, string)>(4096);
        var rsiFiles = new List<(int, string)>(8192);
        string? line;
        var index = 0;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var space = line.IndexOf(' ');
            if (space <= 0)
                throw new InvalidDataException($"bad manifest line {index}");
            var path = line[(space + 1)..].Replace('\\', '/');

            if (path.StartsWith("Assemblies/", StringComparison.OrdinalIgnoreCase)
                && path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                assemblies.Add((index, path));
            }
            else if (path.StartsWith("Prototypes/", StringComparison.OrdinalIgnoreCase)
                     || path.StartsWith("Resources/Prototypes/", StringComparison.OrdinalIgnoreCase))
            {
                prototypes.Add((index, path));
            }
            else if (path.StartsWith("Textures/", StringComparison.OrdinalIgnoreCase)
                     && path.EndsWith(".rsic", StringComparison.OrdinalIgnoreCase))
            {
                textures.Add((index, path));
            }
            else if (path.StartsWith("Textures/", StringComparison.OrdinalIgnoreCase)
                     && path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                     && (path.Contains("/Tiles/", StringComparison.OrdinalIgnoreCase)
                         || path.Contains("/tiles/", StringComparison.OrdinalIgnoreCase)))
            {
                tilePng.Add((index, path));
            }
            else if (path.StartsWith("Textures/", StringComparison.OrdinalIgnoreCase)
                     && path.Contains(".rsi/", StringComparison.OrdinalIgnoreCase)
                     && (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                         || path.EndsWith("/meta.json", StringComparison.OrdinalIgnoreCase)))
            {
                rsiFiles.Add((index, path));
            }

            index++;
        }

        return new ManifestPlan
        {
            Header = header,
            TotalEntries = index,
            SourceBytes = utf8Bytes.Length,
            Assemblies = assemblies,
            Prototypes = prototypes,
            TexturesRsic = textures,
            TexturesTilePng = tilePng,
            TexturesRsiFiles = rsiFiles,
        };
    }
}
