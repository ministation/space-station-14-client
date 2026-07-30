using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using YamlDotNet.RepresentationModel;

namespace Port.Content;

/// <summary>
/// Authoritative entity prototype sprite index. Parses YAML structurally, resolves
/// parent inheritance, and preserves the complete ordered Sprite layer definition.
/// </summary>
public sealed class PrototypeSpriteIndex
{
    public readonly record struct SpriteLayer(
        string? Path,
        string? State,
        bool Visible,
        byte R,
        byte G,
        byte B,
        float OffsetX,
        float OffsetY,
        float ScaleX,
        float ScaleY,
        float Rotation,
        string? Shader,
        string? MapKey);

    public sealed record ResolvedSprite(
        string? Path,
        string? State,
        bool NoRotation,
        int? DrawDepth,
        IReadOnlyList<SpriteLayer> Layers);

    /// <summary>PC EntityStorageVisuals — locker/crate door art from YAML, not network.</summary>
    public readonly record struct StorageVisuals(
        string? StateBaseClosed,
        string? StateBaseOpen,
        string? StateDoorClosed,
        string? StateDoorOpen);

    sealed class RawPrototype
    {
        public required string Id;
        public readonly List<string> Parents = new();
        public SpritePatch? Sprite;
        public IconSmoothData? Smooth;
        public StorageVisuals? Storage;
    }

    sealed class SpritePatch
    {
        public string? Path;
        public bool HasPath;
        public string? State;
        public bool HasState;
        public bool? NoRotation;
        public int? DrawDepth;
        public bool HasLayers;
        public readonly List<SpriteLayer> Layers = new();
    }

    readonly ConcurrentDictionary<string, RawPrototype> _raw =
        new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, ResolvedSprite?> _resolved =
        new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, IconSmoothData?> _smooth =
        new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, StorageVisuals?> _storage =
        new(StringComparer.OrdinalIgnoreCase);

    public int Count => _resolved.Count(kv => kv.Value is not null);
    public string? Root { get; private set; }

    public ResolvedSprite? TryGetResolvedSprite(string? prototypeId)
    {
        if (string.IsNullOrWhiteSpace(prototypeId))
            return null;
        return _resolved.TryGetValue(prototypeId, out var sprite) ? sprite : null;
    }

    public string? TryGetSprite(string? prototypeId) =>
        TryGetResolvedSprite(prototypeId)?.Path
        ?? TryGetResolvedSprite(prototypeId)?.Layers.FirstOrDefault(l => l.Path is not null).Path;

    public string? TryGetState(string? prototypeId) =>
        TryGetResolvedSprite(prototypeId)?.State
        ?? TryGetResolvedSprite(prototypeId)?.Layers.FirstOrDefault(l => l.State is not null).State;

    public IconSmoothData? TryGetIconSmooth(string? prototypeId)
    {
        if (string.IsNullOrWhiteSpace(prototypeId))
            return null;
        return _smooth.TryGetValue(prototypeId, out var value) ? value : null;
    }

    public StorageVisuals? TryGetStorageVisuals(string? prototypeId)
    {
        if (string.IsNullOrWhiteSpace(prototypeId))
            return null;
        return _storage.TryGetValue(prototypeId, out var value) ? value : null;
    }

    public void Invalidate()
    {
        Root = null;
        _raw.Clear();
        _resolved.Clear();
        _smooth.Clear();
        _storage.Clear();
    }

