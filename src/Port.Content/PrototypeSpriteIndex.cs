using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Port.Content;

/// <summary>
/// Entity prototype → RSI path index from content Prototypes/*.yml.
/// Resolves parent chains (including YAML list parents) so walls/floors/mobs get sprites.
/// </summary>
public sealed class PrototypeSpriteIndex
{
    static readonly Regex IdLine = new(
        @"^\s*id:\s*[""']?([A-Za-z0-9_.\-]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    static readonly Regex SpritePath = new(
        @"^\s*sprite:\s*[""']?([^\s#""']+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    static readonly Regex StateName = new(
        @"^\s*state:\s*[""']?([A-Za-z0-9_.\-]+)[""']?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    static readonly Regex TypeEntity = new(
        @"^\s*-\s*type:\s*entity\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    static readonly Regex ParentScalar = new(
        @"^\s*parent:\s*[""']?([A-Za-z0-9_.\-]+)[""']?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    static readonly Regex ParentListItem = new(
        @"^\s*-\s*[""']?([A-Za-z0-9_.\-]+)[""']?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    static readonly Regex ParentInlineList = new(
        @"^\s*parent:\s*\[([^\]]+)\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    static readonly Regex SmoothKey = new(
        @"^\s*key:\s*[""']?([A-Za-z0-9_.\-]+)[""']?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    static readonly Regex SmoothBase = new(
        @"^\s*base:\s*[""']?([A-Za-z0-9_.\-]+)[""']?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    static readonly Regex SmoothMode = new(
        @"^\s*mode:\s*[""']?([A-Za-z0-9_.\-]+)[""']?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    readonly ConcurrentDictionary<string, string> _spriteByProto =
        new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, string> _stateByProto =
        new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, IconSmoothData> _smoothByProto =
        new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, List<string>> _parentsByProto =
        new(StringComparer.OrdinalIgnoreCase);

    public int Count => _spriteByProto.Count;
    public string? Root { get; private set; }

    public string? TryGetSprite(string? prototypeId)
    {
        if (string.IsNullOrWhiteSpace(prototypeId))
            return null;

        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return Resolve(prototypeId!, visiting, 0);
    }

    public string? TryGetState(string? prototypeId)
    {
        if (string.IsNullOrWhiteSpace(prototypeId))
            return null;

        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return ResolveState(prototypeId!, visiting, 0);
    }

    public IconSmoothData? TryGetIconSmooth(string? prototypeId)
    {
        if (string.IsNullOrWhiteSpace(prototypeId))
            return null;

        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return ResolveSmooth(prototypeId!, visiting, 0);
    }

    string? Resolve(string id, HashSet<string> visiting, int depth)
    {
        if (depth > 24 || !visiting.Add(id))
            return null;
        if (_spriteByProto.TryGetValue(id, out var path))
            return path;
        if (!_parentsByProto.TryGetValue(id, out var parents))
            return null;
        foreach (var parent in parents)
        {
            var got = Resolve(parent, visiting, depth + 1);
            if (got is not null)
                return got;
        }

        return null;
    }

    string? ResolveState(string id, HashSet<string> visiting, int depth)
    {
        if (depth > 24 || !visiting.Add(id))
            return null;
        if (_stateByProto.TryGetValue(id, out var st))
            return st;
        if (!_parentsByProto.TryGetValue(id, out var parents))
            return null;
        foreach (var parent in parents)
        {
            var got = ResolveState(parent, visiting, depth + 1);
            if (got is not null)
                return got;
        }

        return null;
    }

    IconSmoothData? ResolveSmooth(string id, HashSet<string> visiting, int depth)
    {
        if (depth > 24 || !visiting.Add(id))
            return null;
        if (_smoothByProto.TryGetValue(id, out var sm))
            return sm;
        if (!_parentsByProto.TryGetValue(id, out var parents))
            return null;
        foreach (var parent in parents)
        {
            var got = ResolveSmooth(parent, visiting, depth + 1);
            if (got is not null)
                return got;
        }

        return null;
    }

    public void Invalidate()
    {
        Root = null;
        _spriteByProto.Clear();
        _stateByProto.Clear();
        _smoothByProto.Clear();
        _parentsByProto.Clear();
    }

    public void EnsureLoaded(string? contentFilesRoot, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(contentFilesRoot) || !Directory.Exists(contentFilesRoot))
            return;
        if (string.Equals(Root, contentFilesRoot, StringComparison.OrdinalIgnoreCase) && _spriteByProto.Count > 0)
            return;

        Root = contentFilesRoot;
        _spriteByProto.Clear();
        _stateByProto.Clear();
        _smoothByProto.Clear();
        _parentsByProto.Clear();

        var protoRoot = Path.Combine(contentFilesRoot, "Prototypes");
        if (!Directory.Exists(protoRoot))
        {
            var alt = Path.Combine(contentFilesRoot, "Resources", "Prototypes");
            protoRoot = Directory.Exists(alt) ? alt : protoRoot;
        }

        if (!Directory.Exists(protoRoot))
        {
            log?.Invoke("prototypes: directory missing");
            return;
        }

        var files = Directory.EnumerateFiles(protoRoot, "*.yml", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(protoRoot, "*.yaml", SearchOption.AllDirectories))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
        var scanned = 0;
        foreach (var file in files)
        {
            scanned++;
            try
            {
                ScanFile(file);
            }
            catch
            {
                /* skip bad yaml */
            }
        }

        // Inherit sprite from parents when child has no explicit sprite (multi-pass BFS).
        var resolved = 0;
        for (var pass = 0; pass < 12; pass++)
        {
            var added = 0;
            foreach (var (id, parents) in _parentsByProto)
            {
                if (_spriteByProto.ContainsKey(id))
                    continue;
                foreach (var parent in parents)
                {
                    if (!_spriteByProto.TryGetValue(parent, out var path))
                        continue;
                    if (_spriteByProto.TryAdd(id, path))
                        added++;
                    break;
                }
            }

            resolved += added;
            if (added == 0)
                break;
        }

        log?.Invoke($"prototypes: indexed {_spriteByProto.Count} sprites ({resolved} via parent) from {scanned} files");
    }

