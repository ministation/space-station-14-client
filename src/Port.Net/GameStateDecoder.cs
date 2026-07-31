using System.Numerics;
using Robust.Shared.GameObjects;
using Robust.Shared.GameStates;
using Robust.Shared.Maths;
using Robust.Shared.Network;
using Robust.Shared.Serialization;
using Robust.Shared.Timing;

namespace Port.Net;

public sealed record EyeSnapshot(
    NetEntity Controlled,
    Vector2 LocalPosition,
    Angle Rotation,
    Vector2 EyeOffset,
    bool DrawFov,
    int EntityStateCount,
    int PlayerStateCount,
    GameTick ToSequence,
    string Detail);

/// <summary>One drawable entity/layer for the Android ghost viewport.</summary>
public readonly record struct WorldEntityDraw(
    NetEntity Entity,
    float X,
    float Y,
    float Rotation,
    string? RsiPath,
    byte R,
    byte G,
    byte B,
    bool IsControlled,
    int DrawDepth = 50,
    string? StateName = null,
    bool Visible = true,
    float OffsetX = 0,
    float OffsetY = 0,
    bool NoRotation = false,
    string? Label = null,
    /// <summary>RSI direction index override (−1 = derive from Rotation). Used by IconSmooth corners.</summary>
    int DirOverride = -1,
    bool IsGhost = false,
    float ScaleX = 1f,
    float ScaleY = 1f,
    float RotationOffset = 0f,
    /// <summary>PC SpriteComponent.SnapCardinals — force RSI dir 0.</summary>
    bool SnapCardinals = false);

public sealed record WorldSnapshot(
    EyeSnapshot? Eye,
    IReadOnlyList<WorldEntityDraw> Entities,
    GameTick ToSequence,
    string Detail,
    IReadOnlyList<WorldTileDraw>? Tiles = null,
    IReadOnlyList<WorldAudioCue>? Audio = null);

public readonly record struct WorldTileDraw(
    float X,
    float Y,
    byte R,
    byte G,
    byte B,
    string? RsiPath = null,
    string? StateName = null,
    byte Variant = 0,
    byte RotationMirroring = 0,
    float Rotation = 0f);

/// <summary>Networked AudioComponent cue (PC SharedAudioSystem → Android SoundPool).</summary>
public readonly record struct WorldAudioCue(
    NetEntity Entity,
    string FileName,
    float X,
    float Y,
    float VolumeDb,
    float MaxDistance,
    bool Global,
    bool Loop,
    bool Playing);

public static class GameStateDecoder
{
    const int MaxDrawEntities = 20_000;

    public static bool TryDecode(
        IRobustSerializer serializer,
        byte[] payload,
        Guid localUserId,
        out EyeSnapshot? eye,
        out GameTick toSequence,
        out string error)
        => TryDecodeWorld(serializer, payload, localUserId, out eye, out _, out toSequence, out error);

