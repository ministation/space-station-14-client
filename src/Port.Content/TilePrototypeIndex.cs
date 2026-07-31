using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Port.Content;

/// <summary>
/// Tile TypeId → sprite path, matching SS14 <c>EntryPoint.InitTileDefinitions</c>:
/// Space = 0, then all non-abstract <c>type: tile</c> sorted by id Ordinal.
/// Sprites are usually PNGs under Textures/Tiles/*.png (not RSI).
/// Child tiles inherit <c>sprite:</c> from <c>parent:</c> (e.g. FloorAsteroidSand).
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
    static readonly Regex ParentLine = new(
        @"^\s*parent:\s*[""']?([A-Za-z0-9_.\-]+)",
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

    public string? TryGetSpriteById(string? prototypeId)
    {
        if (string.IsNullOrWhiteSpace(prototypeId)) return null;
        return _spriteByProto.TryGetValue(prototypeId, out var s) ? s : null;
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

        // Scan BOTH trees — ACZ may land under Prototypes/ and Resources/Prototypes/.
        var roots = new List<string>();
        var primary = Path.Combine(contentFilesRoot, "Prototypes");
        var alt = Path.Combine(contentFilesRoot, "Resources", "Prototypes");
        if (Directory.Exists(primary)) roots.Add(primary);
        if (Directory.Exists(alt)) roots.Add(alt);

        if (roots.Count == 0)
        {
            log?.Invoke("tiles: prototypes missing");
            return;
        }

        var found = new Dictionary<string, TileRaw>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var protoRoot in roots)
        foreach (var file in Directory.EnumerateFiles(protoRoot, "*.yml", SearchOption.AllDirectories)
                     .Concat(Directory.EnumerateFiles(protoRoot, "*.yaml", SearchOption.AllDirectories)))
        {
            var rel = Path.GetRelativePath(protoRoot, file);
            if (!seen.Add(protoRoot + "|" + rel))
                continue;
            try { ScanFile(file, found); }
            catch { /* skip */ }
        }

        // Resolve sprite inheritance (FloorAsteroidSand → parent Borderless PNG).
        foreach (var id in found.Keys.ToList())
            ResolveSprite(id, found, new HashSet<string>(StringComparer.Ordinal));

        // 0 = Space (no sprite). SS14 always registers Space first.
        _byTypeId[0] = null;
        _idToType["Space"] = 0;
        _spriteByProto["Space"] = null;

        foreach (var id in found.Keys.OrderBy(x => x, StringComparer.Ordinal))
        {
            if (id.Equals("Space", StringComparison.Ordinal))
                continue;
            var raw = found[id];
            if (raw.Abstract)
                continue;
            var sprite = raw.Sprite;
            var typeId = (ushort)_byTypeId.Count;
            _byTypeId.Add(sprite);
            _idToType[id] = typeId;
            _spriteByProto[id] = sprite;
        }

        var withSprite = _byTypeId.Count(s => !string.IsNullOrEmpty(s));
        log?.Invoke($"tiles: indexed {Count} typeIds ({withSprite} with sprites) — Ordinal+parent like SS14");
    }

    static string? ResolveSprite(string id, Dictionary<string, TileRaw> found, HashSet<string> visiting)
    {
        if (!found.TryGetValue(id, out var raw))
            return null;
        if (!string.IsNullOrEmpty(raw.Sprite))
            return raw.Sprite;
        if (string.IsNullOrEmpty(raw.Parent))
            return null;
        if (!visiting.Add(id))
            return null;
        var inherited = ResolveSprite(raw.Parent, found, visiting);
        if (!string.IsNullOrEmpty(inherited))
            found[id] = raw with { Sprite = inherited };
        return inherited;
    }

    static void ScanFile(string path, Dictionary<string, TileRaw> found)
    {
        string? currentId = null;
        var inTile = false;
        var isAbstract = false;
        string? sprite = null;
        string? parent = null;

        foreach (var raw in File.ReadLines(path))
        {
            var line = raw;
            if (TypeTile.IsMatch(line))
            {
                Flush(found, ref currentId, ref sprite, ref parent, ref inTile, ref isAbstract);
                inTile = true;
                currentId = null;
                sprite = null;
                parent = null;
                isAbstract = false;
                continue;
            }

            if (!inTile) continue;

            if (line.Length > 0 && line.TrimStart().StartsWith("- type:", StringComparison.OrdinalIgnoreCase)
                && !TypeTile.IsMatch(line))
            {
                Flush(found, ref currentId, ref sprite, ref parent, ref inTile, ref isAbstract);
                continue;
            }

            if (line.Length > 0 && line[0] != ' ' && line[0] != '\t' && line[0] != '-' && line[0] != '#')
            {
                Flush(found, ref currentId, ref sprite, ref parent, ref inTile, ref isAbstract);
                continue;
            }

            if (AbstractLine.IsMatch(line))
                isAbstract = true;

            var id = IdLine.Match(line);
            if (id.Success && currentId is null)
                currentId = id.Groups[1].Value;

            var par = ParentLine.Match(line);
            if (par.Success && parent is null)
                parent = par.Groups[1].Value;

            var spr = SpriteLine.Match(line);
            if (spr.Success)
            {
                var s = spr.Groups[1].Value.Trim().Trim('"', '\'');
                if (s.Length > 0 && !s.Equals("null", StringComparison.OrdinalIgnoreCase))
                    sprite = NormalizeTileSprite(s);
            }
        }

        Flush(found, ref currentId, ref sprite, ref parent, ref inTile, ref isAbstract);
    }

    static void Flush(
        Dictionary<string, TileRaw> found,
        ref string? id,
        ref string? sprite,
        ref string? parent,
        ref bool inTile,
        ref bool isAbstract)
    {
        if (inTile && id is not null)
            found[id] = new TileRaw(id, parent, sprite, isAbstract);
        id = null;
        sprite = null;
        parent = null;
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

    readonly record struct TileRaw(string Id, string? Parent, string? Sprite, bool Abstract);
}
