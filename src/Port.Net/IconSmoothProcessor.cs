using System.Numerics;

namespace Port.Net;

/// <summary>
/// Cardinal IconSmooth for walls/windows — grid-local snap like PC IconSmoothSystem.
/// RSI states are typically the 4-bit neighbor mask (N=1, E=2, S=4, W=8).
/// </summary>
public static class IconSmoothProcessor
{
    const int North = 1;
    const int East = 2;
    const int South = 4;
    const int West = 8;

    public static void Apply(
        List<WorldEntityDraw> draws,
        Func<NetEntity, string?> getPrototype,
        Func<string?, bool> isIconSmoothProto)
    {
        if (draws.Count == 0)
            return;

        var entries = new List<Entry>(draws.Count);
        for (var i = 0; i < draws.Count; i++)
        {
            var d = draws[i];
            if (string.IsNullOrEmpty(d.RsiPath))
                continue;
            var proto = getPrototype(d.Entity);
            if (!isIconSmoothProto(proto) && !IsSmoothPath(d.RsiPath, proto))
                continue;
            entries.Add(new Entry(i, SnapCell(d.X, d.Y), SmoothKey(d.RsiPath, proto)));
        }

        if (entries.Count == 0)
            return;

        var occupancy = new Dictionary<Vector2i, Entry>(entries.Count);
        foreach (var e in entries)
            occupancy[e.Cell] = e;

        foreach (var e in entries)
        {
            var mask = 0;
            if (HasNeighbor(occupancy, e.Cell, e.Key, 0, 1)) mask |= North;
            if (HasNeighbor(occupancy, e.Cell, e.Key, 1, 0)) mask |= East;
            if (HasNeighbor(occupancy, e.Cell, e.Key, 0, -1)) mask |= South;
            if (HasNeighbor(occupancy, e.Cell, e.Key, -1, 0)) mask |= West;

            var state = MapMaskToState(mask, draws[e.DrawIndex].RsiPath);
            var old = draws[e.DrawIndex];
            draws[e.DrawIndex] = old with { StateName = state };
        }
    }

    readonly record struct Entry(int DrawIndex, Vector2i Cell, string Key);

    static Vector2i SnapCell(float x, float y)
    {
        // Entity transforms are tile-centered (0.5 offset).
        return new Vector2i((int)MathF.Floor(x), (int)MathF.Floor(y));
    }

    static string SmoothKey(string? path, string? proto)
    {
        if (!string.IsNullOrEmpty(proto))
            return proto!;
        var p = (path ?? "").Replace('\\', '/');
        var slash = p.LastIndexOf('/');
        return slash >= 0 ? p[..slash] : p;
    }

    static bool IsSmoothPath(string? path, string? proto)
    {
        var p = (path ?? "") + " " + (proto ?? "");
        return p.Contains("Wall", StringComparison.OrdinalIgnoreCase)
               || p.Contains("/Walls/", StringComparison.OrdinalIgnoreCase)
               || p.Contains("Window", StringComparison.OrdinalIgnoreCase)
               || p.Contains("Grille", StringComparison.OrdinalIgnoreCase);
    }

    static bool HasNeighbor(
        Dictionary<Vector2i, Entry> occ,
        Vector2i cell,
        string key,
        int dx,
        int dy)
    {
        var n = new Vector2i(cell.X + dx, cell.Y + dy);
        return occ.TryGetValue(n, out var hit) && string.Equals(hit.Key, key, StringComparison.OrdinalIgnoreCase);
    }

    static string MapMaskToState(int mask, string? rsiPath)
    {
        // Most wall/window RSIs use numeric state ids 0..15; some use "full" for isolated.
        if (mask == 0 && rsiPath is not null
            && (rsiPath.Contains("Wall", StringComparison.OrdinalIgnoreCase)
                || rsiPath.Contains("window", StringComparison.OrdinalIgnoreCase)))
            return "full";
        return mask.ToString();
    }
}