    void ScanFile(string path)
    {
        string? currentId = null;
        var inEntity = false;
        var sawSpriteComponent = false;
        var inIconSmooth = false;
        string? smoothKey = null;
        string? smoothBase = null;
        var smoothMode = IconSmoothMode.Corners;
        var inParentList = false;
        var entityIndent = -1;
        var spriteComponentIndent = -1;

        void FlushSmooth()
        {
            if (currentId is null || !inIconSmooth)
                return;
            if (string.IsNullOrWhiteSpace(smoothKey) || string.IsNullOrWhiteSpace(smoothBase))
                return;
            _smoothByProto.TryAdd(currentId, new IconSmoothData(smoothKey!, smoothBase!, smoothMode));
        }

        foreach (var raw in File.ReadLines(path))
        {
            var line = raw;
            if (TypeEntity.IsMatch(line))
            {
                FlushSmooth();
                inEntity = true;
                currentId = null;
                sawSpriteComponent = false;
                inIconSmooth = false;
                smoothKey = null;
                smoothBase = null;
                smoothMode = IconSmoothMode.Corners;
                inParentList = false;
                entityIndent = line.TakeWhile(c => c == ' ' || c == '\t').Count();
                spriteComponentIndent = -1;
                continue;
            }

            if (!inEntity)
                continue;

            var indent = line.TakeWhile(c => c == ' ' || c == '\t').Count();
            if (line.Length > 0
                && line.TrimStart().StartsWith("- type:", StringComparison.OrdinalIgnoreCase)
                && indent <= entityIndent)
            {
                FlushSmooth();
                if (!TypeEntity.IsMatch(line))
                {
                    inEntity = false;
                    inParentList = false;
                    inIconSmooth = false;
                    continue;
                }

                currentId = null;
                sawSpriteComponent = false;
                inIconSmooth = false;
                smoothKey = null;
                smoothBase = null;
                smoothMode = IconSmoothMode.Corners;
                inParentList = false;
                entityIndent = indent;
                spriteComponentIndent = -1;
                continue;
            }

            if (line.Length > 0 && line[0] != ' ' && line[0] != '\t' && line[0] != '-' && line[0] != '#')
            {
                FlushSmooth();
                inEntity = false;
                inParentList = false;
                inIconSmooth = false;
                sawSpriteComponent = false;
                spriteComponentIndent = -1;
                continue;
            }

            // Component boundary inside one entity: keep Sprite/Icon and IconSmooth scopes strict.
            if (line.TrimStart().StartsWith("- type:", StringComparison.OrdinalIgnoreCase)
                && indent > entityIndent)
            {
                if (inIconSmooth)
                    FlushSmooth();
                inIconSmooth = false;
                sawSpriteComponent = false;
                spriteComponentIndent = indent;
                inParentList = false;

                if (line.Contains("type: IconSmooth", StringComparison.OrdinalIgnoreCase))
                {
                    inIconSmooth = true;
                    smoothKey = null;
                    smoothBase = null;
                    smoothMode = IconSmoothMode.Corners;
                }
                else if (line.Contains("type: Sprite", StringComparison.OrdinalIgnoreCase)
                         || line.Contains("type: sprite", StringComparison.OrdinalIgnoreCase)
                         || line.Contains("type: Icon", StringComparison.OrdinalIgnoreCase))
                {
                    sawSpriteComponent = true;
                }

                continue;
            }

            var idMatch = IdLine.Match(line);
            if (idMatch.Success && currentId is null)
            {
                currentId = idMatch.Groups[1].Value;
                inParentList = false;
                continue;
            }

            if (currentId is not null)
            {
                var inline = ParentInlineList.Match(line);
                if (inline.Success)
                {
                    AddParents(currentId, SplitYamlList(inline.Groups[1].Value));
                    inParentList = false;
                    continue;
                }

                var scalar = ParentScalar.Match(line);
                if (scalar.Success)
                {
                    AddParents(currentId, new[] { scalar.Groups[1].Value });
                    inParentList = false;
                    continue;
                }

                if (Regex.IsMatch(line, @"^\s*parent:\s*$", RegexOptions.IgnoreCase))
                {
                    inParentList = true;
                    continue;
                }

                if (inParentList)
                {
                    var item = ParentListItem.Match(line);
                    if (item.Success)
                    {
                        AddParents(currentId, new[] { item.Groups[1].Value });
                        continue;
                    }

                    // left the parent list
                    if (!string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith('#'))
                        inParentList = false;
                }
            }

            if (inIconSmooth && currentId is not null)
            {
                var sk = SmoothKey.Match(line);
                if (sk.Success)
                    smoothKey = sk.Groups[1].Value;
                var sb = SmoothBase.Match(line);
                if (sb.Success)
                    smoothBase = sb.Groups[1].Value;
                var sm = SmoothMode.Match(line);
                if (sm.Success)
                {
                    smoothMode = sm.Groups[1].Value.ToLowerInvariant() switch
                    {
                        "cardinalflags" => IconSmoothMode.CardinalFlags,
                        "diagonal" => IconSmoothMode.Diagonal,
                        "nosprite" => IconSmoothMode.NoSprite,
                        _ => IconSmoothMode.Corners,
                    };
                }
            }

            if (sawSpriteComponent && !inIconSmooth && currentId is not null
                && (spriteComponentIndent < 0 || indent > spriteComponentIndent))
            {
                var st = StateName.Match(line);
                if (st.Success)
                    _stateByProto.TryAdd(currentId, st.Groups[1].Value);
            }

            var spr = SpritePath.Match(line);
            if (!spr.Success || currentId is null)
                continue;

            var rsi = spr.Groups[1].Value.Trim().Trim('"', '\'');
            if (rsi.Length == 0 || rsi.Equals("null", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!rsi.Contains('/') && !rsi.EndsWith(".rsi", StringComparison.OrdinalIgnoreCase)
                && !rsi.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                continue;
            // Keep .png as-is (tile/entity sheet). Only append .rsi when extension missing.
            if (!rsi.EndsWith(".rsi", StringComparison.OrdinalIgnoreCase)
                && !rsi.EndsWith(".rsic", StringComparison.OrdinalIgnoreCase)
                && !rsi.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                rsi += ".rsi";

            // First sprite under Sprite/Icon only — each entity keeps its own YAML sprite.
            if (!sawSpriteComponent)
                continue;
            if (_spriteByProto.ContainsKey(currentId))
            {
                sawSpriteComponent = false;
                continue;
            }

            if (_spriteByProto.TryAdd(currentId, rsi.Replace('\\', '/')))
                sawSpriteComponent = false;
        }

        FlushSmooth();
    }

    void AddParents(string id, IEnumerable<string> parents)
    {
        var list = _parentsByProto.GetOrAdd(id, _ => new List<string>());
        foreach (var p in parents)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            if (!list.Contains(p, StringComparer.OrdinalIgnoreCase))
                list.Add(p);
        }
    }

    static IEnumerable<string> SplitYamlList(string inner)
    {
        foreach (var part in inner.Split(','))
        {
            var s = part.Trim().Trim('"', '\'');
            if (s.Length > 0)
                yield return s;
        }
    }
}
