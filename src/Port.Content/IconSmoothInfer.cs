using System.Collections.Concurrent;

namespace Port.Content;

/// <summary>
/// Infer IconSmooth base/mode from RSI <c>meta.json</c> / .rsic when YAML is missing
/// or incomplete — matches PC connection states like solid0..7 / riveted0..7.
/// </summary>
public static class IconSmoothInfer
{
    static readonly ConcurrentDictionary<string, IconSmoothData?> Cache =
        new(StringComparer.OrdinalIgnoreCase);
    static readonly ConcurrentDictionary<string, List<(string Name, int Dirs)>> StateNameCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Read meta states and pick the best numbered base (e.g. solid0..7 → base "solid", Corners).
    /// </summary>
    public static IconSmoothData? FromRsi(string? contentRoot, string? rsiPath, string? protoId = null)
    {
        if (string.IsNullOrWhiteSpace(rsiPath))
            return null;

        var cacheKey = (contentRoot ?? "") + "|" + rsiPath;
        if (Cache.TryGetValue(cacheKey, out var hit))
            return hit;

        IconSmoothData? result = null;
        try
        {
            result = InferCore(contentRoot, rsiPath, protoId);
        }
        catch
        {
            result = null;
        }

        // Never cache misses — RSI/meta often arrives later via ACZ; a sticky null
        // permanently kills wall IconSmooth for that path.
        if (result is not null)
            Cache[cacheKey] = result;
        return result;
    }

    public static void ClearCache()
    {
        Cache.Clear();
        StateNameCache.Clear();
    }

    /// <summary>Drop one RSI entry after ACZ writes that path — never wipe the whole cache.</summary>
    public static void Invalidate(string? contentRoot, string? rsiPath)
    {
        if (string.IsNullOrWhiteSpace(rsiPath))
            return;
        var key = (contentRoot ?? "") + "|" + rsiPath;
        Cache.TryRemove(key, out _);
        StateNameCache.TryRemove(key, out _);
        // Also drop path-only keys used by callers that omit contentRoot.
        Cache.TryRemove("|" + rsiPath, out _);
        Cache.TryRemove(rsiPath, out _);
        StateNameCache.TryRemove("|" + rsiPath, out _);
        StateNameCache.TryRemove(rsiPath, out _);
    }

