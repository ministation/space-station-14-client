namespace Port.Content;

/// <summary>
/// PC Content.Shared.DrawDepth-style ordering. Prefer network → YAML → IconSmooth key,
/// then path heuristic only when <see cref="SpriteResolveOptions.AuthoritativeOnly"/> is false.
/// </summary>
public static class DrawDepthResolver
{
    // Relative ints match PrototypeSpriteIndex.TryParseDrawDepth / WorldStateCache.
    public const int BelowFloor = -13;
    public const int FloorTiles = -12;
    public const int DeadMobs = -5;
    public const int Walls = -2;
    public const int WallTops = -1;
    public const int Objects = 0;
    public const int Doors = 1;
    public const int Mobs = 4;
    public const int OverMobs = 5;
    public const int Effects = 6;
    public const int Ghosts = 8;

    public static int Resolve(
        PrototypeSpriteIndex? prototypes,
        string? prototypeId,
        string? rsiPath,
        int networkOrFallbackDepth,
        bool hasAuthoritativeDepth,
        IconSmoothData? iconSmooth = null)
    {
        if (hasAuthoritativeDepth)
            return networkOrFallbackDepth;

        if (!string.IsNullOrWhiteSpace(prototypeId) && prototypes is not null)
        {
            var resolved = prototypes.TryGetResolvedSprite(prototypeId);
            if (resolved?.DrawDepth is { } yamlDepth)
                return yamlDepth;
        }

        // Drawable IconSmooth defaults match PC wall/window stacking.
        if (iconSmooth is { } sm && sm.Mode is not IconSmoothMode.NoSprite)
        {
            if (sm.Key.Equals("windows", StringComparison.OrdinalIgnoreCase)
                || sm.Key.Equals("window", StringComparison.OrdinalIgnoreCase))
                return WallTops;
            return Walls;
        }

        if (SpriteResolveOptions.AuthoritativeOnly)
            return Objects;

        return HeuristicFromPath(rsiPath, prototypeId, networkOrFallbackDepth);
    }

    public static int? TryParseName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        return name.Trim().ToLowerInvariant() switch
        {
            "belowfloor" => BelowFloor,
            "floortiles" or "floor" or "floors" => FloorTiles,
            "deadmobs" => DeadMobs,
            "walls" => Walls,
            "walltops" or "walltop" => WallTops,
            "objects" or "items" or "default" => Objects,
            "doors" or "airlocks" => Doors,
            "mobs" => Mobs,
            "overmobs" => OverMobs,
            "effects" => Effects,
            "ghosts" or "overlays" => Ghosts,
            _ => null,
        };
    }

    public static int HeuristicFromPath(string? path, string? proto, int fallback)
    {
        var p = (path ?? "") + " " + (proto ?? "");
        if (p.Contains("Tiles", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Floor", StringComparison.OrdinalIgnoreCase)
            || p.Contains("plating", StringComparison.OrdinalIgnoreCase))
            return FloorTiles;
        if (p.Contains("/Walls/", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Wall", StringComparison.OrdinalIgnoreCase)
                && !p.Contains("Window", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Grille", StringComparison.OrdinalIgnoreCase))
            return Walls;
        if (p.Contains("Window", StringComparison.OrdinalIgnoreCase)
            || p.Contains("/Windows/", StringComparison.OrdinalIgnoreCase))
            return WallTops;
        if (p.Contains("Airlock", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Door", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Windoor", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Firelock", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Shutter", StringComparison.OrdinalIgnoreCase))
            return Doors;
        if (LooksLikeMob(p))
            return Mobs;
        if (p.Contains("Ghost", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Observer", StringComparison.OrdinalIgnoreCase))
            return Ghosts;
        return fallback != 0 ? fallback : Objects;
    }

    static bool LooksLikeMob(string p) =>
        (p.Contains("Mob", StringComparison.OrdinalIgnoreCase)
         || p.Contains("Human", StringComparison.OrdinalIgnoreCase)
         || p.Contains("Species", StringComparison.OrdinalIgnoreCase)
         || p.Contains("/Mobs/", StringComparison.OrdinalIgnoreCase))
        && !p.Contains("Spawner", StringComparison.OrdinalIgnoreCase)
        && !p.Contains("SpawnPoint", StringComparison.OrdinalIgnoreCase);
}
