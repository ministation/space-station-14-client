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
        IReadOnlyList<SpriteLayer> Layers,
        bool SnapCardinals = false);

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
        /// <summary>Partial IconSmooth override (Goob often sets only <c>base:</c>).</summary>
        public IconSmoothPatch? Smooth;
        public StorageVisuals? Storage;
    }

    sealed class SpritePatch
    {
        public string? Path;
        public bool HasPath;
        public string? State;
        public bool HasState;
        public bool? NoRotation;
        public bool? SnapCardinals;
        public int? DrawDepth;
        public bool HasLayers;
        public readonly List<SpriteLayer> Layers = new();
    }

    /// <summary>Field-level IconSmooth patch — mirrors PC component inheritance.</summary>
    sealed class IconSmoothPatch
    {
        public string? Key;
        public bool HasKey;
        public string? StateBase;
        public bool HasBase;
        public IconSmoothMode? Mode;
        public string[]? AdditionalKeys;
        public bool HasAdditionalKeys;
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
        IconSmoothInfer.ClearCache();
        RsiAtlas.ClearCache();
        Root = contentFilesRoot;

        // Scan BOTH trees — ACZ may land files under Prototypes/ and Resources/Prototypes/.
        var roots = new List<string>();
        var primary = Path.Combine(contentFilesRoot, "Prototypes");
        var alt = Path.Combine(contentFilesRoot, "Resources", "Prototypes");
        if (Directory.Exists(primary)) roots.Add(primary);
        if (Directory.Exists(alt)) roots.Add(alt);

        if (roots.Count == 0)
        {
            log?.Invoke("prototypes: directory missing");
            return;
        }

        var scanned = 0;
        var failed = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var protoRoot in roots)
        foreach (var file in Directory.EnumerateFiles(protoRoot, "*.yml", SearchOption.AllDirectories)
                     .Concat(Directory.EnumerateFiles(protoRoot, "*.yaml", SearchOption.AllDirectories))
                     .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            // Prefer first tree's file when the same relative path appears twice.
            var rel = Path.GetRelativePath(protoRoot, file);
            if (!seen.Add(protoRoot + "|" + rel))
                continue;
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

        var smoothCount = _smooth.Count(kv => kv.Value is not null);
        log?.Invoke(
            $"prototypes: YAML parsed {_raw.Count} entities, {_resolved.Count(kv => kv.Value is not null)} sprites, " +
            $"{smoothCount} IconSmooth from {scanned} files in {roots.Count} root(s) (failed={failed})");
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
        if (TryBool(component, "snapCardinals", out var snap)
            || TryBool(component, "SnapCardinals", out snap))
            patch.SnapCardinals = snap;
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

    static IconSmoothPatch? ParseSmooth(YamlMappingNode component)
    {
        var patch = new IconSmoothPatch();
        if (Has(component, "key"))
        {
            patch.HasKey = true;
            patch.Key = NullIfBlank(Scalar(component, "key"));
        }

        if (Has(component, "base"))
        {
            patch.HasBase = true;
            patch.StateBase = NullIfBlank(Scalar(component, "base"));
        }

        if (Has(component, "mode"))
        {
            patch.Mode = Scalar(component, "mode").ToLowerInvariant() switch
            {
                "cardinalflags" => IconSmoothMode.CardinalFlags,
                "diagonal" => IconSmoothMode.Diagonal,
                "nosprite" => IconSmoothMode.NoSprite,
                _ => IconSmoothMode.Corners,
            };
        }

        if (Get(component, "additionalKeys") is YamlSequenceNode seq)
        {
            patch.HasAdditionalKeys = true;
            var list = new List<string>();
            foreach (var child in seq.Children)
            {
                if (child is YamlScalarNode s && !string.IsNullOrWhiteSpace(s.Value))
                    list.Add(s.Value.Trim().Trim('"', '\''));
            }

            if (list.Count > 0)
                patch.AdditionalKeys = list.ToArray();
        }

        // Accept partials (base-only child overrides). Empty mapping → ignore.
        if (!patch.HasKey && !patch.HasBase && patch.Mode is null && !patch.HasAdditionalKeys)
            return null;
        return patch;
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
        // Later parents overlay earlier; child patch overlays all (PC component merge).
        foreach (var parent in raw.Parents)
            result = MergeSmooth(result, ResolveSmooth(parent, visiting));
        result = ApplySmoothPatch(result, raw.Smooth);
        visiting.Remove(id);
        if (result is not null)
            _smooth[id] = result;
        return result;
    }

    static IconSmoothData? MergeSmooth(IconSmoothData? first, IconSmoothData? overlay) =>
        overlay ?? first;

    static IconSmoothData? ApplySmoothPatch(IconSmoothData? current, IconSmoothPatch? patch)
    {
        if (patch is null)
            return current;

        var key = patch.HasKey ? patch.Key : current?.Key;
        var stateBase = patch.HasBase ? patch.StateBase : current?.StateBase;
        var mode = patch.Mode ?? current?.Mode ?? IconSmoothMode.Corners;
        var additional = patch.HasAdditionalKeys ? patch.AdditionalKeys : current?.AdditionalKeys;

        // Drawable modes need key+base after merge. NoSprite may omit base.
        if (string.IsNullOrWhiteSpace(key))
            return current;
        if (mode is not IconSmoothMode.NoSprite && string.IsNullOrWhiteSpace(stateBase))
            return current;

        return new IconSmoothData(key!, stateBase ?? "", mode, additional);
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
            overlay.Layers.Count > 0 ? overlay.Layers : first?.Layers ?? Array.Empty<SpriteLayer>(),
            overlay.SnapCardinals || first?.SnapCardinals == true);

    static ResolvedSprite Apply(ResolvedSprite? inherited, SpritePatch patch) =>
        new(
            patch.HasPath ? patch.Path : inherited?.Path,
            patch.HasState ? patch.State : inherited?.State,
            patch.NoRotation ?? inherited?.NoRotation ?? false,
            patch.DrawDepth ?? inherited?.DrawDepth,
            patch.HasLayers ? patch.Layers.ToArray() : inherited?.Layers ?? Array.Empty<SpriteLayer>(),
            patch.SnapCardinals ?? inherited?.SnapCardinals ?? false);

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
        if (DrawDepthResolver.TryParseName(name) is { } parsed)
        {
            depth = parsed;
            return true;
        }

        depth = 0;
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
        var map = mapKey ?? "";
        var st = state ?? "";
        var key = map + " " + st;

        // Lathe/techfab powered glow is bare "unlit" — must stay visible (PC LatheSystem).
        if (st.Equals("unlit", StringComparison.OrdinalIgnoreCase)
            && !map.Contains("DoorVisual", StringComparison.OrdinalIgnoreCase)
            && !map.Contains("BaseUnlit", StringComparison.OrdinalIgnoreCase))
            return false;

        // Inserting / panel overlays stay off until Appearance says otherwise.
        if (map.Contains("Inserting", StringComparison.OrdinalIgnoreCase)
            || st.Equals("inserting", StringComparison.OrdinalIgnoreCase)
            || map.Contains("MaterialStorageVisualLayers", StringComparison.OrdinalIgnoreCase))
            return true;

        if (key.Contains("Weldable", StringComparison.OrdinalIgnoreCase)
            || key.Contains("BaseBolted", StringComparison.OrdinalIgnoreCase)
            || key.Contains("BaseEmergency", StringComparison.OrdinalIgnoreCase)
            || key.Contains("MaintenancePanel", StringComparison.OrdinalIgnoreCase)
            || key.Contains("WiresVisual", StringComparison.OrdinalIgnoreCase)
            || key.Contains("Electrified", StringComparison.OrdinalIgnoreCase)
            || key.Contains("BaseUnlit", StringComparison.OrdinalIgnoreCase)
            || key.Contains("BaseEmagging", StringComparison.OrdinalIgnoreCase))
            return true;

        if (st.Equals("welded", StringComparison.OrdinalIgnoreCase)
            || st.Equals("bolted_unlit", StringComparison.OrdinalIgnoreCase)
            || st.Equals("emergency_unlit", StringComparison.OrdinalIgnoreCase)
            || st.Equals("closed_unlit", StringComparison.OrdinalIgnoreCase)
            || st.Equals("panel_open", StringComparison.OrdinalIgnoreCase)
            || st.Equals("sparks", StringComparison.OrdinalIgnoreCase)
            || st.Contains("electrified", StringComparison.OrdinalIgnoreCase)
            // Door overlays only — not lathe "unlit"
            || (st.EndsWith("_unlit", StringComparison.OrdinalIgnoreCase)
                && !st.Equals("unlit", StringComparison.OrdinalIgnoreCase)))
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
