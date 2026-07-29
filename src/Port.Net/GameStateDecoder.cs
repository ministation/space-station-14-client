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

public static class GameStateDecoder
{
    public static bool TryDecode(
        IRobustSerializer serializer,
        byte[] payload,
        Guid localUserId,
        out EyeSnapshot? eye,
        out GameTick toSequence,
        out string error)
    {
        eye = null;
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

            if (me?.ControlledEntity is not { } controlled || !controlled.IsValid())
            {
                error = $"no ControlledEntity (players={players.Count} status={me?.Status})";
                eye = new EyeSnapshot(
                    default, default, default, default, true,
                    state.EntityStates.Value?.Count ?? 0,
                    players.Count,
                    state.ToSequence,
                    error);
                return false;
            }

            Vector2 localPos = default;
            Angle rot = default;
            Vector2 eyeOff = default;
            var drawFov = true;
            var foundXform = false;
            var foundEye = false;

            var entities = state.EntityStates.Value ?? Array.Empty<EntityState>();
            foreach (var es in entities)
            {
                if (es.NetEntity != controlled)
                    continue;

                foreach (var change in es.ComponentChanges.Span)
                {
                    if (change.State is null)
                        continue;

                    var st = change.State;
                    if (st is TransformComponentState xform)
                    {
                        localPos = xform.LocalPosition;
                        rot = xform.Rotation;
                        foundXform = true;
                        continue;
                    }

                    var tn = st.GetType().Name;
                    if (!tn.Contains("EyeComponent", StringComparison.Ordinal))
                        continue;

                    var t = st.GetType();
                    if (t.GetProperty("Offset")?.GetValue(st) is Vector2 vo)
                        eyeOff = vo;
                    else if (t.GetField("Offset")?.GetValue(st) is Vector2 vo2)
                        eyeOff = vo2;

                    if (t.GetProperty("DrawFov")?.GetValue(st) is bool df)
                        drawFov = df;
                    else if (t.GetField("DrawFov")?.GetValue(st) is bool df2)
                        drawFov = df2;

                    foundEye = true;
                }

                break;
            }

            var detail = $"ent={controlled} xform={foundXform} eye={foundEye} pos=({localPos.X:0.##},{localPos.Y:0.##})";
            eye = new EyeSnapshot(
                controlled, localPos, rot, eyeOff, drawFov,
                entities.Count, players.Count, state.ToSequence, detail);
            return foundXform || foundEye;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }
}
