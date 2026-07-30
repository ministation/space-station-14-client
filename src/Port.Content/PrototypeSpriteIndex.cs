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

    readonly ConcurrentDictionary<string, string> _spriteByProto =
        new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, string> _stateByProto =
        new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, List<string>> _parentsByProto =
        new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, byte> _iconSmoothProtos =
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

    public bool IsIconSmooth(string? prototypeId)
    {
        if (string.IsNullOrWhiteSpace(prototypeId))
            return false;
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return ResolveIconSmooth(prototypeId!, visiting, 0);
    }

    bool ResolveIconSmooth(string id, HashSet<string> visiting, int depth)
    {
        if (depth > 24 || !visiting.Add(id))
            return false;
        if (_iconSmoothProtos.ContainsKey(id))
            return true;
        if (!_parentsByProto.TryGetValue(id, out var parents))
            return false;
        foreach (var parent in parents)
        {
            if (ResolveIconSmooth(parent, visiting, depth + 1))
                return true;
        }

        return false;
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

    public void Invalidate()
    {
        Root = null;
        _spriteByProto.Clear();
        _stateByProto.Clear();
        _parentsByProto.Clear();
        _iconSmoothProtos.Clear();
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
        _parentsByProto.Clear();
        _iconSmoothProtos.Clear();

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
        var inParentList = false;
        var entityIndent = -1;

        foreach (var raw in File.ReadLines(path))
        {
            var line = raw;
            if (TypeEntity.IsMatch(line))
            {
                inEntity = true;
                currentId = null;
                sawSpriteComponent = false;
                inParentList = false;
                entityIndent = line.TakeWhile(c => c == ' ' || c == '\t').Count();
                continue;
            }

            if (!inEntity)
                continue;

            var indent = line.TakeWhile(c => c == ' ' || c == '\t').Count();
            if (line.Length > 0
                && line.TrimStart().StartsWith("- type:", StringComparison.OrdinalIgnoreCase)
                && indent <= entityIndent)
            {
                if (!TypeEntity.IsMatch(line))
                {
                    inEntity = false;
                    inParentList = false;
                    continue;
                }

                currentId = null;
                sawSpriteComponent = false;
                inParentList = false;
                entityIndent = indent;
                continue;
            }

            if (line.Length > 0 && line[0] != ' ' && line[0] != '\t' && line[0] != '-' && line[0] != '#')
            {
                inEntity = false;
                inParentList = false;
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

            if (line.Contains("type: IconSmooth", StringComparison.OrdinalIgnoreCase)
                && currentId is not null)
            {
                _iconSmoothProtos.TryAdd(currentId, 1);
                sawSpriteComponent = true;
                inParentList = false;
            }

            if (line.Contains("type: Sprite", StringComparison.OrdinalIgnoreCase)
                || line.Contains("type: sprite", StringComparison.OrdinalIgnoreCase)
                || line.Contains("type: Icon", StringComparison.OrdinalIgnoreCase))
            {
                sawSpriteComponent = true;
                inParentList = false;
            }

            if (sawSpriteComponent && currentId is not null)
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
            if (!rsi.Contains('/') && !rsi.EndsWith(".rsi", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!rsi.EndsWith(".rsi", StringComparison.OrdinalIgnoreCase)
                && !rsi.EndsWith(".rsic", StringComparison.OrdinalIgnoreCase)
                && !rsi.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                rsi += ".rsi";
            if (rsi.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                rsi = rsi[..^4] + ".rsi";

            // Prefer first sprite under Sprite/Icon; also accept layer sprite: paths.
            if (!sawSpriteComponent && !line.Contains("sprite:", StringComparison.OrdinalIgnoreCase))
                continue;

            if (_spriteByProto.TryAdd(currentId, rsi.Replace('\\', '/')))
                sawSpriteComponent = false;
        }
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