    public static bool TryDecodeWorld(
        IRobustSerializer serializer,
        byte[] payload,
        Guid localUserId,
        out EyeSnapshot? eye,
        out WorldSnapshot? world,
        out GameTick toSequence,
        out string error)
    {
        eye = null;
        world = null;
        toSequence = default;
        error = "";
        try
        {
            using var stream = new MemoryStream(payload, writable: false);
            serializer.DeserializeDirect(stream, out GameState state);
            toSequence = state.ToSequence;

            SessionState? me = null;
            var players = state.PlayerStates.Value ?? Array.Empty<SessionState>();
            foreach (var p in players)
            {
                if (p.UserId.UserId == localUserId)
                {
                    me = p;
                    break;
                }
            }

            var entities = state.EntityStates.Value ?? Array.Empty<EntityState>();
            var xforms = new Dictionary<NetEntity, TransformComponentState>(entities.Count);
            var sprites = new Dictionary<NetEntity, SpriteVisual>(Math.Min(entities.Count, 512));

            foreach (var es in entities)
            {
                foreach (var change in es.ComponentChanges.Span)
                {
                    if (change.State is null)
                        continue;
                    if (change.State is TransformComponentState xform)
                        xforms[es.NetEntity] = xform;
                    else
                        TryExtractSprite(change.State, es.NetEntity, sprites);
                }
            }

            NetEntity controlled = default;
            if (me?.ControlledEntity is { } c && c.IsValid())
                controlled = c;

            Vector2 localPos = default;
            Angle rot = default;
            Vector2 eyeOff = default;
            var drawFov = true;
            var foundXform = false;
            var foundEye = false;

            if (controlled.IsValid() && xforms.TryGetValue(controlled, out var cx))
            {
                var worldCache = new Dictionary<NetEntity, Vector2>(xforms.Count);
                localPos = ResolveWorldPos(controlled, xforms, worldCache);
                rot = cx.Rotation;
                foundXform = true;
            }

            if (controlled.IsValid())
            {
                foreach (var es in entities)
                {
                    if (es.NetEntity != controlled)
                        continue;
                    foreach (var change in es.ComponentChanges.Span)
                    {
                        if (change.State is null)
                            continue;
                        var tn = change.State.GetType().Name;
                        if (!tn.Contains("EyeComponent", StringComparison.Ordinal))
                            continue;
                        var t = change.State.GetType();
                        if (t.GetProperty("Offset")?.GetValue(change.State) is Vector2 vo)
                            eyeOff = vo;
                        else if (t.GetField("Offset")?.GetValue(change.State) is Vector2 vo2)
                            eyeOff = vo2;
                        if (t.GetProperty("DrawFov")?.GetValue(change.State) is bool df)
                            drawFov = df;
                        else if (t.GetField("DrawFov")?.GetValue(change.State) is bool df2)
                            drawFov = df2;
                        foundEye = true;
                    }
                    break;
                }
            }

            if (!controlled.IsValid())
            {
                // Observe / lobby ghost often has no ControlledEntity yet — still draw the world.
                var worldPosCacheEarly = new Dictionary<NetEntity, Vector2>(xforms.Count);
                var drawEarly = new List<WorldEntityDraw>(Math.Min(xforms.Count, MaxDrawEntities));
                Vector2 sum = default;
                var nSum = 0;
                foreach (var (ent, xf) in xforms)
                {
                    if (drawEarly.Count >= MaxDrawEntities)
                        break;
                    var wp = ResolveWorldPos(ent, xforms, worldPosCacheEarly);
                    sprites.TryGetValue(ent, out var spr);
                    byte r = spr?.R ?? 0, g = spr?.G ?? 0, b = spr?.B ?? 0;
                    if (r == 0 && g == 0 && b == 0)
                    {
                        if (!string.IsNullOrEmpty(spr?.Path)) { r = 200; g = 190; b = 160; }
                        else { r = 70; g = 95; b = 130; }
                    }

                    drawEarly.Add(new WorldEntityDraw(
                        ent, wp.X, wp.Y, (float)xf.Rotation.Theta,
                        spr?.Path, r, g, b, IsControlled: false,
                        spr?.DrawDepth ?? GuessDepth(spr?.Path), spr?.State));
                    sum += wp;
                    nSum++;
                }

                if (nSum > 0)
                    localPos = sum / nSum;

                error = drawEarly.Count > 0
                    ? $"observe cam free (no ControlledEntity) draw={drawEarly.Count}"
                    : $"no ControlledEntity (players={players.Count} status={me?.Status} xforms={xforms.Count})";
                eye = new EyeSnapshot(
                    default, localPos, default, default, true,
                    entities.Count, players.Count, state.ToSequence, error);
                world = new WorldSnapshot(eye, drawEarly, state.ToSequence, error);
                return drawEarly.Count > 0;
            }

            var worldPosCache = new Dictionary<NetEntity, Vector2>(xforms.Count);
            var drawList = new List<WorldEntityDraw>(Math.Min(xforms.Count, MaxDrawEntities));
            foreach (var (ent, xf) in xforms)
            {
                if (drawList.Count >= MaxDrawEntities)
                    break;
                var wp = ResolveWorldPos(ent, xforms, worldPosCache);
                sprites.TryGetValue(ent, out var spr);
                var isCtrl = ent == controlled;
                byte r = spr?.R ?? 0, g = spr?.G ?? 0, b = spr?.B ?? 0;
                if (r == 0 && g == 0 && b == 0)
                {
                    if (isCtrl) { r = 80; g = 220; b = 255; }
                    else if (!string.IsNullOrEmpty(spr?.Path)) { r = 200; g = 190; b = 160; }
                    else { r = 90; g = 110; b = 140; }
                }

                drawList.Add(new WorldEntityDraw(
                    ent, wp.X, wp.Y, (float)xf.Rotation.Theta,
                    spr?.Path, r, g, b, isCtrl,
                    spr?.DrawDepth ?? GuessDepth(spr?.Path), spr?.State));
            }

            // Depth then controlled.
            drawList.Sort((a, b) =>
            {
                var d = a.DrawDepth.CompareTo(b.DrawDepth);
                if (d != 0) return d;
                return b.IsControlled.CompareTo(a.IsControlled);
            });

            var detail =
                $"ent={controlled} xform={foundXform} eye={foundEye} " +
                $"pos=({localPos.X:0.##},{localPos.Y:0.##}) draw={drawList.Count}/{xforms.Count}";
            eye = new EyeSnapshot(
                controlled, localPos, rot, eyeOff, drawFov,
                entities.Count, players.Count, state.ToSequence, detail);
            world = new WorldSnapshot(eye, drawList, state.ToSequence, detail);
            return foundXform || foundEye || drawList.Count > 0;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    static Vector2 ResolveWorldPos(
        NetEntity ent,
        Dictionary<NetEntity, TransformComponentState> xforms,
        Dictionary<NetEntity, Vector2> cache)
    {
        if (cache.TryGetValue(ent, out var cached))
            return cached;

        if (!xforms.TryGetValue(ent, out var xf))
            return default;

        // Guard against cycles.
        var visiting = new HashSet<NetEntity>();
        var cur = ent;
        var pos = Vector2.Zero;
        var rot = Angle.Zero;
        for (var depth = 0; depth < 24; depth++)
        {
            if (!xforms.TryGetValue(cur, out var t))
                break;
            if (!visiting.Add(cur))
                break;

            // local → parent space (rotation then translate)
            pos = t.Rotation.RotateVec(pos) + t.LocalPosition;
            rot = t.Rotation + rot;

            if (!t.ParentID.IsValid() || t.ParentID == cur)
                break;
            cur = t.ParentID;
        }

        cache[ent] = pos;
        return pos;
    }

    public static void TryExtractSpritePublic(
        object state,
        NetEntity ent,
        Dictionary<NetEntity, SpriteVisual> sprites)
        => TryExtractSprite(state, ent, sprites);

    public sealed class SpriteVisual
    {
        /// <summary>True after a real SpriteComponentState was applied (not prototype YAML guess).</summary>
        public bool FromNetwork;
        public string? Path;
        public string? State;
        public byte R, G, B;
        public bool HasColor;
        public bool HasDrawDepth;
        public bool Visible = true;
        public int DrawDepth = 50;
        public bool NoRotation;
        /// <summary>PC SpriteComponent.SnapCardinals — RSI dir snaps to cardinals / dir0.</summary>
        public bool SnapCardinals;
        public readonly List<LayerVis> Layers = new();
    }

    public readonly record struct LayerVis(
        string? Path,
        string? State,
        int Depth,
        bool Visible,
        byte R,
        byte G,
        byte B,
        float OffsetX = 0,
        float OffsetY = 0,
        bool HasDepth = false,
        float ScaleX = 1f,
        float ScaleY = 1f,
        float RotationOffset = 0f,
        string? MapKey = null);

    static void TryExtractSprite(
        object state,
        NetEntity ent,
        Dictionary<NetEntity, SpriteVisual> sprites)
    {
        var tn = state.GetType().Name;
        // Avoid matching unrelated *Sprite* types / IconSmooth intermediate junk.
        if (!tn.Contains("SpriteComponent", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(tn, "SpriteComponentState", StringComparison.OrdinalIgnoreCase)
            && !(tn.Contains("Sprite", StringComparison.OrdinalIgnoreCase)
                 && tn.Contains("State", StringComparison.OrdinalIgnoreCase)
                 && !tn.Contains("IconSmooth", StringComparison.OrdinalIgnoreCase)))
            return;

        sprites.TryGetValue(ent, out var prev);
        SpriteVisual vis;
        var t = state.GetType();

        // Probe fields first — empty SpriteComponentState must not wipe prototype art (mobs/players).
        string? netPath = null;
        foreach (var name in new[] { "RSI", "Rsi", "RsiPath", "SpritePath", "BaseRSI", "Path", "ActualRsi" })
        {
            var p = t.GetProperty(name)?.GetValue(state)
                    ?? t.GetField(name)?.GetValue(state);
            netPath = NormalizeRsiPath(p);
            if (netPath is not null) break;
        }

        var layersObj = t.GetProperty("Layers")?.GetValue(state)
                        ?? t.GetField("Layers")?.GetValue(state);
        var layerCount = 0;
        if (layersObj is System.Collections.IEnumerable probeEn)
        {
            foreach (var _ in probeEn)
            {
                layerCount++;
                if (layerCount > 0) break;
            }
        }

        if (netPath is null && layerCount == 0
            && t.GetProperty("Visible")?.GetValue(state) is not bool
            && t.GetField("Visible")?.GetValue(state) is not bool)
        {
            // Nothing useful — keep prototype / previous sprite.
            return;
        }

        // Prototype-first (PC netsync:false): YAML owns RSI path/layers after compose.
        // Never flip FromNetwork→true — that let the next MsgState replace chair/locker/door
        // stacks with sparse/wrong network layers (~90% wrong sprites).
        var protoOwned = prev is { FromNetwork: false };
        if (protoOwned)
            vis = CloneVisual(prev!);
        else if (prev is { FromNetwork: true })
            vis = prev;
        else
            vis = new SpriteVisual { FromNetwork = true };

        // Proto-owned: ignore network RSI path/state (Appearance/Door handle visuals).
        if (!protoOwned && netPath is not null)
            vis.Path = netPath;

        if (!protoOwned)
        {
            foreach (var name in new[] { "Layer", "State", "RsiState", "BaseRsiState", "ActualRsiState" })
            {
                var p = t.GetProperty(name)?.GetValue(state)
                        ?? t.GetField(name)?.GetValue(state);
                if (p is string s && !string.IsNullOrWhiteSpace(s))
                {
                    vis.State = s;
                    break;
                }

                var asStr = p?.ToString();
                if (!string.IsNullOrWhiteSpace(asStr) && asStr is not "null")
                {
                    if (asStr.Length < 64 && !asStr.Contains('.'))
                    {
                        vis.State = asStr;
                        break;
                    }
                }
            }
        }

        foreach (var name in new[] { "DrawDepth", "DrawDepthSet" })
        {
            var p = t.GetProperty(name)?.GetValue(state)
                    ?? t.GetField(name)?.GetValue(state);
            if (p is null) continue;
            if (p is int di) { vis.DrawDepth = di; vis.HasDrawDepth = true; }
            else if (p is Enum)
            {
                vis.DrawDepth = Convert.ToInt32(p);
                vis.HasDrawDepth = true;
            }
            else if (int.TryParse(p.ToString(), out var parsed))
            {
                vis.DrawDepth = parsed;
                vis.HasDrawDepth = true;
            }
        }

        var noRot = t.GetProperty("NoRotation")?.GetValue(state)
                    ?? t.GetField("NoRotation")?.GetValue(state)
                    ?? t.GetProperty("NoRot")?.GetValue(state)
                    ?? t.GetField("NoRot")?.GetValue(state);
        if (noRot is bool nr)
            vis.NoRotation = nr;

        var rootVis = t.GetProperty("Visible")?.GetValue(state)
                      ?? t.GetField("Visible")?.GetValue(state);
        if (rootVis is bool rv)
            vis.Visible = rv;

        byte r = vis.R, g = vis.G, b = vis.B;
        foreach (var name in new[] { "Color", "Modulate" })
        {
            var c = t.GetProperty(name)?.GetValue(state)
                    ?? t.GetField(name)?.GetValue(state);
            if (c is null) continue;
            if (TryReadColor(c, out r, out g, out b))
            {
                vis.R = r;
                vis.G = g;
                vis.B = b;
                vis.HasColor = true;
            }
            break;
        }

        var layers = t.GetProperty("Layers")?.GetValue(state)
                     ?? t.GetField("Layers")?.GetValue(state);
        // Proto-owned stacks are sealed — network layers (even equal count) used to wipe
        // YAML base+door / computer body+keys with wrong RSI states every MsgState.
        if (!protoOwned && layers is System.Collections.IEnumerable en)
        {
            var parsed = new List<LayerVis>();
            foreach (var layer in en)
            {
                if (layer is null) continue;
                var lt = layer.GetType();
                string? path = null;
                string? stName = null;
                string? mapKey = null;
                bool? visibleExplicit = null;
                byte lr = r, lg = g, lb = b;
                var depth = vis.DrawDepth;
                var hasDepth = false;
                float ox = 0, oy = 0;
                float sx = 1, sy = 1, rotationOffset = 0;

                foreach (var name in new[] { "RsiPath", "RSI", "Path", "ActualRsi", "Rsi", "Sprite" })
                {
                    var v = lt.GetProperty(name)?.GetValue(layer)
                            ?? lt.GetField(name)?.GetValue(layer);
                    path = NormalizeRsiPath(v);
                    if (path is not null) break;
                }

                foreach (var name in new[] { "State", "RsiState", "ActualState", "AnimationState" })
                {
                    var v = lt.GetProperty(name)?.GetValue(layer)
                            ?? lt.GetField(name)?.GetValue(layer);
                    if (v is null) continue;
                    var s = v as string ?? v.ToString();
                    if (!string.IsNullOrWhiteSpace(s) && s is not "null" && s.Length < 64)
                    {
                        stName = s;
                        break;
                    }
                }

                foreach (var name in new[] { "MapKey", "Shader", "Name" })
                {
                    // Map keys sometimes live on layer copy as string; skip Shader misuse.
                    if (name.Equals("Shader", StringComparison.Ordinal)) continue;
                    var v = lt.GetProperty(name)?.GetValue(layer)
                            ?? lt.GetField(name)?.GetValue(layer);
                    if (v is null) continue;
                    if (v is string mk && !string.IsNullOrWhiteSpace(mk) && mk.Length < 96)
                    {
                        mapKey = mk;
                        break;
                    }
                    // Enum map keys (DoorVisualLayers.Base) arrive as non-string.
                    if (v is Enum || v.GetType().IsEnum)
                    {
                        var es = v.ToString();
                        if (!string.IsNullOrWhiteSpace(es) && es.Length < 96)
                        {
                            mapKey = es;
                            break;
                        }
                    }
                }

                var visProp = lt.GetProperty("Visible")?.GetValue(layer)
                              ?? lt.GetField("Visible")?.GetValue(layer);
                if (visProp is bool vb) visibleExplicit = vb;

                foreach (var name in new[] { "DrawDepth", "DrawDepthSet" })
                {
                    var v = lt.GetProperty(name)?.GetValue(layer)
                            ?? lt.GetField(name)?.GetValue(layer);
                    if (v is null) continue;
                    if (v is int di) { depth = di; hasDepth = true; break; }
                    if (v is Enum) { depth = Convert.ToInt32(v); hasDepth = true; break; }
                    if (int.TryParse(v.ToString(), out var parsedDepth)) { depth = parsedDepth; hasDepth = true; break; }
                }

                foreach (var name in new[] { "Color", "ColorOverride" })
                {
                    var c = lt.GetProperty(name)?.GetValue(layer)
                            ?? lt.GetField(name)?.GetValue(layer);
                    if (c is not null && TryReadColor(c, out lr, out lg, out lb))
                        break;
                }

                var off = lt.GetProperty("Offset")?.GetValue(layer)
                          ?? lt.GetField("Offset")?.GetValue(layer);
                if (off is Vector2 ov)
                {
                    ox = ov.X;
                    oy = ov.Y;
                }

                var scale = lt.GetProperty("Scale")?.GetValue(layer)
                            ?? lt.GetField("Scale")?.GetValue(layer);
                if (scale is Vector2 sv)
                {
                    sx = sv.X;
                    sy = sv.Y;
                }

                var rotation = lt.GetProperty("Rotation")?.GetValue(layer)
                               ?? lt.GetField("Rotation")?.GetValue(layer);
                if (rotation is Angle angle)
                    rotationOffset = (float)angle.Theta;
                else if (rotation is float rf)
                    rotationOffset = rf;
                else if (rotation is double rd)
                    rotationOffset = (float)rd;

                // Layer without own RSI uses component base path (proto or network).
                // Do NOT promote root State onto layers — that turns every computer into one screen.
                path ??= vis.Path;
                if (path is null && stName is null)
                    continue;

                var visible = visibleExplicit
                              ?? !Port.Content.PrototypeSpriteIndex.IsDefaultHiddenOverlay(mapKey, stName);

                parsed.Add(new LayerVis(
                    path, stName, depth, visible, lr, lg, lb, ox, oy, hasDepth,
                    sx, sy, rotationOffset, mapKey));
                if (vis.Path is null && path is not null)
                    vis.Path = path;
            }

            // Never shrink a richer prototype stack with a sparse/wrong network layer list
            // (one "virology" layer used to wipe computer body+keyboard).
            // Also never replace YAML door stacks with state-less network layers.
            if (parsed.Count > 0 && (vis.Layers.Count == 0 || parsed.Count >= vis.Layers.Count))
            {
                var parsedHasStates = false;
                foreach (var l in parsed)
                {
                    if (!string.IsNullOrWhiteSpace(l.State))
                    {
                        parsedHasStates = true;
                        break;
                    }
                }

                var prevHasStates = false;
                foreach (var l in vis.Layers)
                {
                    if (!string.IsNullOrWhiteSpace(l.State))
                    {
                        prevHasStates = true;
                        break;
                    }
                }

                if (!(prevHasStates && !parsedHasStates))
                {
                    vis.Layers.Clear();
                    vis.Layers.AddRange(parsed);
                }
            }
        }

        var snapNet = t.GetProperty("SnapCardinals")?.GetValue(state)
                      ?? t.GetField("SnapCardinals")?.GetValue(state);
        if (snapNet is bool sc)
            vis.SnapCardinals = sc;

        if (vis.Path is not null || vis.Layers.Count > 0 || vis.HasColor || !vis.Visible)
            sprites[ent] = vis;
    }

    static SpriteVisual CloneVisual(SpriteVisual src)
    {
        var clone = new SpriteVisual
        {
            FromNetwork = src.FromNetwork,
            Path = src.Path,
            State = src.State,
            R = src.R,
            G = src.G,
            B = src.B,
            HasColor = src.HasColor,
            HasDrawDepth = src.HasDrawDepth,
            Visible = src.Visible,
            DrawDepth = src.DrawDepth,
            NoRotation = src.NoRotation,
            SnapCardinals = src.SnapCardinals,
        };
        clone.Layers.AddRange(src.Layers);
        return clone;
    }

    static bool TryReadColor(object c, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        try
        {
            var ct = c.GetType();
            float rf = 1, gf = 1, bf = 1;
            if (ct.GetProperty("R")?.GetValue(c) is float fr) rf = fr;
            else if (ct.GetField("R")?.GetValue(c) is float fr2) rf = fr2;
            else if (ct.GetProperty("R")?.GetValue(c) is byte br) rf = br / 255f;
            if (ct.GetProperty("G")?.GetValue(c) is float fg) gf = fg;
            else if (ct.GetField("G")?.GetValue(c) is float fg2) gf = fg2;
            else if (ct.GetProperty("G")?.GetValue(c) is byte bg) gf = bg / 255f;
            if (ct.GetProperty("B")?.GetValue(c) is float fb) bf = fb;
            else if (ct.GetField("B")?.GetValue(c) is float fb2) bf = fb2;
            else if (ct.GetProperty("B")?.GetValue(c) is byte bb) bf = bb / 255f;
            r = (byte)Math.Clamp((int)(rf * 255), 0, 255);
            g = (byte)Math.Clamp((int)(gf * 255), 0, 255);
            b = (byte)Math.Clamp((int)(bf * 255), 0, 255);
            return true;
        }
        catch
        {
            return false;
        }
    }

    static string? NormalizeRsiPath(object? value)
    {
        if (value is null)
            return null;

        // ResourcePath / ResPath often expose CanonString / ToString.
        string? s = null;
        var vt = value.GetType();
        s = vt.GetProperty("CanonString")?.GetValue(value)?.ToString()
            ?? vt.GetProperty("Path")?.GetValue(value)?.ToString()
            ?? value.ToString();
        if (string.IsNullOrWhiteSpace(s))
            return null;

        var path = s.Replace('\\', '/').Trim();
        if (path.StartsWith("/Textures/", StringComparison.OrdinalIgnoreCase))
            path = path["/Textures/".Length..];
        else if (path.StartsWith("Textures/", StringComparison.OrdinalIgnoreCase))
            path = path["Textures/".Length..];
        else if (path.StartsWith('/'))
            path = path.TrimStart('/');

        if (!path.Contains('/') && !path.EndsWith(".rsi", StringComparison.OrdinalIgnoreCase))
            return null;
        return path;
    }

    public static int GuessDepth(string? rsiPath)
    {
        if (string.IsNullOrWhiteSpace(rsiPath)) return 40;
        var p = rsiPath.Replace('\\', '/');
        if (p.Contains("Tiles/", StringComparison.OrdinalIgnoreCase)
            || p.Contains("/Floor", StringComparison.OrdinalIgnoreCase))
            return 0;
        if (p.Contains("Wall", StringComparison.OrdinalIgnoreCase)
            || p.Contains("grille", StringComparison.OrdinalIgnoreCase))
            return 20;
        if (p.Contains("cable", StringComparison.OrdinalIgnoreCase)
            || p.Contains("wire", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Power/", StringComparison.OrdinalIgnoreCase))
            return 30;
        if (p.Contains("pipe", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Atmos", StringComparison.OrdinalIgnoreCase)
            || p.Contains("disposal", StringComparison.OrdinalIgnoreCase))
            return 35;
        if (p.Contains("Door", StringComparison.OrdinalIgnoreCase)
            || p.Contains("airlock", StringComparison.OrdinalIgnoreCase)
            || p.Contains("windoor", StringComparison.OrdinalIgnoreCase)
            || p.Contains("shutter", StringComparison.OrdinalIgnoreCase))
            return 45;
        if (p.Contains("Mobs/", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Species/", StringComparison.OrdinalIgnoreCase)
            || p.Contains("ghost", StringComparison.OrdinalIgnoreCase))
            return 70;
        if (p.Contains("Clothing/", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Objects/", StringComparison.OrdinalIgnoreCase))
            return 55;
        return 50;
    }
}