    static IconSmoothData? InferCore(string? contentRoot, string rsiPath, string? protoId)
    {
        var names = LoadStateNames(contentRoot, rsiPath);
        if (names.Count == 0)
            return HeuristicFromPath(rsiPath, protoId);

        // Collect bases that have contiguous numbered states (base0, base1, ...).
        var bases = new Dictionary<string, (int Max, int Count, int DirHint)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, dirs) in names)
        {
            if (!TrySplitNumbered(name, out var bas, out var n))
                continue;
            if (!bases.TryGetValue(bas, out var cur))
                cur = (n, 0, dirs);
            cur.Max = Math.Max(cur.Max, n);
            cur.Count++;
            cur.DirHint = Math.Max(cur.DirHint, dirs);
            bases[bas] = cur;
        }

        // Prefer bases with 0..7 (Corners) or 0..15 (CardinalFlags); skip "full"/"icon".
        string? bestBase = null;
        var bestScore = -1;
        IconSmoothMode bestMode = IconSmoothMode.Corners;
        foreach (var (bas, info) in bases)
        {
            if (bas.Equals("full", StringComparison.OrdinalIgnoreCase)
                || bas.Equals("icon", StringComparison.OrdinalIgnoreCase)
                || bas.Equals("state", StringComparison.OrdinalIgnoreCase) && info.Max <= 1)
                continue;

            var score = info.Count * 10 + info.Max;
            // solid0..7 with 4 dirs is the classic wall sheet.
            if (info.Max >= 7 && info.Count >= 8)
                score += 100;
            if (info.Max >= 15 && info.Count >= 16)
                score += 80;
            if (info.DirHint >= 4)
                score += 20;

            if (score <= bestScore)
                continue;
            bestScore = score;
            bestBase = bas;
            bestMode = info.Max >= 15 && info.Count >= 12
                ? IconSmoothMode.CardinalFlags
                : info.Max <= 1
                    ? IconSmoothMode.Diagonal
                    : IconSmoothMode.Corners;
        }

        // Meta present but no numbered sheet (railing side/corner) → not IconSmooth.
        // Heuristic only when meta/atlas is empty (ACZ not arrived yet).
        if (bestBase is null)
            return names.Count > 0 ? null : HeuristicFromPath(rsiPath, protoId);

        var key = InferSmoothKey(rsiPath, protoId);
        return new IconSmoothData(key, bestBase, bestMode);
    }

    static IconSmoothData? HeuristicFromPath(string rsiPath, string? protoId)
    {
        var p = rsiPath.Replace('\\', '/');
        if (p.Contains("Railing", StringComparison.OrdinalIgnoreCase)
            || (protoId?.Contains("Railing", StringComparison.OrdinalIgnoreCase) ?? false))
            return null;
        if (!p.Contains("/Walls/", StringComparison.OrdinalIgnoreCase)
            && !p.Contains("/Windows/", StringComparison.OrdinalIgnoreCase)
            && !p.Contains("Grille", StringComparison.OrdinalIgnoreCase)
            && !(protoId?.Contains("Wall", StringComparison.OrdinalIgnoreCase) ?? false)
            && !(protoId?.Contains("Window", StringComparison.OrdinalIgnoreCase) ?? false)
            && !(protoId?.Contains("Grille", StringComparison.OrdinalIgnoreCase) ?? false))
            return null;

        var file = Path.GetFileNameWithoutExtension(p);
        if (file.EndsWith(".rsi", StringComparison.OrdinalIgnoreCase))
            file = file[..^4];
        if (string.IsNullOrWhiteSpace(file))
            return null;

        // solid.rsi → solid, solid_rust.rsi → solid, reinforced_diagonal → often "state"
        var bas = file;
        if (bas.EndsWith("_rust", StringComparison.OrdinalIgnoreCase))
            bas = bas[..^5];
        if (bas.EndsWith("_diagonal", StringComparison.OrdinalIgnoreCase))
            return new IconSmoothData(InferSmoothKey(rsiPath, protoId), "state", IconSmoothMode.Diagonal);

        return new IconSmoothData(InferSmoothKey(rsiPath, protoId), bas, IconSmoothMode.Corners);
    }

    static string InferSmoothKey(string rsiPath, string? protoId)
    {
        var p = (rsiPath + " " + (protoId ?? "")).Replace('\\', '/');
        if (p.Contains("Window", StringComparison.OrdinalIgnoreCase))
            return "windows";
        if (p.Contains("Grille", StringComparison.OrdinalIgnoreCase))
            return "grilles";
        return "walls";
    }

    static List<(string Name, int Dirs)> LoadStateNames(string? contentRoot, string rsiPath)
    {
        var cacheKey = (contentRoot ?? "") + "|" + rsiPath;
        if (StateNameCache.TryGetValue(cacheKey, out var hit))
            return hit;

        var list = new List<(string, int)>();
        if (string.IsNullOrWhiteSpace(contentRoot))
            return list;

        var src = RsiMeta.FindRsiSource(contentRoot, rsiPath);
        if (src is null)
            return list;

        if (src.Value.IsRsic)
        {
            var atlas = RsiAtlas.TryLoad(src.Value.Path);
            if (atlas is null)
                return list;
            foreach (var (name, info) in atlas.States)
                list.Add((name, info.DirCount));
            if (list.Count > 0)
                StateNameCache[cacheKey] = list;
            return list;
        }

        var doc = RsiMeta.TryLoad(src.Value.Path);
        if (doc?.States is null)
            return list;
        foreach (var s in doc.States)
        {
            if (string.IsNullOrWhiteSpace(s.Name))
                continue;
            list.Add((s.Name, s.Directions > 0 ? s.Directions : 1));
        }
        if (list.Count > 0)
            StateNameCache[cacheKey] = list;

        return list;
    }

    public static bool TrySplitNumbered(string stateName, out string stateBase, out int number)
    {
        stateBase = "";
        number = 0;
        if (string.IsNullOrWhiteSpace(stateName))
            return false;
        var i = stateName.Length - 1;
        while (i >= 0 && char.IsDigit(stateName[i]))
            i--;
        if (i < 0 || i >= stateName.Length - 1)
            return false;
        if (!int.TryParse(stateName[(i + 1)..], out number))
            return false;
        stateBase = stateName[..(i + 1)];
        return stateBase.Length > 0;
    }

    /// <summary>
    /// True when RSI meta/folder exposes at least one <c>{stateBase}N</c> state (e.g. rwindow3).
    /// </summary>
    public static bool RsiHasNumberedBase(string? contentRoot, string? rsiPath, string? stateBase)
    {
        if (string.IsNullOrWhiteSpace(stateBase) || string.IsNullOrWhiteSpace(rsiPath))
            return false;
        foreach (var (name, _) in LoadStateNames(contentRoot, rsiPath))
        {
            if (!TrySplitNumbered(name, out var bas, out _))
                continue;
            if (string.Equals(bas, stateBase, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
