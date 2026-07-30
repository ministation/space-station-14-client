using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Port.Content;

/// <summary>
/// Tile TypeId → sprite path, matching SS14 <c>EntryPoint.InitTileDefinitions</c>:
/// Space = 0, then all non-abstract <c>type: tile</c> sorted by id Ordinal.
/// Sprites are usually PNGs under Textures/Tiles/*.png (not RSI).
/// </summary>
public sealed class TilePrototypeIndex
{
    static readonly Regex TypeTile = new(
        @"^\s*-\s*type:\s*tile\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    static readonly Regex IdLine = new(
        @"^\s*id:\s*[""']?([A-Za-z0-9_.\-]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex SpriteLine = new(
        @"^\s*sprite:\s*[""']?([^\s#""']+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    static readonly Regex AbstractLine = new(
        @"^\s*abstract:\s*true\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    readonly List<string?> _byTypeId = new() { null }; // 0 = Space / empty
    readonly ConcurrentDictionary<string, ushort> _idToType = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, string?> _spriteByProto = new(StringComparer.OrdinalIgnoreCase);

    public int Count => Math.Max(0, _byTypeId.Count - 1);
    public string? Root { get; private set; }

    public string? TryGetSprite(ushort typeId)
    {
        if (typeId == 0) return null;
        if (typeId >= _byTypeId.Count) return null;
        return _byTypeId[typeId];
    }

    public void Invalidate()
    {
        Root = null;
        _byTypeId.Clear();
        _byTypeId.Add(null);
        _idToType.Clear();
        _spriteByProto.Clear();
    }

    public void EnsureLoaded(string? contentFilesRoot, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(contentFilesRoot) || !Directory.Exists(contentFilesRoot))
            return;
        if (string.Equals(Root, contentFilesRoot, StringComparison.OrdinalIgnoreCase) && Count > 0)
            return;

        Invalidate();
        Root = contentFilesRoot;

        var protoRoot = Path.Combine(contentFilesRoot, "Prototypes");
        if (!Directory.Exists(protoRoot))
        {
            var alt = Path.Combine(contentFilesRoot, "Resources", "Prototypes");
            protoRoot = Directory.Exists(alt) ? alt : protoRoot;
        }

        if (!Directory.Exists(protoRoot))
        {
            log?.Invoke("tiles: prototypes missing");
            return;
        }

        // Collect every concrete tile (with or without sprite) — TypeId slots must match server.
        var found = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(protoRoot, "*.yml", SearchOption.AllDirectories)
                     .Concat(Directory.EnumerateFiles(protoRoot, "*.yaml", SearchOption.AllDirectories)))
        {
            try { ScanFile(file, found); }
            catch { /* skip */ }
        }

        // 0 = Space (no sprite). SS14 always registers Space first.
        _byTypeId[0] = null;
        _idToType["Space"] = 0;
        _spriteByProto["Space"] = null;

        foreach (var id in found.Keys.OrderBy(x => x, StringComparer.Ordinal))
        {
            if (id.Equals("Space", StringComparison.Ordinal))
                continue;
            var sprite = found[id];
            var typeId = (ushort)_byTypeId.Count;
            _byTypeId.Add(sprite);
            _idToType[id] = typeId;
            _spriteByProto[id] = sprite;
        }

        var withSprite = _byTypeId.Count(s => !string.IsNullOrEmpty(s));
        log?.Invoke($"tiles: indexed {Count} typeIds ({withSprite} with sprites) — Ordinal like SS14");
    }

    static void ScanFile(string path, Dictionary<string, string?> found)
    {
        string? currentId = null;
        var inTile = false;
        var isAbstract = false;
        string? sprite = null;

        foreach (var raw in File.ReadLines(path))
        {
            var line = raw;
            if (TypeTile.IsMatch(line))
            {
                Flush(found, ref currentId, ref sprite, ref inTile, ref isAbstract);
                inTile = true;
                currentId = null;
                sprite = null;
                isAbstract = false;
                continue;
            }

            if (!inTile) continue;

            if (line.Length > 0 && line.TrimStart().StartsWith("- type:", StringComparison.OrdinalIgnoreCase)
                && !TypeTile.IsMatch(line))
            {
                Flush(found, ref currentId, ref sprite, ref inTile, ref isAbstract);
                continue;
            }

            if (line.Length > 0 && line[0] != ' ' && line[0] != '\t' && line[0] != '-' && line[0] != '#')
            {
                Flush(found, ref currentId, ref sprite, ref inTile, ref isAbstract);
                continue;
            }

            if (AbstractLine.IsMatch(line))
                isAbstract = true;

            var id = IdLine.Match(line);
            if (id.Success && currentId is null)
                currentId = id.Groups[1].Value;

            var spr = SpriteLine.Match(line);
            if (spr.Success)
            {
                var s = spr.Groups[1].Value.Trim().Trim('"', '\'');
                if (s.Length > 0 && !s.Equals("null", StringComparison.OrdinalIgnoreCase))
                    sprite = NormalizeTileSprite(s);
            }
        }

        Flush(found, ref currentId, ref sprite, ref inTile, ref isAbstract);
    }

    static void Flush(
        Dictionary<string, string?> found,
        ref string? id,
        ref string? sprite,
        ref bool inTile,
        ref bool isAbstract)
    {
        if (inTile && id is not null && !isAbstract)
            found[id] = sprite; // sprite may be null (Space / special)
        id = null;
        sprite = null;
        inTile = false;
        isAbstract = false;
    }

    /// <summary>Keep PNG paths — SS14 floors are Textures/Tiles/*.png, not RSI.</summary>
    static string NormalizeTileSprite(string s)
    {
        s = s.Replace('\\', '/').Trim();
        while (s.StartsWith('/'))
            s = s[1..];
        if (s.StartsWith("Textures/", StringComparison.OrdinalIgnoreCase))
            s = s["Textures/".Length..];
        return s;
    }
}