    public void EnsureLoaded(string? contentFilesRoot, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(contentFilesRoot) || !Directory.Exists(contentFilesRoot))
            return;
        if (string.Equals(Root, contentFilesRoot, StringComparison.OrdinalIgnoreCase)
            && _resolved.Count > 0)
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
            log?.Invoke("prototypes: directory missing");
            return;
        }

        var scanned = 0;
        var failed = 0;
        foreach (var file in Directory.EnumerateFiles(protoRoot, "*.yml", SearchOption.AllDirectories)
                     .Concat(Directory.EnumerateFiles(protoRoot, "*.yaml", SearchOption.AllDirectories))
                     .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            scanned++;
            try
            {
                ParseFile(file);
            }
            catch (Exception ex)
            {
                failed++;
                if (failed <= 8)
                    log?.Invoke($"prototype YAML FAIL {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        foreach (var id in _raw.Keys)
        {
            ResolveSprite(id, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            ResolveSmooth(id, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            ResolveStorage(id, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        log?.Invoke(
            $"prototypes: YAML parsed {_raw.Count} entities, {_resolved.Count(kv => kv.Value is not null)} sprites " +
            $"from {scanned} files (failed={failed})");
    }

    void ParseFile(string path)
    {
        using var reader = File.OpenText(path);
        var stream = new YamlStream();
        stream.Load(reader);
        foreach (var doc in stream.Documents)
        {
            if (doc.RootNode is YamlSequenceNode seq)
            {
                foreach (var node in seq.Children.OfType<YamlMappingNode>())
                    ParsePrototype(node);
            }
            else if (doc.RootNode is YamlMappingNode map)
            {
                ParsePrototype(map);
            }
        }
    }

    void ParsePrototype(YamlMappingNode map)
    {
        if (!Scalar(map, "type").Equals("entity", StringComparison.OrdinalIgnoreCase))
            return;
        var id = Scalar(map, "id");
        if (string.IsNullOrWhiteSpace(id))
            return;

        var raw = new RawPrototype { Id = id };
        if (Get(map, "parent") is YamlScalarNode parentScalar)
        {
            if (!string.IsNullOrWhiteSpace(parentScalar.Value))
                raw.Parents.Add(parentScalar.Value!);
        }
        else if (Get(map, "parent") is YamlSequenceNode parentSeq)
        {
            foreach (var parent in parentSeq.Children.OfType<YamlScalarNode>())
                if (!string.IsNullOrWhiteSpace(parent.Value))
                    raw.Parents.Add(parent.Value!);
        }

        if (Get(map, "components") is YamlSequenceNode components)
        {
            foreach (var component in components.Children.OfType<YamlMappingNode>())
            {
                var type = Scalar(component, "type");
                // Icon is editor-only on PC — never let it overwrite Sprite (was poisoning
                // walls/windows with state:full and wiping layer stacks → wrong furniture).
                if (type.Equals("Sprite", StringComparison.OrdinalIgnoreCase))
                    raw.Sprite = ParseSprite(component);
                else if (type.Equals("IconSmooth", StringComparison.OrdinalIgnoreCase))
                    raw.Smooth = ParseSmooth(component);
                else if (type.Equals("EntityStorageVisuals", StringComparison.OrdinalIgnoreCase))
                    raw.Storage = ParseStorage(component);
            }
        }

        _raw[id] = raw;
    }

    static SpritePatch ParseSprite(YamlMappingNode component)
    {
        var patch = new SpritePatch();
        if (Has(component, "sprite"))
        {
            patch.HasPath = true;
            patch.Path = NormalizeRsi(Scalar(component, "sprite"));
        }
        if (Has(component, "state"))
        {
            patch.HasState = true;
            patch.State = NullIfBlank(Scalar(component, "state"));
        }
        if (TryBool(component, "noRot", out var noRot)
            || TryBool(component, "noRotation", out noRot))
            patch.NoRotation = noRot;
        if (TryParseDrawDepth(component, out var depth))
            patch.DrawDepth = depth;

        if (Get(component, "layers") is YamlSequenceNode layers)
        {
            patch.HasLayers = true;
            foreach (var child in layers.Children)
            {
                if (child is YamlScalarNode scalar)
                {
                    patch.Layers.Add(new SpriteLayer(
                        null, NullIfBlank(scalar.Value), true, 255, 255, 255,
                        0, 0, 1, 1, 0, null, null));
                    continue;
                }
                if (child is not YamlMappingNode layer)
                    continue;

                var path = NormalizeRsi(Scalar(layer, "sprite"));
                if (path is null)
                    path = NormalizeRsi(Scalar(layer, "rsi"));
                var state = NullIfBlank(Scalar(layer, "state"));
                var mapKey = ReadMapKey(layer);
                // PC Appearance visualizers default these overlays off until state says otherwise.
                var visible = Has(layer, "visible")
                    ? (!TryBool(layer, "visible", out var vis) || vis)
                    : !IsDefaultHiddenOverlay(mapKey, state);
                ReadColor(Scalar(layer, "color"), out var r, out var g, out var b);
                ReadVector(Get(layer, "offset"), 0, 0, out var ox, out var oy);
                ReadVector(Get(layer, "scale"), 1, 1, out var sx, out var sy);
                TryFloat(layer, "rotation", out var rotation);
                patch.Layers.Add(new SpriteLayer(
                    path, state, visible, r, g, b, ox, oy, sx, sy, rotation,
                    NullIfBlank(Scalar(layer, "shader")),
                    mapKey));
            }
        }

        return patch;
    }

    static StorageVisuals? ParseStorage(YamlMappingNode component)
    {
        var baseClosed = NullIfBlank(Scalar(component, "stateBaseClosed"));
        var baseOpen = NullIfBlank(Scalar(component, "stateBaseOpen"));
        var doorClosed = NullIfBlank(Scalar(component, "stateDoorClosed"));
        var doorOpen = NullIfBlank(Scalar(component, "stateDoorOpen"));
        if (baseClosed is null && baseOpen is null && doorClosed is null && doorOpen is null)
            return null;
        return new StorageVisuals(baseClosed, baseOpen, doorClosed, doorOpen);
    }

    static IconSmoothData? ParseSmooth(YamlMappingNode component)
    {
        var key = NullIfBlank(Scalar(component, "key"));
        var stateBase = NullIfBlank(Scalar(component, "base"));
        if (key is null || stateBase is null)
            return null;
        var mode = Scalar(component, "mode").ToLowerInvariant() switch
        {
            "cardinalflags" => IconSmoothMode.CardinalFlags,
            "diagonal" => IconSmoothMode.Diagonal,
            "nosprite" => IconSmoothMode.NoSprite,
            _ => IconSmoothMode.Corners,
        };
        string[]? additional = null;
        if (Get(component, "additionalKeys") is YamlSequenceNode seq)
        {
            var list = new List<string>();
            foreach (var child in seq.Children)
            {
                if (child is YamlScalarNode s && !string.IsNullOrWhiteSpace(s.Value))
                    list.Add(s.Value.Trim().Trim('"', '\''));
            }
            if (list.Count > 0)
                additional = list.ToArray();
        }

        return new IconSmoothData(key, stateBase, mode, additional);
    }

    ResolvedSprite? ResolveSprite(string id, HashSet<string> visiting)
    {
        if (_resolved.TryGetValue(id, out var cached))
            return cached;
        if (!visiting.Add(id) || !_raw.TryGetValue(id, out var raw))
            return null;

        ResolvedSprite? result = null;
        // Robust parent order: later parents overlay earlier parents, then the child overlays all.
        foreach (var parent in raw.Parents)
        {
            var inherited = ResolveSprite(parent, visiting);
            if (inherited is not null)
                result = Merge(result, inherited);
        }

        if (raw.Sprite is not null)
            result = Apply(result, raw.Sprite);
        visiting.Remove(id);
        if (result is not null)
            _resolved[id] = result;
        return result;
    }

    IconSmoothData? ResolveSmooth(string id, HashSet<string> visiting)
    {
        if (_smooth.TryGetValue(id, out var cached))
            return cached;
        if (!visiting.Add(id) || !_raw.TryGetValue(id, out var raw))
            return null;
        IconSmoothData? result = null;
        foreach (var parent in raw.Parents)
            result = ResolveSmooth(parent, visiting) ?? result;
        result = raw.Smooth ?? result;
        visiting.Remove(id);
        if (result is not null)
            _smooth[id] = result;
        return result;
    }

    StorageVisuals? ResolveStorage(string id, HashSet<string> visiting)
    {
        if (_storage.TryGetValue(id, out var cached))
            return cached;
        if (!visiting.Add(id) || !_raw.TryGetValue(id, out var raw))
            return null;
        StorageVisuals? result = null;
        foreach (var parent in raw.Parents)
            result = MergeStorage(result, ResolveStorage(parent, visiting));
        result = MergeStorage(result, raw.Storage);
        visiting.Remove(id);
        if (result is not null)
            _storage[id] = result;
        return result;
    }

    static StorageVisuals? MergeStorage(StorageVisuals? first, StorageVisuals? overlay)
    {
        if (overlay is null) return first;
        if (first is null) return overlay;
        return new StorageVisuals(
            overlay.Value.StateBaseClosed ?? first.Value.StateBaseClosed,
            overlay.Value.StateBaseOpen ?? first.Value.StateBaseOpen,
            overlay.Value.StateDoorClosed ?? first.Value.StateDoorClosed,
            overlay.Value.StateDoorOpen ?? first.Value.StateDoorOpen);
    }

    static ResolvedSprite Merge(ResolvedSprite? first, ResolvedSprite overlay) =>
        new(
            overlay.Path ?? first?.Path,
            overlay.State ?? first?.State,
            overlay.NoRotation || first?.NoRotation == true,
            overlay.DrawDepth ?? first?.DrawDepth,
            overlay.Layers.Count > 0 ? overlay.Layers : first?.Layers ?? Array.Empty<SpriteLayer>());

    static ResolvedSprite Apply(ResolvedSprite? inherited, SpritePatch patch) =>
        new(
            patch.HasPath ? patch.Path : inherited?.Path,
            patch.HasState ? patch.State : inherited?.State,
            patch.NoRotation ?? inherited?.NoRotation ?? false,
            patch.DrawDepth ?? inherited?.DrawDepth,
            patch.HasLayers ? patch.Layers.ToArray() : inherited?.Layers ?? Array.Empty<SpriteLayer>());

    static YamlNode? Get(YamlMappingNode map, string key)
    {
        foreach (var (nodeKey, value) in map.Children)
            if (nodeKey is YamlScalarNode scalar
                && scalar.Value?.Equals(key, StringComparison.OrdinalIgnoreCase) == true)
                return value;
        return null;
    }

    static bool Has(YamlMappingNode map, string key) => Get(map, key) is not null;
    static string Scalar(YamlMappingNode map, string key) =>
        (Get(map, key) as YamlScalarNode)?.Value?.Trim().Trim('"', '\'') ?? "";
    static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Equals("null", StringComparison.OrdinalIgnoreCase)
            ? null
            : value;

    static string? NormalizeRsi(string? value)
    {
        value = NullIfBlank(value);
        if (value is null)
            return null;
        value = value.Replace('\\', '/').TrimStart('/');
        if (!value.EndsWith(".rsi", StringComparison.OrdinalIgnoreCase)
            && !value.EndsWith(".rsic", StringComparison.OrdinalIgnoreCase)
            && !value.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            value += ".rsi";
        return value;
    }

    static bool TryBool(YamlMappingNode map, string key, out bool value) =>
        bool.TryParse(Scalar(map, key), out value);
    static bool TryInt(YamlMappingNode map, string key, out int value) =>
        int.TryParse(Scalar(map, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    static bool TryFloat(YamlMappingNode map, string key, out float value) =>
        float.TryParse(Scalar(map, key), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    /// <summary>PC Content.Shared.DrawDepth-style enum names used in YAML.</summary>
    static bool TryParseDrawDepth(YamlMappingNode component, out int depth)
    {
        if (TryInt(component, "drawdepth", out depth) || TryInt(component, "drawDepth", out depth))
            return true;
        var name = Scalar(component, "drawdepth");
        if (string.IsNullOrWhiteSpace(name))
            name = Scalar(component, "drawDepth");
        depth = name.ToLowerInvariant() switch
        {
            "belowfloor" or "floortiles" => -13,
            "floor" or "floors" => -12,
            "deadmobs" => -5,
            "walls" => -2,
            "walltops" or "walltop" => -1,
            "objects" or "items" => 0,
            "doors" or "airlocks" => 1,
            "mobs" => 4,
            "overmobs" => 5,
            "effects" => 6,
            "ghosts" or "overlays" => 8,
            _ => 0,
        };
        return !string.IsNullOrWhiteSpace(name);
    }

    static string? ReadMapKey(YamlMappingNode layer)
    {
        var mapNode = Get(layer, "map");
        if (mapNode is YamlSequenceNode seq)
        {
            foreach (var child in seq.Children.OfType<YamlScalarNode>())
                if (!string.IsNullOrWhiteSpace(child.Value))
                    return child.Value!.Trim().Trim('"', '\'');
        }
        return NullIfBlank(Scalar(layer, "map"));
    }

    /// <summary>
    /// Layers that PC visualizers keep hidden until Appearance says otherwise.
    /// Without this, airlocks draw welded+bolted+panel on top of closed.
    /// </summary>
    public static bool IsDefaultHiddenOverlay(string? mapKey, string? state)
    {
        var key = (mapKey ?? "") + " " + (state ?? "");
        if (key.Contains("Weldable", StringComparison.OrdinalIgnoreCase)
            || key.Contains("BaseBolted", StringComparison.OrdinalIgnoreCase)
            || key.Contains("BaseEmergency", StringComparison.OrdinalIgnoreCase)
            || key.Contains("MaintenancePanel", StringComparison.OrdinalIgnoreCase)
            || key.Contains("WiresVisual", StringComparison.OrdinalIgnoreCase)
            || key.Contains("Electrified", StringComparison.OrdinalIgnoreCase)
            || key.Contains("BaseUnlit", StringComparison.OrdinalIgnoreCase))
            return true;
        if (state is not null
            && (state.Equals("welded", StringComparison.OrdinalIgnoreCase)
                || state.Equals("bolted_unlit", StringComparison.OrdinalIgnoreCase)
                || state.Equals("emergency_unlit", StringComparison.OrdinalIgnoreCase)
                || state.Equals("panel_open", StringComparison.OrdinalIgnoreCase)
                || state.EndsWith("_unlit", StringComparison.OrdinalIgnoreCase)
                || state.Contains("electrified", StringComparison.OrdinalIgnoreCase)))
            return true;
        return false;
    }

    static void ReadVector(YamlNode? node, float fallbackX, float fallbackY, out float x, out float y)
    {
        x = fallbackX;
        y = fallbackY;
        if (node is YamlSequenceNode seq && seq.Children.Count >= 2)
        {
            float.TryParse((seq.Children[0] as YamlScalarNode)?.Value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out x);
            float.TryParse((seq.Children[1] as YamlScalarNode)?.Value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out y);
        }
        else if (node is YamlScalarNode scalar && scalar.Value is { } raw)
        {
            var parts = raw.Trim('(', ')', '[', ']').Split(',');
            if (parts.Length >= 2)
            {
                float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x);
                float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y);
            }
        }
    }

    static void ReadColor(string? value, out byte r, out byte g, out byte b)
    {
        r = g = b = 255;
        if (string.IsNullOrWhiteSpace(value))
            return;
        var s = value.Trim().Trim('"', '\'').TrimStart('#');
        if (s.Length is not (6 or 8)
            || !uint.TryParse(s[..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            return;
        r = (byte)(rgb >> 16);
        g = (byte)(rgb >> 8);
        b = (byte)rgb;
    }
}
