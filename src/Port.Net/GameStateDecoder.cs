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

/// <summary>One drawable entity for the Android ghost viewport.</summary>
public readonly record struct WorldEntityDraw(
    NetEntity Entity,
    float X,
    float Y,
    float Rotation,
    string? RsiPath,
    byte R,
    byte G,
    byte B,
    bool IsControlled);

public sealed record WorldSnapshot(
    EyeSnapshot? Eye,
    IReadOnlyList<WorldEntityDraw> Entities,
    GameTick ToSequence,
    string Detail);

public static class GameStateDecoder
{
    const int MaxDrawEntities = 3500;

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
            var sprites = new Dictionary<NetEntity, (string? Path, byte R, byte G, byte B)>(Math.Min(entities.Count, 512));

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
                localPos = cx.LocalPosition;
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
                    byte r = spr.R, g = spr.G, b = spr.B;
                    if (r == 0 && g == 0 && b == 0)
                    {
                        if (!string.IsNullOrEmpty(spr.Path)) { r = 200; g = 190; b = 160; }
                        else { r = 70; g = 95; b = 130; }
                    }

                    drawEarly.Add(new WorldEntityDraw(
                        ent, wp.X, wp.Y, (float)xf.Rotation.Theta,
                        spr.Path, r, g, b, IsControlled: false));
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
                byte r = spr.R, g = spr.G, b = spr.B;
                if (r == 0 && g == 0 && b == 0)
                {
                    if (isCtrl) { r = 80; g = 220; b = 255; }
                    else if (!string.IsNullOrEmpty(spr.Path)) { r = 200; g = 190; b = 160; }
                    else { r = 90; g = 110; b = 140; }
                }

                drawList.Add(new WorldEntityDraw(
                    ent, wp.X, wp.Y, (float)xf.Rotation.Theta,
                    spr.Path, r, g, b, isCtrl));
            }

            // Controlled first for camera / highlight.
            drawList.Sort((a, b) => b.IsControlled.CompareTo(a.IsControlled));

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

    static void TryExtractSprite(
        object state,
        NetEntity ent,
        Dictionary<NetEntity, (string? Path, byte R, byte G, byte B)> sprites)
    {
        var tn = state.GetType().Name;
        if (!tn.Contains("Sprite", StringComparison.OrdinalIgnoreCase))
            return;

        string? path = null;
        byte r = 0, g = 0, b = 0;
        var t = state.GetType();

        foreach (var name in new[] { "RSI", "Rsi", "RsiPath", "SpritePath", "BaseRSI", "Path" })
        {
            var p = t.GetProperty(name)?.GetValue(state)
                    ?? t.GetField(name)?.GetValue(state);
            if (p is null)
                continue;
            var s = p.ToString();
            if (!string.IsNullOrWhiteSpace(s) && s!.Contains('/'))
            {
                path = s.Replace('\\', '/');
                if (path.StartsWith("/Textures/", StringComparison.OrdinalIgnoreCase))
                    path = path["/Textures/".Length..];
                else if (path.StartsWith("Textures/", StringComparison.OrdinalIgnoreCase))
                    path = path["Textures/".Length..];
                break;
            }
        }

        // Layers collection — first RSI-like string.
        if (path is null)
        {
            var layers = t.GetProperty("Layers")?.GetValue(state)
                         ?? t.GetField("Layers")?.GetValue(state);
            if (layers is System.Collections.IEnumerable en)
            {
                foreach (var layer in en)
                {
                    if (layer is null) continue;
                    var lt = layer.GetType();
                    foreach (var name in new[] { "RsiPath", "RSI", "Path", "ActualRsi" })
                    {
                        var v = lt.GetProperty(name)?.GetValue(layer)
                                ?? lt.GetField(name)?.GetValue(layer);
                        var s = v?.ToString();
                        if (!string.IsNullOrWhiteSpace(s) && s!.Contains('/'))
                        {
                            path = s.Replace('\\', '/');
                            break;
                        }
                    }
                    if (path is not null)
                        break;
                }
            }
        }

        foreach (var name in new[] { "Color", "Modulate" })
        {
            var c = t.GetProperty(name)?.GetValue(state)
                    ?? t.GetField(name)?.GetValue(state);
            if (c is null) continue;
            var ct = c.GetType();
            try
            {
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
            }
            catch { /* ignore */ }
            break;
        }

        if (path is not null || r != 0 || g != 0 || b != 0)
            sprites[ent] = (path, r, g, b);
    }
}
