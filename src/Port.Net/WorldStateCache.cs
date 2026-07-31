using System.Numerics;
using Robust.Shared.GameObjects;
using Robust.Shared.GameStates;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Network;
using Robust.Shared.Serialization;
using Robust.Shared.Timing;
using Port.Content;

namespace Port.Net;

/// <summary>
/// Merges GameState deltas into a persistent ghost-world snapshot:
/// transforms, sprites (component or prototype), map-grid tiles.
/// </summary>
public sealed class WorldStateCache
{
    /// <summary>Mobile hard cap — 10k+ tiles (all maps, eyeMap invalid) OOMs / crashes GLES.</summary>
    const int MaxDrawTiles = 2_400;
    const int ChunkDefault = 16;
    /// <summary>PC DrawDepth.BelowFloor (Default=0).</summary>
    const int BelowFloorDepth = -13;

    readonly Dictionary<NetEntity, TransformComponentState> _xforms = new();
    readonly Dictionary<NetEntity, GameStateDecoder.SpriteVisual> _sprites = new();
    readonly Dictionary<NetEntity, (NetEntity RelativeEntity, float RelativeRotation, float TargetRelativeRotation)> _movers = new();
    readonly Dictionary<NetEntity, string> _prototypes = new();
    readonly Dictionary<NetEntity, string> _names = new();
    readonly Dictionary<NetEntity, Dictionary<Vector2i, ChunkDatum>> _grids = new();
    readonly Dictionary<NetEntity, ushort> _gridChunkSize = new();
    readonly Dictionary<NetEntity, Vector2> _worldPosCache = new();
    /// <summary>Entities that have MapComponent — MapUid for GetWorldPosition / draw filter.</summary>
    readonly HashSet<NetEntity> _mapEntities = new();
    readonly Dictionary<NetEntity, NetEntity> _mapUidCache = new();
    /// <summary>Closed containers (lockers/crates) — hide contained children.</summary>
    readonly HashSet<NetEntity> _closedContainers = new();
    readonly HashSet<NetEntity> _openContainers = new();
    readonly Dictionary<NetEntity, bool> _containerOccludes = new();
    readonly HashSet<NetEntity> _visitScratch = new();
    readonly HashSet<(int Parent, int X, int Y, string Key)> _smoothTilesScratch = new();
    readonly Dictionary<NetEntity, (IconSmoothData Data, string Path, int Depth, int Parent, int Tx, int Ty)> _smoothByEntScratch = new();
    readonly List<WorldEntityDraw> _drawListScratch = new(4096);
    WorldEntityDraw[] _lastDrawSnapshot = Array.Empty<WorldEntityDraw>();
    Vector2 _lastTileFocus;
    NetEntity _lastGoodEyeMap;
    Vector2 _lastGoodWorldEye;
    readonly HashSet<NetEntity> _movedScratch = new();
    readonly HashSet<NetEntity> _ghostEntities = new();
    readonly Dictionary<NetEntity, AudioVisual> _audio = new();
    /// <summary>DoorComponent.State — Open/Closed/Opening/Closing (PC DoorSystem visualizer).</summary>
    readonly Dictionary<NetEntity, string> _doorStates = new();
    /// <summary>AppearanceComponent visual key → value (PC GenericVisualizer input).</summary>
    readonly Dictionary<NetEntity, Dictionary<string, string>> _appearance = new();
    /// <summary>Entities that already received a one-shot YAML visual compose (not every tick).</summary>
    readonly HashSet<NetEntity> _composedFromProto = new();
    readonly Dictionary<NetEntity, string> _composedProtoId = new();
    /// <summary>Cached IconSmooth YAML→meta StateBase remaps (proto|path → data). Log once.</summary>
    readonly Dictionary<string, IconSmoothData> _iconSmoothRemapCache = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _iconSmoothRemapLogged = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Last non-empty floor draw — keep across LeavePvs/warp until new chunks arrive.</summary>
    IReadOnlyList<WorldTileDraw> _lastTiles = Array.Empty<WorldTileDraw>();
    string? _contentRoot;
    Vector2 _lastEyeOffset;
    bool _lastDrawFov = true;
    long _lastDeserLogMs;
    string? _lastDeserKind;

    sealed class AudioVisual
    {
        public string FileName = "";
        public float VolumeDb;
        public float MaxDistance = 15f;
        public bool Global;
        public bool Loop;
        public bool Playing = true;
    }

    PrototypeSpriteIndex? _protos;
    TilePrototypeIndex? _tiles;
    bool _canReturnToBody = true;
    bool _canTakeGhostRoles = true;

    public int XformCount { get { lock (this) return _xforms.Count; } }
    public int SpriteCount { get { lock (this) return _sprites.Count; } }
    public int TileChunkCount { get { lock (this) return _grids.Sum(g => g.Value.Count); } }
    public int PrototypeHits { get; private set; }

    public bool TryGetControlledGhostFlags(out bool canReturnToBody, out bool canTakeGhostRoles)
    {
        lock (this)
        {
            canReturnToBody = _canReturnToBody;
            canTakeGhostRoles = _canTakeGhostRoles;
            return _lastControlled.IsValid();
        }
    }

    public bool TryGetWorldPos(NetEntity ent, out float x, out float y)
    {
        lock (this)
        {
            if (!ent.IsValid() || !_xforms.ContainsKey(ent))
            {
                x = 0;
                y = 0;
                return false;
            }

            var p = ResolveWorldPos(ent);
            x = p.X;
            y = p.Y;
            return true;
        }
    }

    public void RemoveEntities(IReadOnlyList<NetEntity> entities)
    {
        lock (this)
        {
            foreach (var del in entities)
            {
                _xforms.Remove(del);
                _sprites.Remove(del);
                _movers.Remove(del);
                _prototypes.Remove(del);
                _names.Remove(del);
                _grids.Remove(del);
                _gridChunkSize.Remove(del);
                _worldPosCache.Remove(del);
                _mapEntities.Remove(del);
                _closedContainers.Remove(del);
                _openContainers.Remove(del);
                _containerOccludes.Remove(del);
                _ghostEntities.Remove(del);
                _audio.Remove(del);
                _doorStates.Remove(del);
                _appearance.Remove(del);
                _composedFromProto.Remove(del);
                _composedProtoId.Remove(del);
                _mapUidCache.Remove(del);
            }

            // Once per batch — clearing inside the loop was O(n²) after big LeavePvs.
            if (entities.Count > 0)
            {
                _mapUidCache.Clear();
                // Force full draw rebuild after PVS leave — fast-path snapshot would keep ghosts.
                if (entities.Count >= 8 || _lastDrawSnapshot.Length == 0)
                    _lastDrawSnapshot = Array.Empty<WorldEntityDraw>();
                else
                    _lastDrawSnapshot = FilterDrawSnapshot(_lastDrawSnapshot, entities);
            }
        }
    }

    static WorldEntityDraw[] FilterDrawSnapshot(WorldEntityDraw[] prev, IReadOnlyList<NetEntity> leaving)
    {
        var set = new HashSet<NetEntity>(leaving);
        var n = 0;
        for (var i = 0; i < prev.Length; i++)
        {
            if (!set.Contains(prev[i].Entity))
                n++;
        }

        if (n == prev.Length)
            return prev;
        if (n == 0)
            return Array.Empty<WorldEntityDraw>();
        var arr = new WorldEntityDraw[n];
        var w = 0;
        for (var i = 0; i < prev.Length; i++)
        {
            if (!set.Contains(prev[i].Entity))
                arr[w++] = prev[i];
        }

        return arr;
    }

    /// <summary>Rebuild floor tiles near the controlled eye after LeavePvs (drop stale areas).</summary>
    public IReadOnlyList<WorldTileDraw> RebuildTilesNearEye(float viewTiles = 40f)
    {
        lock (this)
        {
            // Do NOT wipe _worldPosCache here — LeavePvs storms were O(store) re-resolve hitches.
            // Deleted ents already drop their cache entries in RemoveEntities.
            if (!_lastControlled.IsValid() || !_xforms.ContainsKey(_lastControlled))
                return _lastTiles; // keep prior floors until eye/grid data returns
            var focus = ResolveWorldPos(_lastControlled);
            var eyeMap = ResolveMapUid(_lastControlled);
            var tiles = BuildTileDrawList(focus, viewTiles, eyeMap);
            if (tiles.Count > 0)
                _lastTiles = tiles;
            return tiles.Count > 0 ? tiles : _lastTiles;
        }
    }

    public void SetPrototypeIndex(PrototypeSpriteIndex? index)
    {
        lock (this) _protos = index;
    }

    public void SetTileIndex(TilePrototypeIndex? index)
    {
        lock (this) _tiles = index;
    }

    public void SetContentRoot(string? contentFilesRoot)
    {
        lock (this)
        {
            _contentRoot = contentFilesRoot;
            _iconSmoothRemapCache.Clear();
            _iconSmoothRemapLogged.Clear();
        }
    }

    public void Clear()
    {
        lock (this)
        {
            _xforms.Clear();
            _sprites.Clear();
            _movers.Clear();
            _prototypes.Clear();
            _names.Clear();
            _grids.Clear();
            _gridChunkSize.Clear();
            _worldPosCache.Clear();
            _mapEntities.Clear();
            _mapUidCache.Clear();
            _closedContainers.Clear();
            _openContainers.Clear();
            _containerOccludes.Clear();
            _ghostEntities.Clear();
            _audio.Clear();
            _doorStates.Clear();
            _appearance.Clear();
            _composedFromProto.Clear();
            _composedProtoId.Clear();
            _iconSmoothRemapCache.Clear();
            _iconSmoothRemapLogged.Clear();
            _lastEyeOffset = default;
            _lastDrawFov = true;
            _lastControlled = default;
            _lastTiles = Array.Empty<WorldTileDraw>();
            _lastDrawSnapshot = Array.Empty<WorldEntityDraw>();
            _lastTileFocus = default;
            _lastGoodEyeMap = default;
            _lastGoodWorldEye = default;
            PrototypeHits = 0;
        }
    }

    NetEntity _lastControlled;
    public NetEntity LastControlled
    {
        get { lock (this) return _lastControlled; }
    }

    public bool Apply(
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
            GameState state;
            try
            {
                using var stream = new MemoryStream(payload, writable: false);
                serializer.DeserializeDirect(stream, out state);
            }
            catch (Exception deserEx)
            {
                var msg = string.IsNullOrWhiteSpace(deserEx.Message) ? "fail" : deserEx.Message;
                if (deserEx.InnerException is { } inner && !string.IsNullOrWhiteSpace(inner.Message))
                    msg += $" → {inner.GetType().Name}: {inner.Message}";
                var frame = deserEx.StackTrace?
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .FirstOrDefault(l => l.Contains("Robust", StringComparison.OrdinalIgnoreCase)
                                         || l.Contains("NetSerializer", StringComparison.OrdinalIgnoreCase)
                                         || l.Contains("Content.", StringComparison.OrdinalIgnoreCase));
                var detail =
                    $"DESER {deserEx.GetType().Name}: {msg}" +
                    $" (payload={payload.Length:N0}B storeXf={_xforms.Count})" +
                    (frame is null ? "" : $" @ {frame.Trim()}");
                // Rate-limit — unique payload sizes were flooding the ring and clipboard after warp.
                var now = Environment.TickCount64;
                var kind = deserEx.GetType().Name;
                // Same DESER kind after warp can fire dozens of times — keep ring usable.
                if (now - _lastDeserLogMs >= 5000 || !string.Equals(kind, _lastDeserKind, StringComparison.Ordinal))
                {
                    _lastDeserLogMs = now;
                    _lastDeserKind = kind;
                    Port.Content.DiagLog.Error($"Apply FAIL {detail}");
                }
                error = detail;
                return false;
            }

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
            var deletions = state.EntityDeletions.Value ?? Array.Empty<NetEntity>();
            var packetXforms = 0;
            var packetSprites = 0;
            var packetMeta = 0;
            var packetGrids = 0;

            lock (this)
            {
                if (state.FromSequence == GameTick.Zero)
                {
                    _xforms.Clear();
                    _sprites.Clear();
                    _movers.Clear();
                    _prototypes.Clear();
                    _names.Clear();
                    _grids.Clear();
                    _gridChunkSize.Clear();
                    _worldPosCache.Clear();
                    _mapEntities.Clear();
                    _mapUidCache.Clear();
                    _closedContainers.Clear();
                    _openContainers.Clear();
                    _containerOccludes.Clear();
                    _ghostEntities.Clear();
                    _audio.Clear();
                    _lastDrawSnapshot = Array.Empty<WorldEntityDraw>();
                    _lastTileFocus = default;
                    _lastGoodEyeMap = default;
                    _lastGoodWorldEye = default;
                    _lastEyeOffset = default;
                    _lastDrawFov = true;
                    _lastControlled = default;
                    PrototypeHits = 0;
                }

                foreach (var del in deletions)
                {
                    _xforms.Remove(del);
                    _sprites.Remove(del);
                    _movers.Remove(del);
                    _prototypes.Remove(del);
                    _names.Remove(del);
                    _grids.Remove(del);
                    _gridChunkSize.Remove(del);
                    _worldPosCache.Remove(del);
                    _mapEntities.Remove(del);
                    _closedContainers.Remove(del);
                    _openContainers.Remove(del);
                    _containerOccludes.Remove(del);
                    _ghostEntities.Remove(del);
                    _audio.Remove(del);
                }

                if (deletions.Count > 0)
                    _mapUidCache.Clear();

                var packetMaps = 0;
                var invalidatePos = false;
                _movedScratch.Clear();
                NetEntity ctrlScan = default;
                if (me?.ControlledEntity is { } cScan && cScan.IsValid())
                    ctrlScan = cScan;
                else if (_lastControlled.IsValid())
                    ctrlScan = _lastControlled;

                foreach (var es in entities)
                {
                    try
                    {
                        foreach (var change in es.ComponentChanges.Span)
                        {
                            if (change.State is null)
                                continue;

                            try
                            {
                                if (TryReadTransform(change.State, out var xf))
                                {
                                    // Legacy Leave-PVS signal: NaN local position.
                                    if (float.IsNaN(xf.LocalPosition.X) || float.IsNaN(xf.LocalPosition.Y))
                                    {
                                        _xforms.Remove(es.NetEntity);
                                        _sprites.Remove(es.NetEntity);
                                        _movers.Remove(es.NetEntity);
                                        _prototypes.Remove(es.NetEntity);
                                        _names.Remove(es.NetEntity);
                                        _grids.Remove(es.NetEntity);
                                        _gridChunkSize.Remove(es.NetEntity);
                                        _closedContainers.Remove(es.NetEntity);
                                        _openContainers.Remove(es.NetEntity);
                                        _containerOccludes.Remove(es.NetEntity);
                                        _ghostEntities.Remove(es.NetEntity);
                                        _audio.Remove(es.NetEntity);
                                        invalidatePos = true;
                                        continue;
                                    }

                                    _xforms[es.NetEntity] = xf;
                                    // Per-entity invalidate — full wipe every Δxf was O(store) re-resolve hitches.
                                    _worldPosCache.Remove(es.NetEntity);
                                    _mapUidCache.Remove(es.NetEntity);
                                    _movedScratch.Add(es.NetEntity);
                                    // Grid/map roots move whole stations — wipe descendant caches once.
                                    if (_grids.ContainsKey(es.NetEntity) || _mapEntities.Contains(es.NetEntity))
                                        invalidatePos = true;
                                    packetXforms++;
                                    continue;
                                }

                                if (change.State is MetaDataComponentState meta)
                                {
                                    packetMeta++;
                                    if (!string.IsNullOrWhiteSpace(meta.Name))
                                        _names[es.NetEntity] = meta.Name!;
                                    if (!string.IsNullOrWhiteSpace(meta.PrototypeId))
                                    {
                                        _prototypes[es.NetEntity] = meta.PrototypeId!;
                                        TryAttachPrototypeSprite(es.NetEntity, meta.PrototypeId!);
                                    }
                                    continue;
                                }

                                // MapComponent — marks MapUid roots so we can separate stacked maps.
                                if (change.State is MapComponentState
                                    || change.State.GetType().Name.Contains("MapComponentState", StringComparison.Ordinal))
                                {
                                    _mapEntities.Add(es.NetEntity);
                                    invalidatePos = true;
                                    packetMaps++;
                                    continue;
                                }

                                if (TryApplyGrid(es.NetEntity, change.State))
                                {
                                    packetGrids++;
                                    continue;
                                }

                                if (TryReadInputMover(change.State, out var mover))
                                {
                                    _movers[es.NetEntity] = mover;
                                    continue;
                                }

                                if (ctrlScan.IsValid() && es.NetEntity == ctrlScan)
                                    TryApplyGhostFlags(change.State);

                                if (IsGhostComponentState(change.State))
                                    _ghostEntities.Add(es.NetEntity);

                                if (TryApplyContainerState(es.NetEntity, change.State))
                                    continue;

                                if (TryExtractAudio(change.State, es.NetEntity))
                                    continue;

                                if (TryReadDoorState(change.State, out var doorState))
                                    _doorStates[es.NetEntity] = doorState;

                                if (AppearanceVisuals.TryExtract(change.State, out var appVisuals))
                                    _appearance[es.NetEntity] = appVisuals;

                                var before = _sprites.Count;
                                GameStateDecoder.TryExtractSpritePublic(change.State, es.NetEntity, _sprites);
                                if (_sprites.Count != before || _sprites.ContainsKey(es.NetEntity))
                                    packetSprites++;
                            }
                            catch (Exception compEx) when (
                                compEx is IndexOutOfRangeException or NullReferenceException
                                    or InvalidCastException or KeyNotFoundException)
                            {
                                // One bad component must not abort the rest of the entity / packet.
                                Port.Content.DiagLog.Warn(
                                    $"Apply skip component {change.State.GetType().Name} on {es.NetEntity}: {compEx.GetType().Name}: {compEx.Message}");
                            }
                        }
                    }
                    catch (Exception entEx) when (
                        entEx is IndexOutOfRangeException or NullReferenceException
                            or InvalidCastException or KeyNotFoundException)
                    {
                        Port.Content.DiagLog.Warn(
                            $"Apply skip entity {es.NetEntity}: {entEx.GetType().Name}: {entEx.Message}");
                    }
                }

                if (invalidatePos)
                {
                    _worldPosCache.Clear();
                    _mapUidCache.Clear();
                }

                // Late bind: attach YAML visuals ONCE per entity (PC netsync:false).
                // Re-running ForcePrototypeSprite every MsgState wiped door/storage/network deltas
                // and fought IconSmooth — see sprite pipeline audit.
                foreach (var (ent, proto) in _prototypes)
                {
                    if (!_sprites.TryGetValue(ent, out var spr))
                    {
                        TryAttachPrototypeSprite(ent, proto);
                        MarkComposed(ent, proto);
                        continue;
                    }

                    if (IsPlayerLike(proto, spr))
                    {
                        if (spr.Layers.Count == 0)
                            EnsurePrototypeLayers(ent, proto, spr);
                        else if (!spr.FromNetwork && string.IsNullOrEmpty(spr.Path))
                            TryAttachPrototypeSprite(ent, proto);
                        continue;
                    }

                    if (!NeedsPrototypeCompose(ent, proto, spr))
                    {
                        // Refresh Appearance / door overlays without wiping the YAML stack.
                        if (_appearance.ContainsKey(ent) || _doorStates.ContainsKey(ent))
                        {
                            AppearanceVisuals.ApplyToSprite(
                                spr, _appearance.GetValueOrDefault(ent), _doorStates.GetValueOrDefault(ent));
                            _sprites[ent] = spr;
                        }
                        continue;
                    }

                    ForcePrototypeSprite(ent, proto, spr);
                    MarkComposed(ent, proto);
                }

                NetEntity controlled = default;
                if (me?.ControlledEntity is { } c && c.IsValid())
                {
                    controlled = c;
                    _lastControlled = c;
                }
                else if (_lastControlled.IsValid() && _xforms.ContainsKey(_lastControlled))
                {
                    controlled = _lastControlled;
                }

                // Camera + tile cull MUST use world coords — LocalPosition is grid-relative.
                Vector2 worldEye = default;
                Angle rot = default;
                Vector2 eyeOff = _lastEyeOffset;
                var foundXform = false;
                var foundEye = false;
                var drawFov = _lastDrawFov;
                NetEntity eyeMap = default;

                if (controlled.IsValid() && _xforms.ContainsKey(controlled))
                {
                    worldEye = ResolveWorldPos(controlled);
                    rot = new Angle(ResolveWorldRot(controlled));
                    eyeMap = ResolveMapUid(controlled);
                    foundXform = true;
                    // After LeavePvs/DESER the parent chain can break → eyeMap invalid and
                    // worldEye collapses to 0. Reuse last good eye so we don't dump all maps.
                    if (!eyeMap.IsValid() && _lastGoodEyeMap.IsValid())
                        eyeMap = _lastGoodEyeMap;
                    var eyeOk = worldEye.LengthSquared() > 1f
                                || (_lastGoodWorldEye.LengthSquared() < 1f);
                    if (!eyeOk && _lastGoodWorldEye.LengthSquared() > 1f)
                        worldEye = _lastGoodWorldEye;
                    else if (eyeOk && eyeMap.IsValid())
                    {
                        _lastGoodEyeMap = eyeMap;
                        _lastGoodWorldEye = worldEye;
                    }
                }
                else if (_lastGoodWorldEye.LengthSquared() > 1f)
                {
                    // Controlled xform missing mid-warp — keep camera/tiles on last good station.
                    worldEye = _lastGoodWorldEye;
                    eyeMap = _lastGoodEyeMap;
                    foundXform = eyeMap.IsValid() || _lastGoodWorldEye.LengthSquared() > 1f;
                }

                if (controlled.IsValid())
                {
                    foreach (var es in entities)
                    {
                        if (es.NetEntity != controlled) continue;
                        foreach (var change in es.ComponentChanges.Span)
                        {
                            if (change.State is null) continue;
                            var tn = change.State.GetType().Name;
                            if (!tn.Contains("EyeComponent", StringComparison.Ordinal)) continue;
                            var t = change.State.GetType();
                            if (t.GetProperty("Offset")?.GetValue(change.State) is Vector2 vo)
                            {
                                eyeOff = vo;
                                foundEye = true;
                            }
                            else if (t.GetField("Offset")?.GetValue(change.State) is Vector2 vo2)
                            {
                                eyeOff = vo2;
                                foundEye = true;
                            }

                            var fov = t.GetProperty("DrawFov")?.GetValue(change.State)
                                      ?? t.GetField("DrawFov")?.GetValue(change.State);
                            if (fov is bool fb)
                            {
                                drawFov = fb;
                                foundEye = true;
                            }
                        }
                        break;
                    }
                }

                if (foundEye)
                {
                    _lastEyeOffset = eyeOff;
                    _lastDrawFov = drawFov;
                }

                // Entity store is already updated. Isolate draw rebuild so a single
                // IndexOutOfRange in IconSmooth/tiles cannot discard a whole MsgState.
                try
                {
                // Fast path (PC-like): tiny visual-static deltas only patch positions —
                // skip full IconSmooth occupancy + sort (was hitching every MsgState at draw~1200).
                const float viewTiles = 18f;
                var tilesSane = _lastTiles.Count > 0 && _lastTiles.Count <= MaxDrawTiles;
                // Never fast-path a ghost-only snapshot while the store still has a station —
                // that locked walls at draw=2 after LeavePvs storms.
                var drawSane = _lastDrawSnapshot.Length >= 24
                               || _xforms.Count < 80
                               || _lastDrawSnapshot.Length >= Math.Max(8, _sprites.Count / 4);
                if (state.FromSequence != GameTick.Zero
                    && packetSprites == 0 && packetMeta == 0 && packetGrids == 0 && packetMaps == 0
                    && entities.Count <= 32
                    && _lastDrawSnapshot.Length > 0
                    && drawSane
                    && foundXform
                    && eyeMap.IsValid()
                    && worldEye.LengthSquared() > 1f
                    && tilesSane
                    && !invalidatePos)
                {
                    var patched = PatchDrawSnapshot(_lastDrawSnapshot, controlled, worldEye);
                    _lastDrawSnapshot = patched;
                    var eyeMoved = MathF.Abs(worldEye.X - _lastTileFocus.X) > 2.5f
                                   || MathF.Abs(worldEye.Y - _lastTileFocus.Y) > 2.5f;
                    IReadOnlyList<WorldTileDraw> tilesFast = _lastTiles;
                    if (eyeMoved || _lastTiles.Count == 0)
                    {
                        tilesFast = BuildTileDrawList(worldEye, viewTiles, eyeMap);
                        if (tilesFast.Count > 0)
                        {
                            _lastTiles = tilesFast;
                            _lastTileFocus = worldEye;
                        }
                        else
                            tilesFast = _lastTiles;
                    }

                    var audioFast = BuildAudioCueList(worldEye, viewTiles, eyeMap);
                    var detailFast =
                        $"from={state.FromSequence.Value} ents={entities.Count} " +
                        $"Δxf={packetXforms} Δspr=0 Δmeta=0 Δgrid=0 Δmap=0 " +
                        $"store={_xforms.Count}/{_sprites.Count} maps={_mapEntities.Count} tiles={tilesFast.Count} " +
                        $"audio={audioFast.Count} draw={patched.Length} fast " +
                        $"eyeMap={(eyeMap.IsValid() ? eyeMap.ToString() : "-")} " +
                        $"ctrl={(controlled.IsValid() ? controlled.ToString() : "-")}";
                    eye = new EyeSnapshot(
                        controlled, worldEye, rot, eyeOff, drawFov,
                        entities.Count, players.Count, state.ToSequence, detailFast);
                    world = new WorldSnapshot(eye, patched, state.ToSequence, detailFast, tilesFast, audioFast);
                    error = detailFast;
                    return true;
                }

                // Drop poisoned tile caches from prior eyeMap=- all-maps dumps.
                if (_lastTiles.Count > MaxDrawTiles)
                    _lastTiles = Array.Empty<WorldTileDraw>();

                var drawList = _drawListScratch;
                drawList.Clear();
                if (drawList.Capacity < Math.Min(_xforms.Count * 2, 20_000))
                    drawList.Capacity = Math.Min(_xforms.Count * 2, 20_000);
                Vector2 sum = default;
                var nSum = 0;
                // Viewport + modest margin only — far entities stay in store but don't
                // compete for RSI loads (was 56 tiles → texture thrash, walls missing).
                var viewR2 = viewTiles * viewTiles;

                // IconSmooth occupancy: parent/grid-local lookup matching PC Snapgrid.
                // Cull to eye AABB (+margin) — full-map scans at store~1700 hitch the net thread.
                var smoothTiles = _smoothTilesScratch;
                smoothTiles.Clear();
                var smoothByEnt = _smoothByEntScratch;
                smoothByEnt.Clear();
                const float smoothMargin = 4f;
                var smoothR = viewTiles + smoothMargin;
                var smoothR2 = smoothR * smoothR;
                foreach (var (ent, xf0) in _xforms)
                {
                    if (!xf0.ParentID.IsValid() || _grids.ContainsKey(ent))
                        continue;
                    if (eyeMap.IsValid())
                    {
                        var entMap0 = ResolveMapUid(ent);
                        if (entMap0.IsValid() && entMap0 != eyeMap)
                            continue;
                    }

                    var wp0 = ResolveWorldPos(ent);
                    if (foundXform)
                    {
                        var dx0 = wp0.X - worldEye.X;
                        var dy0 = wp0.Y - worldEye.Y;
                        if (dx0 * dx0 + dy0 * dy0 > smoothR2)
                            continue;
                    }

                    _prototypes.TryGetValue(ent, out var proto0);
                    _sprites.TryGetValue(ent, out var spr0);
                    var path0 = ResolveSpritePath(spr0, proto0);
                    if (!TryResolveIconSmooth(proto0, path0, out var smooth0))
                        continue;

                    var tx0 = (int)MathF.Floor(xf0.LocalPosition.X);
                    var ty0 = (int)MathF.Floor(xf0.LocalPosition.Y);
                    var parentKey = xf0.ParentID.Id;
                    // Occupancy is always by the entity's own SmoothKey (PC MatchingEntity).
                    smoothTiles.Add((parentKey, tx0, ty0, smooth0.Key));

                    // NoSprite only contributes to neighbors — never draws its own smooth.
                    if (smooth0.Mode == IconSmoothMode.NoSprite || string.IsNullOrEmpty(path0))
                        continue;

                    var depth0 = spr0 is { FromNetwork: true, HasDrawDepth: true }
                        ? spr0.DrawDepth
                        : ClassifyDepth(path0, spr0?.DrawDepth ?? GameStateDecoder.GuessDepth(path0), proto0);
                    smoothByEnt[ent] = (smooth0, path0!, depth0, parentKey, tx0, ty0);
                }

                foreach (var (ent, xf) in _xforms)
                {
                    if (_grids.ContainsKey(ent) && !_sprites.ContainsKey(ent) && !_prototypes.ContainsKey(ent))
                        continue; // map-grid entity without sprite — tiles drawn separately

                    var isCtrl = controlled.IsValid() && ent == controlled;
                    // Critical: only the eye's map — otherwise stations/asteroids stack in one space.
                    if (!isCtrl && eyeMap.IsValid())
                    {
                        var entMap = ResolveMapUid(ent);
                        if (entMap.IsValid() && entMap != eyeMap)
                            continue;
                    }

                    // Container / inventory occlusion — never draw item piles on the mob.
                    if (!isCtrl && (IsHiddenByContainer(ent, xf) || IsHiddenInInventoryOrEquippedSlot(ent, xf)))
                        continue;

                    var wp = ResolveWorldPos(ent);
                    if (!isCtrl && foundXform)
                    {
                        var dx = wp.X - worldEye.X;
                        var dy = wp.Y - worldEye.Y;
                        if (dx * dx + dy * dy > viewR2)
                            continue;
                    }

                    _sprites.TryGetValue(ent, out var spr);
                    if (spr is { Visible: false })
                        continue;

                    var worldRot = (float)ResolveWorldRot(ent);
                    _prototypes.TryGetValue(ent, out var protoId);

                    // Pure networked AudioComponent ents have no sprite — skip markers.
                    if (!isCtrl && _audio.ContainsKey(ent))
                    {
                        var hasArt = (spr is not null
                                      && (!string.IsNullOrEmpty(spr.Path) || spr.Layers is { Count: > 0 }))
                                     || (!string.IsNullOrEmpty(protoId)
                                         && !string.IsNullOrEmpty(_protos?.TryGetSprite(protoId)));
                        if (!hasArt)
                            continue;
                    }

                    var isGhost = _ghostEntities.Contains(ent)
                                  || (protoId?.Contains("Ghost", StringComparison.OrdinalIgnoreCase) ?? false)
                                  || (protoId?.Contains("Observer", StringComparison.OrdinalIgnoreCase) ?? false);

                    if (!isCtrl && IsHiddenFromDefaultEye(protoId, spr?.Path, spr?.DrawDepth ?? 0, spr?.FromNetwork == true))
                        continue;

                    // IconSmooth walls/windows — neighbor bitmask → RSI states (UV is one cell).
                    if (!isCtrl && smoothByEnt.TryGetValue(ent, out var smoothEnt))
                    {
                        var path = smoothEnt.Path;
                        byte r = 255, g = 255, b = 255;
                        if (spr is { HasColor: true })
                        {
                            r = spr.R;
                            g = spr.G;
                            b = spr.B;
                        }
                        else if (spr is not null && (spr.R != 0 || spr.G != 0 || spr.B != 0))
                        {
                            r = spr.R;
                            g = spr.G;
                            b = spr.B;
                        }

                        var depth = smoothEnt.Depth;
                        var tx = smoothEnt.Tx;
                        var ty = smoothEnt.Ty;
                        var parent = smoothEnt.Parent;
                        var bas = smoothEnt.Data.StateBase;
                        // PC MatchingEntity: neighbor.SmoothKey == us.Key || us.AdditionalKeys.contains(neighbor.Key)
                        bool Has(int ox, int oy)
                        {
                            if (smoothTiles.Contains((parent, tx + ox, ty + oy, smoothEnt.Data.Key)))
                                return true;
                            var extra = smoothEnt.Data.AdditionalKeys;
                            if (extra is null) return false;
                            for (var i = 0; i < extra.Length; i++)
                            {
                                if (smoothTiles.Contains((parent, tx + ox, ty + oy, extra[i])))
                                    return true;
                            }
                            return false;
                        }

                        if (smoothEnt.Data.Mode == IconSmoothMode.CardinalFlags)
                        {
                            var flags = 0;
                            if (Has(0, 1)) flags |= 1;
                            if (Has(0, -1)) flags |= 2;
                            if (Has(1, 0)) flags |= 4;
                            if (Has(-1, 0)) flags |= 8;
                            drawList.Add(new WorldEntityDraw(
                                ent, wp.X, wp.Y, worldRot, path, r, g, b, false, depth,
                                $"{bas}{flags}", true, 0, 0, true, null, -1, isGhost));
                        }
                        else if (smoothEnt.Data.Mode == IconSmoothMode.Diagonal)
                        {
                            // PC CalculateNewSpriteDiagonal: neighbor offsets rotated by LocalRotation.
                            var diagRot = (float)xf.Rotation.Theta;
                            var cosA = MathF.Cos(diagRot);
                            var sinA = MathF.Sin(diagRot);
                            bool HasRotated(float lx, float ly)
                            {
                                var rx = lx * cosA - ly * sinA;
                                var ry = lx * sinA + ly * cosA;
                                return Has((int)MathF.Round(rx), (int)MathF.Round(ry));
                            }
                            var d = HasRotated(1, 0) && HasRotated(1, -1) && HasRotated(0, -1) ? 1 : 0;
                            drawList.Add(new WorldEntityDraw(
                                ent, wp.X, wp.Y, worldRot, path, r, g, b, false, depth,
                                $"{bas}{d}", true, 0, 0, true, null, -1, isGhost));
                        }
                        else
                        {
                            var n = Has(0, 1);
                            var ne = Has(1, 1);
                            var e = Has(1, 0);
                            var se = Has(1, -1);
                            var south = Has(0, -1);
                            var sw = Has(-1, -1);
                            var w = Has(-1, 0);
                            var nw = Has(-1, 1);

                            // CornerFill bits match Content.Client IconSmoothSystem (Baystation12).
                            byte cNE = 0, cNW = 0, cSW = 0, cSE = 0;
                            if (n) { cNE |= 1; cNW |= 4; }
                            if (ne) cNE |= 2;
                            if (e) { cNE |= 4; cSE |= 1; }
                            if (se) cSE |= 2;
                            if (south) { cSE |= 4; cSW |= 1; }
                            if (sw) cSW |= 2;
                            if (w) { cSW |= 4; cNW |= 1; }
                            if (nw) cNW |= 2;

                            // Remap fills by local cardinal facing (same switch as PC CalculateCornerFill).
                            var cornerLocalRot = (float)xf.Rotation.Theta;
                            RemapIconSmoothCorners(cornerLocalRot, ref cNE, ref cNW, ref cSW, ref cSE);

                            // RSI dir indices with base = South (Angle 0 → South on PC):
                            // SE=None→S=0, NE=CCW→E=2, NW=Flip→N=1, SW=CW→W=3.
                            void AddCorner(byte fill, int dir) =>
                                drawList.Add(new WorldEntityDraw(
                                    ent, wp.X, wp.Y, worldRot, path, r, g, b, false, depth,
                                    $"{bas}{fill}", true, 0, 0, true, null, dir, isGhost));

                            AddCorner(cSE, 0);
                            AddCorner(cNE, 2);
                            AddCorner(cNW, 1);
                            AddCorner(cSW, 3);
                        }

                        sum += wp;
                        nSum++;
                        continue;
                    }

                    // Empty layer stack → expand full YAML layers (computers: base+keyboard+screen).
                    if ((spr?.Layers.Count ?? 0) == 0 && !string.IsNullOrEmpty(protoId))
                    {
                        EnsurePrototypeLayers(ent, protoId!, spr);
                        _sprites.TryGetValue(ent, out spr);
                    }

                    var layersAdded = 0;
                    if (spr?.Layers is { Count: > 0 })
                    {
                        // Preserve the complete authoritative Sprite layer stack.
                        var baseDepth = spr.FromNetwork && spr.HasDrawDepth
                            ? spr.DrawDepth
                            : ClassifyDepth(spr.Path, spr.DrawDepth != 0 ? spr.DrawDepth : GameStateDecoder.GuessDepth(spr.Path), protoId);

                        foreach (var layer in spr.Layers)
                        {
                            if (!layer.Visible) continue;
                            var path = layer.Path
                                       ?? (spr.FromNetwork ? spr.Path : null)
                                       ?? spr.Path;
                            if (string.IsNullOrEmpty(path))
                            {
                                if (isCtrl)
                                    path = "Mobs/Ghosts/ghost_human.rsi";
                                else
                                    continue;
                            }

                            var layerDepth = layer.HasDepth ? layer.Depth : baseDepth;
                            if (!isCtrl && IsHiddenFromDefaultEye(protoId, path, layerDepth, spr.FromNetwork))
                                continue;

                            byte lr = layer.R, lg = layer.G, lb = layer.B;
                            if (!spr.HasColor && lr == 0 && lg == 0 && lb == 0)
                            {
                                lr = 255;
                                lg = 255;
                                lb = 255;
                            }

                            var layerState = layer.State ?? spr.State;
                            // PC DoorSystem: base layer follows DoorComponent.State (closed/open).
                            if (_doorStates.TryGetValue(ent, out var doorSt)
                                && IsDoorBaseLayerState(layerState))
                                layerState = DoorVisualState(doorSt);
                            // Never draw without an explicit RSI state — null Sample substitutes wrong cells.
                            if (string.IsNullOrWhiteSpace(layerState) && !isCtrl)
                                continue;
                            // Layer offset is in local entity space → world via rotation.
                            var ox = layer.OffsetX;
                            var oy = layer.OffsetY;
                            var cos = MathF.Cos(worldRot);
                            var sin = MathF.Sin(worldRot);
                            var wxL = wp.X + ox * cos - oy * sin;
                            var wyL = wp.Y + ox * sin + oy * cos;
                            // Stable stack order within same DrawDepth.
                            var sortDepth = layerDepth * 16 + layersAdded;
                            var forceDir0 = ForceSpriteDir0(path, protoId, spr.SnapCardinals, spr.NoRotation);
                            var dirOv = forceDir0 ? 0 : -1;
                            drawList.Add(new WorldEntityDraw(
                                ent, wxL, wyL, worldRot,
                                path, lr, lg, lb, isCtrl, sortDepth, layerState,
                                true, 0, 0, spr.NoRotation || isCtrl || forceDir0, null, dirOv, isGhost,
                                layer.ScaleX, layer.ScaleY, layer.RotationOffset, forceDir0));
                            layersAdded++;
                        }
                    }

                    if (layersAdded == 0)
                    {
                        byte r = spr?.R ?? 0, g = spr?.G ?? 0, b = spr?.B ?? 0;
                        var path = spr?.Path;
                        var stateName = spr?.State;
                        if (string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(protoId))
                            path = _protos?.TryGetSprite(protoId);
                        if (string.IsNullOrEmpty(stateName) && !string.IsNullOrEmpty(protoId))
                            stateName = _protos?.TryGetState(protoId);

                        if (isCtrl)
                        {
                            path = "Mobs/Ghosts/ghost_human.rsi";
                            stateName = "animated";
                            r = 255;
                            g = 248;
                            b = 240;
                            isGhost = true;
                        }

                        // Skip unknown furniture/items rather than drawing the wrong RSI cell.
                        if (!isCtrl && (string.IsNullOrEmpty(path) || string.IsNullOrWhiteSpace(stateName)))
                            continue;

                        if (spr is not { HasColor: true } && r == 0 && g == 0 && b == 0)
                        {
                            if (isCtrl) { r = 255; g = 248; b = 240; }
                            else { r = 255; g = 255; b = 255; }
                        }

                        var depth = isCtrl
                            ? 100
                            : (spr is { FromNetwork: true, HasDrawDepth: true }
                                ? spr.DrawDepth
                                : ClassifyDepth(path, spr?.DrawDepth ?? GameStateDecoder.GuessDepth(path), protoId));
                        var snap = ForceSpriteDir0(path, protoId, spr?.SnapCardinals == true, spr?.NoRotation == true);
                        drawList.Add(new WorldEntityDraw(
                            ent, wp.X, wp.Y, worldRot,
                            path, r, g, b, isCtrl, depth, stateName,
                            true, 0, 0, spr?.NoRotation == true || isCtrl || snap
                                || (protoId?.Contains("Observer", StringComparison.OrdinalIgnoreCase) ?? false)
                                || (protoId?.Contains("Ghost", StringComparison.OrdinalIgnoreCase) ?? false),
                            null, snap ? 0 : -1, isGhost, 1f, 1f, 0f, snap));
                    }

                    sum += wp;
                    nSum++;

                    // Nameplates (PC Identity/MetaData) above mobs / controlled.
                    if (_names.TryGetValue(ent, out var label) && !string.IsNullOrWhiteSpace(label))
                    {
                        var showName = isCtrl
                                       || IsPlayerLike(protoId, spr)
                                       || layersAdded > 0 && spr is { DrawDepth: >= 0 };
                        if (showName)
                        {
                            drawList.Add(new WorldEntityDraw(
                                ent, wp.X, wp.Y, 0,
                                null, 255, 255, 255, isCtrl, 200, null,
                                true, 0, 0, true, label, -1, isGhost));
                        }
                    }
                }

                // Guarantee a visible ghost at the eye even if ControlledEntity sprite failed.
                if (foundXform)
                {
                    var hasGhostSprite = false;
                    foreach (var d in drawList)
                    {
                        if (!d.IsControlled) continue;
                        if (!string.IsNullOrEmpty(d.RsiPath)
                            && d.RsiPath.Contains("Ghost", StringComparison.OrdinalIgnoreCase))
                        {
                            hasGhostSprite = true;
                            break;
                        }

                        if (!string.IsNullOrEmpty(d.RsiPath) && d.StateName == "animated")
                        {
                            hasGhostSprite = true;
                            break;
                        }
                    }

                    if (!hasGhostSprite)
                    {
                        var ghostPath = _protos?.TryGetSprite("MobObserver")
                                        ?? _protos?.TryGetSprite("MobObserverBase")
                                        ?? _protos?.TryGetSprite("MobGhost")
                                        ?? "Mobs/Ghosts/ghost_human.rsi";
                        var ghostState = _protos?.TryGetState("MobObserver")
                                         ?? _protos?.TryGetState("MobObserverBase")
                                         ?? "animated";
                        // Always draw own ghost at eye — highest depth so it never culls away.
                        drawList.Add(new WorldEntityDraw(
                            controlled.IsValid() ? controlled : default,
                            worldEye.X, worldEye.Y, 0f,
                            ghostPath, 255, 248, 240, true, 500, ghostState,
                            true, 0, 0, true, null, -1, true));
                    }
                }

                if (!foundXform && nSum > 0)
                    worldEye = sum / nSum;

                var focus = worldEye;
                // Stable depth sort preserves multi-layer clothing order.
                drawList.Sort((a, b) =>
                {
                    var c = a.DrawDepth.CompareTo(b.DrawDepth);
                    if (c != 0) return c;
                    c = a.Y.CompareTo(b.Y);
                    if (c != 0) return c;
                    return a.Entity.Id.CompareTo(b.Entity.Id);
                });

                IReadOnlyList<WorldTileDraw> tiles = BuildTileDrawList(focus, viewTiles, eyeMap);
                if (tiles.Count > 0)
                    _lastTiles = tiles;
                else if (_lastTiles.Count > 0 && _lastTiles.Count <= MaxDrawTiles && eyeMap.IsValid())
                    tiles = _lastTiles;
                else
                {
                    _lastTiles = Array.Empty<WorldTileDraw>();
                    tiles = _lastTiles;
                }
                var audio = BuildAudioCueList(focus, viewTiles, eyeMap);

                var detail =
                    $"from={state.FromSequence.Value} ents={entities.Count} " +
                    $"Δxf={packetXforms} Δspr={packetSprites} Δmeta={packetMeta} Δgrid={packetGrids} Δmap={packetMaps} " +
                    $"store={_xforms.Count}/{_sprites.Count} maps={_mapEntities.Count} tiles={tiles.Count} " +
                    $"audio={audio.Count} draw={drawList.Count} " +
                    $"eyeMap={(eyeMap.IsValid() ? eyeMap.ToString() : "-")} " +
                    $"ctrl={(controlled.IsValid() ? controlled.ToString() : "-")}";

                eye = new EyeSnapshot(
                    controlled, worldEye, rot, eyeOff, drawFov,
                    entities.Count, players.Count, state.ToSequence, detail);
                var drawArr = drawList.ToArray();
                _lastDrawSnapshot = drawArr;
                _lastTileFocus = focus;
                world = new WorldSnapshot(
                    eye,
                    drawArr,
                    state.ToSequence,
                    detail,
                    tiles,
                    audio);
                error = detail;
                return drawList.Count > 0 || tiles.Count > 0 || audio.Count > 0
                       || foundXform || foundEye || entities.Count > 0;
                }
                catch (Exception drawEx)
                {
                    Port.Content.DiagLog.Error(
                        $"draw rebuild FAIL {drawEx.GetType().Name}: {drawEx.Message}\n{drawEx.StackTrace}");
                    // Store applied — emit tiles-only snapshot so warp/PVS does not freeze on last area.
                    var focus = worldEye;
                    IReadOnlyList<WorldTileDraw> tilesSafe = Array.Empty<WorldTileDraw>();
                    try { tilesSafe = BuildTileDrawList(focus, 40f, eyeMap); }
                    catch { /* ignore */ }
                    if (tilesSafe.Count > 0)
                        _lastTiles = tilesSafe;
                    else
                        tilesSafe = _lastTiles;
                    var detailDraw =
                        $"DRAW_FAIL {drawEx.GetType().Name}: {drawEx.Message} " +
                        $"store={_xforms.Count}/{_sprites.Count} tiles={tilesSafe.Count}";
                    eye = new EyeSnapshot(
                        controlled, worldEye, rot, eyeOff, drawFov,
                        entities.Count, players.Count, state.ToSequence, detailDraw);
                    world = new WorldSnapshot(
                        eye, Array.Empty<WorldEntityDraw>(), state.ToSequence, detailDraw, tilesSafe,
                        Array.Empty<WorldAudioCue>());
                    error = detailDraw;
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            var detail = ex.GetType().Name + ": " + (string.IsNullOrWhiteSpace(ex.Message) ? "GenericInvalidData" : ex.Message);
            for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
                detail += " → " + inner.GetType().Name + ": " + inner.Message;
            // Help distinguish type-map mismatch vs truncated payload.
            if (ex is InvalidDataException or EndOfStreamException or NullReferenceException
                or IndexOutOfRangeException or KeyNotFoundException)
            {
                detail += $" (payload={payload.Length:N0}B storeXf={_xforms.Count})";
            }

            Port.Content.DiagLog.Error($"Apply FAIL {detail}");
            error = detail;
            return false;
        }
    }

    void MarkComposed(NetEntity ent, string prototypeId)
    {
        _composedFromProto.Add(ent);
        _composedProtoId[ent] = prototypeId;
    }

    bool NeedsPrototypeCompose(NetEntity ent, string prototypeId, GameStateDecoder.SpriteVisual spr)
    {
        if (!_composedFromProto.Contains(ent))
            return true;
        if (_composedProtoId.TryGetValue(ent, out var prev) &&
            !string.Equals(prev, prototypeId, StringComparison.OrdinalIgnoreCase))
            return true;

        var path = spr.Path ?? ResolveSpritePath(spr, prototypeId);
        // Drawable IconSmooth (walls) is path-only. NoSprite (airlocks) keeps YAML layers.
        if (TryResolveIconSmooth(prototypeId, path, out var smooth))
        {
            if (IsDrawableIconSmooth(smooth))
            {
                if (spr.Layers.Count > 0 || string.IsNullOrEmpty(path))
                    return true;
                return false;
            }

            // Recover airlocks wrongly composed as path-only before NoSprite gate.
            var resolved = _protos?.TryGetResolvedSprite(prototypeId);
            if (resolved is { Layers.Count: > 0 } && spr.Layers.Count == 0)
                return true;
        }

        // Network wiped door/airlock YAML states → recompose from prototype.
        if (LooksLikeDoorOrAirlock(prototypeId, path) && spr.Layers.Count > 0)
        {
            var anyState = false;
            foreach (var layer in spr.Layers)
            {
                if (!string.IsNullOrWhiteSpace(layer.State))
                {
                    anyState = true;
                    break;
                }
            }
            if (!anyState)
                return true;
        }

        // Non-smooth structure with no drawable art yet.
        return spr.Layers.Count == 0 && string.IsNullOrEmpty(spr.Path) && string.IsNullOrEmpty(spr.State);
    }

    static bool LooksLikeDoorOrAirlock(string? proto, string? path)
    {
        var p = (proto ?? "") + " " + (path ?? "");
        return p.Contains("Airlock", StringComparison.OrdinalIgnoreCase)
               || p.Contains("Firelock", StringComparison.OrdinalIgnoreCase)
               || p.Contains("/Doors/", StringComparison.OrdinalIgnoreCase)
               || p.Contains("Windoor", StringComparison.OrdinalIgnoreCase)
               || p.Contains("Hatch", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Walls/windows draw via IconSmooth sheets. NoSprite (airlocks) only contributes neighbors
    /// and must keep YAML Sprite layers.
    /// </summary>
    public static bool IsDrawableIconSmooth(IconSmoothData data) =>
        data.Mode is not IconSmoothMode.NoSprite;

    void TryAttachPrototypeSprite(NetEntity ent, string prototypeId)
    {
        if (_sprites.TryGetValue(ent, out var existing) && existing.FromNetwork && IsPlayerLike(prototypeId, existing))
        {
            if (existing.Layers.Count == 0)
                EnsurePrototypeLayers(ent, prototypeId, existing);
            return;
        }

        ForcePrototypeSprite(ent, prototypeId, _sprites.TryGetValue(ent, out var prev) ? prev : null);
        MarkComposed(ent, prototypeId);
    }

    /// <summary>
    /// Rebuild sprite from YAML for furniture/machines/doors. Keeps network color/visibility.
    /// IconSmooth walls are path-only — never inject full/icon/single-state layers.
    /// </summary>
    void ForcePrototypeSprite(NetEntity ent, string prototypeId, GameStateDecoder.SpriteVisual? existing)
    {
        var resolved = _protos?.TryGetResolvedSprite(prototypeId);
        var path = resolved?.Path ?? existing?.Path ?? _protos?.TryGetSprite(prototypeId);
        // Walls/windows: path-only for drawable IconSmooth. Airlock NoSprite keeps layers.
        if (TryResolveIconSmooth(prototypeId, path, out var smooth) && IsDrawableIconSmooth(smooth))
        {
            var visual = existing ?? new GameStateDecoder.SpriteVisual { FromNetwork = false };
            visual.Path = path;
            visual.State = null;
            visual.Layers.Clear();
            visual.NoRotation = true;
            visual.SnapCardinals = resolved?.SnapCardinals == true;
            if (resolved?.DrawDepth is { } dd)
            {
                visual.DrawDepth = dd;
                visual.HasDrawDepth = true;
            }
            else
            {
                visual.DrawDepth = ClassifyDepth(path, GameStateDecoder.GuessDepth(path), prototypeId);
                visual.HasDrawDepth = true;
            }

            _sprites[ent] = visual;
            PrototypeHits++;
            return;
        }

        if (resolved is null)
        {
            if (existing is not null && existing.Layers.Count == 0)
                EnsurePrototypeLayers(ent, prototypeId, existing);
            return;
        }

        var rebuilt = existing ?? new GameStateDecoder.SpriteVisual { FromNetwork = false };
        // Authoritative path/state from prototype — stop sticky wrong network states.
        rebuilt.Path = resolved.Path ?? rebuilt.Path;
        rebuilt.State = resolved.State ?? rebuilt.State;
        if (resolved.DrawDepth is { } depth)
        {
            rebuilt.DrawDepth = depth;
            rebuilt.HasDrawDepth = true;
        }
        else if (!rebuilt.HasDrawDepth)
        {
            rebuilt.DrawDepth = ClassifyDepth(rebuilt.Path, GameStateDecoder.GuessDepth(rebuilt.Path), prototypeId);
        }

        rebuilt.NoRotation = resolved.NoRotation || rebuilt.NoRotation;
        rebuilt.SnapCardinals = resolved.SnapCardinals || rebuilt.SnapCardinals;
        rebuilt.Layers.Clear();
        AppendResolvedLayers(rebuilt, resolved);
        ApplyStorageVisuals(rebuilt, prototypeId, ent);
        AppearanceVisuals.ApplyToSprite(rebuilt, _appearance.GetValueOrDefault(ent), _doorStates.GetValueOrDefault(ent));

        if (rebuilt.Layers.Count == 0
            && !string.IsNullOrEmpty(resolved.Path)
            && !string.IsNullOrEmpty(resolved.State)
            && !IsEditorOnlySpriteState(resolved.State)
            && !(TryResolveIconSmooth(prototypeId, resolved.Path, out var sm2) && IsDrawableIconSmooth(sm2)))
        {
            rebuilt.Layers.Add(new GameStateDecoder.LayerVis(
                resolved.Path, resolved.State, rebuilt.DrawDepth, true,
                255, 255, 255, 0, 0, rebuilt.HasDrawDepth));
        }

        _sprites[ent] = rebuilt;
        PrototypeHits++;
    }

    /// <summary>
    /// Fill empty SpriteVisual.Layers from YAML while preserving network Path/State/Color/Depth.
    /// </summary>
    void EnsurePrototypeLayers(NetEntity ent, string prototypeId, GameStateDecoder.SpriteVisual? existing)
    {
        var resolved = _protos?.TryGetResolvedSprite(prototypeId);
        if (resolved is null)
            return;

        var visual = existing ?? new GameStateDecoder.SpriteVisual { FromNetwork = false };
        if (string.IsNullOrEmpty(visual.Path))
            visual.Path = resolved.Path;
        if (string.IsNullOrEmpty(visual.State))
            visual.State = resolved.State;
        if (!visual.HasDrawDepth && resolved.DrawDepth is { } dd)
        {
            visual.DrawDepth = dd;
            visual.HasDrawDepth = true;
        }
        visual.NoRotation = visual.NoRotation || resolved.NoRotation;

        if (visual.Layers.Count == 0)
        {
            if (resolved.Layers.Count > 0)
            {
                AppendResolvedLayers(visual, resolved);
                ApplyStorageVisuals(visual, prototypeId, ent);
            }
            else if (!string.IsNullOrEmpty(resolved.Path)
                     && !string.IsNullOrEmpty(resolved.State)
                     && !IsEditorOnlySpriteState(resolved.State)
                     && !(TryResolveIconSmooth(prototypeId, resolved.Path, out var sm)
                          && IsDrawableIconSmooth(sm)))
            {
                // Single-state Sprite. Never inject layers for drawable IconSmooth walls.
                visual.Layers.Add(new GameStateDecoder.LayerVis(
                    resolved.Path, resolved.State, visual.DrawDepth, true,
                    255, 255, 255, 0, 0, visual.HasDrawDepth));
            }
        }

        visual.SnapCardinals = visual.SnapCardinals || resolved.SnapCardinals;
        AppearanceVisuals.ApplyToSprite(visual, _appearance.GetValueOrDefault(ent), _doorStates.GetValueOrDefault(ent));
        _sprites[ent] = visual;
        PrototypeHits++;
    }

    void ApplyStorageVisuals(GameStateDecoder.SpriteVisual visual, string prototypeId, NetEntity ent)
    {
        var storage = _protos?.TryGetStorageVisuals(prototypeId);
        if (storage is null || visual.Layers.Count == 0)
            return;

        var open = _openContainers.Contains(ent)
                   || (_containerOccludes.TryGetValue(ent, out var occludes) && !occludes);
        for (var i = 0; i < visual.Layers.Count; i++)
        {
            var layer = visual.Layers[i];
            var map = layer.MapKey ?? "";
            var state = layer.State;
            if (map.Contains("StorageVisualLayers.Base", StringComparison.OrdinalIgnoreCase))
            {
                state = open
                    ? storage.Value.StateBaseOpen ?? storage.Value.StateBaseClosed ?? state
                    : storage.Value.StateBaseClosed ?? state;
            }
            else if (map.Contains("StorageVisualLayers.Door", StringComparison.OrdinalIgnoreCase))
            {
                state = open
                    ? storage.Value.StateDoorOpen ?? state
                    : storage.Value.StateDoorClosed ?? state;
            }
            else if (map.Length == 0 && i == 0 && storage.Value.StateBaseClosed is not null)
            {
                // ClosetBase layout without map keys: layer0 body, layer1 door.
                state = open
                    ? storage.Value.StateBaseOpen ?? storage.Value.StateBaseClosed
                    : storage.Value.StateBaseClosed;
            }
            else if (map.Length == 0 && i == 1 && storage.Value.StateDoorClosed is not null)
            {
                state = open
                    ? storage.Value.StateDoorOpen ?? storage.Value.StateDoorClosed
                    : storage.Value.StateDoorClosed;
            }

            if (!string.Equals(state, layer.State, StringComparison.Ordinal))
                visual.Layers[i] = layer with { State = state };
        }
    }

    static void AppendResolvedLayers(
        GameStateDecoder.SpriteVisual visual,
        PrototypeSpriteIndex.ResolvedSprite resolved)
    {
        foreach (var layer in resolved.Layers)
        {
            var state = layer.State ?? resolved.State;
            if (string.IsNullOrWhiteSpace(state))
                continue;
            visual.Layers.Add(new GameStateDecoder.LayerVis(
                layer.Path ?? resolved.Path,
                state,
                visual.DrawDepth,
                layer.Visible,
                layer.R,
                layer.G,
                layer.B,
                layer.OffsetX,
                layer.OffsetY,
                resolved.DrawDepth is not null,
                layer.ScaleX,
                layer.ScaleY,
                layer.Rotation,
                layer.MapKey));
        }
    }

    static int ClassifyDepth(string? path, int fallback, string? proto)
    {
        var p = (path ?? "") + " " + (proto ?? "");
        // Match PC Content.Shared.DrawDepth relative order (Default≈0).
        if (p.Contains("Tiles", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Floor", StringComparison.OrdinalIgnoreCase)
            || p.Contains("plating", StringComparison.OrdinalIgnoreCase))
            return -12;
        if (p.Contains("/Walls/", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Wall", StringComparison.OrdinalIgnoreCase)
                && !p.Contains("Window", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Grille", StringComparison.OrdinalIgnoreCase))
            return -2; // Walls
        if (p.Contains("Window", StringComparison.OrdinalIgnoreCase)
            || p.Contains("/Windows/", StringComparison.OrdinalIgnoreCase))
            return -1; // WallTops
        if (p.Contains("Airlock", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Door", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Windoor", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Firelock", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Shutter", StringComparison.OrdinalIgnoreCase))
            return 1;
        if (IsPlayerLike(proto, path))
            return 4; // Mobs
        if (p.Contains("Ghost", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Observer", StringComparison.OrdinalIgnoreCase))
            return 5;
        return fallback != 0 ? fallback : 0;
    }

    static bool IsPlayerLike(string? proto, GameStateDecoder.SpriteVisual? spr)
        => IsPlayerLike(proto, spr?.Path);

    static bool IsPlayerLike(string? proto, string? path)
    {
        var p = (proto ?? "") + " " + (path ?? "");
        // Never treat spawn markers as "mobs" (MobSpawner etc. would skip YAML repair + hide).
        if (p.Contains("Spawner", StringComparison.OrdinalIgnoreCase)
            || p.Contains("SpawnPoint", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Markers/", StringComparison.OrdinalIgnoreCase))
            return false;
        return p.Contains("Mob", StringComparison.OrdinalIgnoreCase)
               || p.Contains("Human", StringComparison.OrdinalIgnoreCase)
               || p.Contains("Humanoid", StringComparison.OrdinalIgnoreCase)
               || p.Contains("Player", StringComparison.OrdinalIgnoreCase)
               || p.Contains("Species", StringComparison.OrdinalIgnoreCase)
               || p.Contains("/Mobs/", StringComparison.OrdinalIgnoreCase)
               || p.Contains("Ghost", StringComparison.OrdinalIgnoreCase)
               || p.Contains("Observer", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// PC eye VisibilityFlags.Normal|Ghost — hide Subfloor, spawn markers, placement ghosts.
    /// </summary>
    static bool IsHiddenFromDefaultEye(string? proto, string? path, int depth, bool fromNetwork)
    {
        if (fromNetwork && depth <= BelowFloorDepth)
            return true;

        var p = (proto ?? "") + " " + (path ?? "");
        // Spawn markers / RandomSpawner / job green circles (Markers/jobs.rsi state:green).
        if (p.Contains("SpawnPoint", StringComparison.OrdinalIgnoreCase)
            || p.Contains("SpawnMarker", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Spawner", StringComparison.OrdinalIgnoreCase)
            || p.Contains("WarpPoint", StringComparison.OrdinalIgnoreCase)
            || p.Contains("MarkerBase", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Marker", StringComparison.OrdinalIgnoreCase)
                && (p.Contains("Spawn", StringComparison.OrdinalIgnoreCase)
                    || p.Contains("Jobs", StringComparison.OrdinalIgnoreCase)
                    || p.Contains("green", StringComparison.OrdinalIgnoreCase))
            || p.Contains("Markers/", StringComparison.OrdinalIgnoreCase)
            || p.Contains("/Markers/", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Spawners/", StringComparison.OrdinalIgnoreCase)
            || p.Contains("/Spawners/", StringComparison.OrdinalIgnoreCase)
            || p.Contains("jobs.rsi", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Placement", StringComparison.OrdinalIgnoreCase)
                && p.Contains("Ghost", StringComparison.OrdinalIgnoreCase))
            return true;

        // Under-floor cables / pipes / disposals (PC Subfloor flag).
        if (p.Contains("Cables/", StringComparison.OrdinalIgnoreCase)
            || p.Contains("/Cable", StringComparison.OrdinalIgnoreCase)
            || p.Contains("PowerCable", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Wire", StringComparison.OrdinalIgnoreCase)
                && (p.Contains("Structure", StringComparison.OrdinalIgnoreCase)
                    || p.Contains("HVWire", StringComparison.OrdinalIgnoreCase)
                    || p.Contains("MVWire", StringComparison.OrdinalIgnoreCase)
                    || p.Contains("LVWire", StringComparison.OrdinalIgnoreCase))
            || p.Contains("DisposalPipe", StringComparison.OrdinalIgnoreCase)
            || p.Contains("DisposalTransit", StringComparison.OrdinalIgnoreCase)
            || p.Contains("/Piping/", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Atmospherics/Pipes", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Subfloor", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    bool TryResolveIconSmooth(string? proto, string? path, out IconSmoothData data)
    {
        data = default;
        var cacheKey = (proto ?? "") + "|" + (path ?? "");
        if (_iconSmoothRemapCache.TryGetValue(cacheKey, out data))
            return true;

        // Resolve once per proto|path then cache. Calling RsiHasNumberedBase / FromRsi for
        // every wall on every MsgState ANR/crashes Android (disk thrash).
        var inferred = IconSmoothInfer.FromRsi(_contentRoot, path, proto);
        var fromProto = _protos?.TryGetIconSmooth(proto);
        if (fromProto is { } sm)
        {
            data = sm;
            // Goob/CD: YAML base "window" vs meta "rwindow0..7".
            // Remap ONLY when YAML base has no numbered sheet (StateNameCache keeps this cheap).
            // Blind remap broke WallReinforced: reinf_over → solid on solid.rsi.
            if (inferred is { } inf
                && !string.IsNullOrEmpty(inf.StateBase)
                && !string.Equals(sm.StateBase, inf.StateBase, StringComparison.OrdinalIgnoreCase)
                && !IconSmoothInfer.RsiHasNumberedBase(_contentRoot, path, sm.StateBase)
                && IconSmoothInfer.RsiHasNumberedBase(_contentRoot, path, inf.StateBase))
            {
                data = new IconSmoothData(sm.Key, inf.StateBase, sm.Mode, sm.AdditionalKeys);
                if (_iconSmoothRemapLogged.Add(cacheKey))
                {
                    Port.Content.DiagLog.Info(
                        $"iconsmooth base remap {proto}: '{sm.StateBase}' → '{inf.StateBase}' ({path})");
                }
            }

            _iconSmoothRemapCache[cacheKey] = data;
            return true;
        }

        if (inferred is { } onlyInf)
        {
            data = onlyInf;
            _iconSmoothRemapCache[cacheKey] = data;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Soft invalidate after ACZ — do NOT wipe the whole remap table (that re-resolved every
    /// wall on the next MsgState and ANR'd the process). Only drop keys for one RSI path.
    /// </summary>
    public void ClearIconSmoothCache()
    {
        // Intentionally no-op for global clears. Use InvalidateIconSmoothPath from ACZ.
    }

    public void InvalidateIconSmoothPath(string? rsiPath)
    {
        if (string.IsNullOrWhiteSpace(rsiPath))
            return;
        lock (this)
        {
            List<string>? dead = null;
            foreach (var key in _iconSmoothRemapCache.Keys)
            {
                if (key.EndsWith("|" + rsiPath, StringComparison.OrdinalIgnoreCase)
                    || key.Equals(rsiPath, StringComparison.OrdinalIgnoreCase)
                    || key.EndsWith("|" + rsiPath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                {
                    dead ??= new List<string>();
                    dead.Add(key);
                }
            }

            if (dead is null)
                return;
            foreach (var key in dead)
            {
                _iconSmoothRemapCache.Remove(key);
                _iconSmoothRemapLogged.Remove(key);
            }
        }
    }

    string? ResolveSpritePath(GameStateDecoder.SpriteVisual? spr, string? proto)
    {
        if (!string.IsNullOrEmpty(spr?.Path))
            return spr!.Path;
        if (spr?.Layers is { Count: > 0 })
        {
            foreach (var layer in spr.Layers)
            {
                if (!string.IsNullOrEmpty(layer.Path))
                    return layer.Path;
            }
        }
        return !string.IsNullOrEmpty(proto) ? _protos?.TryGetSprite(proto) : null;
    }

    static bool IsEditorOnlySpriteState(string? state) =>
        state is not null
        && (state.Equals("full", StringComparison.OrdinalIgnoreCase)
            || state.Equals("icon", StringComparison.OrdinalIgnoreCase));

    static bool IsDoorBaseLayerState(string? state) =>
        state is not null
        && (state.Equals("closed", StringComparison.OrdinalIgnoreCase)
            || state.Equals("open", StringComparison.OrdinalIgnoreCase)
            || state.Equals("opening", StringComparison.OrdinalIgnoreCase)
            || state.Equals("closing", StringComparison.OrdinalIgnoreCase)
            || state.Equals("deny", StringComparison.OrdinalIgnoreCase));

    /// <summary>Map DoorState enum to RSI state names used by airlock RSIs.</summary>
    static string DoorVisualState(string doorState) =>
        doorState.ToLowerInvariant() switch
        {
            "open" or "opened" => "open",
            "opening" => "opening",
            "closing" => "closing",
            "denying" or "deny" => "deny",
            "emagging" or "emagged" => "closed",
            "welded" => "closed", // welded overlay separate; keep base closed
            _ => "closed",
        };

    bool TryReadDoorState(object state, out string doorState)
    {
        doorState = "";
        var tn = state.GetType().Name;
        if (!tn.Contains("Door", StringComparison.OrdinalIgnoreCase)
            || !tn.Contains("State", StringComparison.OrdinalIgnoreCase)
            || tn.Contains("Appearance", StringComparison.OrdinalIgnoreCase))
            return false;

        var t = state.GetType();
        foreach (var name in new[] { "State", "DoorState", "CurrentState" })
        {
            var p = t.GetProperty(name)?.GetValue(state)
                    ?? t.GetField(name)?.GetValue(state);
            if (p is null) continue;
            var s = p is Enum e ? e.ToString() : p.ToString();
            if (string.IsNullOrWhiteSpace(s) || s is "null")
                continue;
            doorState = s!;
            return true;
        }

        return false;
    }

    /// <summary>
    /// PC IconSmoothSystem.CalculateCornerFill remaps corner fills by LocalRotation cardinal.
    /// Tuple order is (NE, NW, SW, SE).
    /// </summary>
    static void RemapIconSmoothCorners(float localRotRadians, ref byte cNE, ref byte cNW, ref byte cSW, ref byte cSE)
    {
        // Cardinal: 0=E, 1=N, 2=W, 3=S (Robust Angle 0 = East).
        var twoPi = MathF.PI * 2f;
        var a = localRotRadians % twoPi;
        if (a < 0) a += twoPi;
        var cardinal = (int)MathF.Floor(((a + MathF.PI / 4f) % twoPi) / (MathF.PI / 2f));
        byte ne = cNE, nw = cNW, sw = cSW, se = cSE;
        switch (cardinal)
        {
            case 1: // North
                cNE = sw; cNW = se; cSW = ne; cSE = nw;
                break;
            case 2: // West
                cNE = se; cNW = ne; cSW = nw; cSE = sw;
                break;
            case 3: // South
                // identity
                break;
            default: // East
                cNE = nw; cNW = sw; cSW = se; cSE = ne;
                break;
        }
    }

    static bool LooksLikeDoorOrMachine(string? proto, string? path)
    {
        var p = (proto ?? "") + " " + (path ?? "");
        return p.Contains("Airlock", StringComparison.OrdinalIgnoreCase)
               || p.Contains("Firelock", StringComparison.OrdinalIgnoreCase)
               || p.Contains("Windoor", StringComparison.OrdinalIgnoreCase)
               || p.Contains("/Doors/", StringComparison.OrdinalIgnoreCase)
               || p.Contains("Structures/Doors", StringComparison.OrdinalIgnoreCase)
               || p.Contains("Shutter", StringComparison.OrdinalIgnoreCase)
               || p.Contains("Computer", StringComparison.OrdinalIgnoreCase)
               || p.Contains("Machine", StringComparison.OrdinalIgnoreCase)
               || p.Contains("Vendor", StringComparison.OrdinalIgnoreCase)
               || p.Contains("Console", StringComparison.OrdinalIgnoreCase)
               || p.Contains("Locker", StringComparison.OrdinalIgnoreCase)
               || p.Contains("Closet", StringComparison.OrdinalIgnoreCase)
               || p.Contains("/Closets/", StringComparison.OrdinalIgnoreCase)
               || p.Contains("/Lockers/", StringComparison.OrdinalIgnoreCase);
    }

    List<WorldTileDraw> BuildTileDrawList(Vector2 focus, float viewTiles, NetEntity eyeMap)
    {
        var list = new List<WorldTileDraw>(Math.Min(MaxDrawTiles, 2048));
        // Without a map filter, 27 stacked maps near a broken eye dump 10k+ tiles and crash.
        if (!eyeMap.IsValid())
            return list;
        if (float.IsNaN(focus.X) || float.IsNaN(focus.Y) || focus.LengthSquared() < 0.01f)
            return list;

        var minX = focus.X - viewTiles;
        var maxX = focus.X + viewTiles;
        var minY = focus.Y - viewTiles;
        var maxY = focus.Y + viewTiles;
        foreach (var (gridEnt, chunks) in _grids)
        {
            if (list.Count >= MaxDrawTiles) break;
            var gridMap = ResolveMapUid(gridEnt);
            if (gridMap.IsValid() && gridMap != eyeMap)
                continue;
            // Orphan grids with no map uid after LeavePvs — skip (were drawn on every map).
            if (!gridMap.IsValid())
                continue;

            var chunkSize = _gridChunkSize.TryGetValue(gridEnt, out var cs) ? cs : (ushort)ChunkDefault;
            if (chunkSize == 0) chunkSize = ChunkDefault;
            var origin = ResolveWorldPos(gridEnt);
            var gridRot = new Angle(ResolveWorldRot(gridEnt));
            foreach (var (chunkIndex, datum) in chunks)
            {
                if (list.Count >= MaxDrawTiles) break;
                if (datum.TileData is null) continue;
                var tiles = datum.TileData;
                for (var i = 0; i < tiles.Length; i++)
                {
                    if (list.Count >= MaxDrawTiles) break;
                    var tile = tiles[i];
                    if (tile.IsEmpty) continue;
                    // Match SharedMapSystem flatten: index = x * ChunkSize + y (x outer, y inner).
                    var x = i / chunkSize;
                    var y = i % chunkSize;
                    var lx = chunkIndex.X * chunkSize + x;
                    var ly = chunkIndex.Y * chunkSize + y;
                    var local = new Vector2(lx + 0.5f, ly + 0.5f);
                    var world = gridRot.RotateVec(local) + origin;
                    if (world.X < minX || world.X > maxX || world.Y < minY || world.Y > maxY)
                        continue;
                    ColorForTile(tile.TypeId, out var r, out var g, out var b);
                    var rsi = _tiles?.TryGetSprite((ushort)tile.TypeId);
                    // Textured floors: white modulate (pastel only for missing art).
                    if (!string.IsNullOrEmpty(rsi))
                    {
                        r = 255;
                        g = 255;
                        b = 255;
                    }

                    list.Add(new WorldTileDraw(
                        world.X, world.Y, r, g, b, rsi, null,
                        Variant: tile.Variant,
                        RotationMirroring: tile.RotationMirroring,
                        Rotation: (float)gridRot.Theta));
                }
            }
        }

        return list;
    }

    bool TryApplyGrid(NetEntity ent, object state)
    {
        if (state is MapGridComponentState full)
        {
            _grids[ent] = new Dictionary<Vector2i, ChunkDatum>(full.FullGridData);
            _gridChunkSize[ent] = full.ChunkSize == 0 ? (ushort)ChunkDefault : full.ChunkSize;
            return true;
        }

        if (state is MapGridComponentDeltaState delta)
        {
            if (!_grids.TryGetValue(ent, out var chunks))
            {
                chunks = new Dictionary<Vector2i, ChunkDatum>();
                _grids[ent] = chunks;
            }

            _gridChunkSize[ent] = delta.ChunkSize == 0 ? (ushort)ChunkDefault : delta.ChunkSize;
            if (delta.ChunkData is null)
                return true;
            foreach (var (index, data) in delta.ChunkData)
            {
                if (data.IsDeleted())
                    chunks.Remove(index);
                else
                    chunks[index] = data;
            }

            return true;
        }

        // Name fallback for ALC mismatches / internal type visibility quirks.
        var tn = state.GetType().Name;
        if (!tn.Contains("MapGridComponent", StringComparison.Ordinal))
            return false;
        try
        {
            var t = state.GetType();
            var chunkSize = (ushort)(t.GetField("ChunkSize")?.GetValue(state) as ushort?
                                     ?? t.GetProperty("ChunkSize")?.GetValue(state) as ushort?
                                     ?? ChunkDefault);
            _gridChunkSize[ent] = chunkSize == 0 ? (ushort)ChunkDefault : chunkSize;
            var fullData = t.GetField("FullGridData")?.GetValue(state)
                           ?? t.GetProperty("FullGridData")?.GetValue(state);
            var chunkData = t.GetField("ChunkData")?.GetValue(state)
                            ?? t.GetProperty("ChunkData")?.GetValue(state);
            if (TryCopyChunkDict(fullData, out var fullDict))
            {
                _grids[ent] = fullDict;
                return true;
            }

            if (TryCopyChunkDict(chunkData, out var deltaDict))
            {
                if (!_grids.TryGetValue(ent, out var chunks))
                {
                    chunks = new();
                    _grids[ent] = chunks;
                }

                foreach (var (index, data) in deltaDict)
                {
                    if (data.IsDeleted()) chunks.Remove(index);
                    else chunks[index] = data;
                }

                return true;
            }
        }
        catch { /* ignore */ }

        return false;
    }

    static bool TryCopyChunkDict(object? raw, out Dictionary<Vector2i, ChunkDatum> dict)
    {
        dict = new();
        if (raw is null) return false;
        if (raw is Dictionary<Vector2i, ChunkDatum> typed)
        {
            dict = new Dictionary<Vector2i, ChunkDatum>(typed);
            return true;
        }

        if (raw is not System.Collections.IDictionary idict)
            return false;

        var any = false;
        foreach (System.Collections.DictionaryEntry entry in idict)
        {
            if (entry.Key is null || entry.Value is null) continue;
            Vector2i index;
            if (entry.Key is Vector2i v)
                index = v;
            else
            {
                var kt = entry.Key.GetType();
                var x = kt.GetField("X")?.GetValue(entry.Key) ?? kt.GetProperty("X")?.GetValue(entry.Key);
                var y = kt.GetField("Y")?.GetValue(entry.Key) ?? kt.GetProperty("Y")?.GetValue(entry.Key);
                if (x is null || y is null) continue;
                index = new Vector2i(Convert.ToInt32(x), Convert.ToInt32(y));
            }

            if (entry.Value is ChunkDatum cd)
            {
                dict[index] = cd;
                any = true;
                continue;
            }

            // Reflect TileData out of foreign ChunkDatum.
            var vt = entry.Value.GetType();
            var tileData = vt.GetField("TileData")?.GetValue(entry.Value)
                           ?? vt.GetProperty("TileData")?.GetValue(entry.Value);
            if (tileData is Tile[] tiles)
            {
                var fixtures = vt.GetField("Fixtures")?.GetValue(entry.Value) as HashSet<string>
                               ?? vt.GetProperty("Fixtures")?.GetValue(entry.Value) as HashSet<string>
                               ?? new HashSet<string>();
                var boundsObj = vt.GetField("CachedBounds")?.GetValue(entry.Value)
                                ?? vt.GetProperty("CachedBounds")?.GetValue(entry.Value);
                var bounds = boundsObj is Box2i bb ? bb : default;
                dict[index] = ChunkDatum.CreateModified(tiles, fixtures, bounds);
                any = true;
            }
            else if (tileData is null)
            {
                // deleted chunk
                dict[index] = ChunkDatum.Empty;
                any = true;
            }
        }

        return any;
    }

    /// <summary>
    /// Wallmount lights / snapCardinals / noRot: force RSI direction index 0
    /// so 4-dir sheets don't spin with camera or entity yaw.
    /// </summary>
    static bool ForceSpriteDir0(string? path, string? proto, bool snapCardinals, bool noRotation)
    {
        if (snapCardinals || noRotation)
            return true;
        var p = (path ?? "") + " " + (proto ?? "");
        return p.Contains("Lighting", StringComparison.OrdinalIgnoreCase)
               || p.Contains("Wallmount", StringComparison.OrdinalIgnoreCase)
               || p.Contains("PoweredLight", StringComparison.OrdinalIgnoreCase)
               || p.Contains("light_tube", StringComparison.OrdinalIgnoreCase)
               || p.Contains("light_small", StringComparison.OrdinalIgnoreCase)
               || p.Contains("light_soft", StringComparison.OrdinalIgnoreCase)
               || p.Contains("/Lights/", StringComparison.OrdinalIgnoreCase);
    }

    static void ColorForTile(int typeId, out byte r, out byte g, out byte b)
    {
        // Muted slate fallback — old pastel hash looked pink for missing sand sprites.
        unchecked
        {
            var h = (uint)typeId * 2654435761u;
            r = (byte)(48 + (h & 0x1F));
            g = (byte)(52 + ((h >> 8) & 0x1F));
            b = (byte)(58 + ((h >> 16) & 0x1F));
        }
    }

    static bool IsStructureProto(string? proto) =>
        !string.IsNullOrEmpty(proto) && (
            proto.Contains("Airlock", StringComparison.OrdinalIgnoreCase)
            || proto.Contains("Door", StringComparison.OrdinalIgnoreCase)
            || proto.Contains("Locker", StringComparison.OrdinalIgnoreCase)
            || proto.Contains("Crate", StringComparison.OrdinalIgnoreCase)
            || proto.Contains("Closet", StringComparison.OrdinalIgnoreCase)
            || proto.Contains("Wall", StringComparison.OrdinalIgnoreCase)
            || proto.Contains("Window", StringComparison.OrdinalIgnoreCase)
            || proto.Contains("Grille", StringComparison.OrdinalIgnoreCase)
            || proto.Contains("Firelock", StringComparison.OrdinalIgnoreCase)
            || proto.Contains("Windoor", StringComparison.OrdinalIgnoreCase));

    static bool IsIconSmoothPath(string? path, string? proto)
    {
        var p = (path ?? "") + " " + (proto ?? "");
        return p.Contains("Wall", StringComparison.OrdinalIgnoreCase)
               || p.Contains("Window", StringComparison.OrdinalIgnoreCase)
               || p.Contains("Grille", StringComparison.OrdinalIgnoreCase);
    }

    static bool ShouldHideFromDefaultEye(string? proto, string? path, int depth)
    {
        var p = ((proto ?? "") + " " + (path ?? "")).Replace('\\', '/');
        // Spawn markers / landmarks — never shown to default eye.
        if (p.Contains("Spawn", StringComparison.OrdinalIgnoreCase)
            && (p.Contains("Marker", StringComparison.OrdinalIgnoreCase)
                || p.Contains("Point", StringComparison.OrdinalIgnoreCase)
                || p.Contains("Spawners/", StringComparison.OrdinalIgnoreCase)))
            return true;
        if (p.Contains("Landmark", StringComparison.OrdinalIgnoreCase)
            || p.Contains("WarpPoint", StringComparison.OrdinalIgnoreCase)
            && !p.Contains("Ghost", StringComparison.OrdinalIgnoreCase))
            return true;
        // Subfloor / cable clutter under plating.
        if (p.Contains("Subfloor", StringComparison.OrdinalIgnoreCase)
            || p.Contains("/Cable", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Cables/", StringComparison.OrdinalIgnoreCase)
            || p.Contains("WireTerminal", StringComparison.OrdinalIgnoreCase)
            || (p.Contains("Power/", StringComparison.OrdinalIgnoreCase)
                && p.Contains("cable", StringComparison.OrdinalIgnoreCase)))
            return true;
        // Very low draw-depth floor overlays that are usually under tiles.
        if (depth > 0 && depth < 5
            && (p.Contains("plating", StringComparison.OrdinalIgnoreCase)
                || p.Contains("lattice", StringComparison.OrdinalIgnoreCase)))
            return true;
        return false;
    }

    bool TryApplyContainerState(NetEntity ent, object state)
    {
        var tn = state.GetType().Name;
        // EntityStorage / Fill / Container visuals often encode Open via Appearance or component state.
        if (tn.Contains("EntityStorage", StringComparison.OrdinalIgnoreCase)
            || tn.Contains("StorageComponent", StringComparison.OrdinalIgnoreCase)
            || tn.Contains("ContainerManager", StringComparison.OrdinalIgnoreCase)
            || tn.Contains("LockComponent", StringComparison.OrdinalIgnoreCase)
            || (tn.Contains("Appearance", StringComparison.OrdinalIgnoreCase)
                && (_prototypes.TryGetValue(ent, out var proto) && IsContainerProto(proto))))
        {
            var open = TryReadOpenFlag(state);
            if (open is true)
            {
                _openContainers.Add(ent);
                _closedContainers.Remove(ent);
                _containerOccludes[ent] = false;
            }
            else if (open is false)
            {
                _closedContainers.Add(ent);
                _openContainers.Remove(ent);
                _containerOccludes[ent] = true;
            }
            else if (IsContainerProto(_prototypes.GetValueOrDefault(ent)))
            {
                // Default: lockers/crates occlude until proven open.
                if (!_openContainers.Contains(ent))
                {
                    _closedContainers.Add(ent);
                    _containerOccludes[ent] = true;
                }
            }

            return tn.Contains("ContainerManager", StringComparison.OrdinalIgnoreCase)
                   || tn.Contains("EntityStorage", StringComparison.OrdinalIgnoreCase);
        }

        // Heuristic: mark known container prototypes when we see their sprite.
        if (_prototypes.TryGetValue(ent, out var p) && IsContainerProto(p)
            && !_openContainers.Contains(ent) && !_closedContainers.Contains(ent))
        {
            _closedContainers.Add(ent);
            _containerOccludes[ent] = true;
        }

        return false;
    }

    static bool? TryReadOpenFlag(object state)
    {
        var t = state.GetType();
        foreach (var name in new[] { "Open", "Opened", "IsOpen", "IsOpened", "Unlocked" })
        {
            var v = t.GetProperty(name)?.GetValue(state) ?? t.GetField(name)?.GetValue(state);
            if (v is bool b) return b;
        }

        return null;
    }

    static bool IsContainerProto(string? proto) =>
        !string.IsNullOrEmpty(proto) && (
            proto.Contains("Locker", StringComparison.OrdinalIgnoreCase)
            || proto.Contains("Closet", StringComparison.OrdinalIgnoreCase)
            || proto.Contains("Crate", StringComparison.OrdinalIgnoreCase)
            || proto.Contains("Fridge", StringComparison.OrdinalIgnoreCase)
            || proto.Contains("Oven", StringComparison.OrdinalIgnoreCase)
            || proto.Contains("Dumpster", StringComparison.OrdinalIgnoreCase)
            || proto.Contains("BodyBag", StringComparison.OrdinalIgnoreCase)
            || proto.Contains("SecureCabinet", StringComparison.OrdinalIgnoreCase));

    bool IsHiddenByContainer(NetEntity ent, TransformComponentState xf)
    {
        // Walk a few parents — contents are parented into the container entity.
        var cur = xf.ParentID;
        for (var depth = 0; depth < 6 && cur.IsValid(); depth++)
        {
            if (_containerOccludes.TryGetValue(cur, out var occludes) && occludes)
                return true;
            if (_closedContainers.Contains(cur))
                return true;
            // Prototype-only containers never saw a storage state — still occlude children.
            if (_prototypes.TryGetValue(cur, out var proto) && IsContainerProto(proto)
                && !_openContainers.Contains(cur))
                return true;
            if (!_xforms.TryGetValue(cur, out var parentXf))
                break;
            if (_mapEntities.Contains(cur) || _grids.ContainsKey(cur))
                break;
            cur = parentXf.ParentID;
        }

        return false;
    }

    /// <summary>
    /// Hide inventory/hand/pocket entities that pile on the mob world position.
    /// Equipped clothing is drawn as Sprite layers on the mob itself (PC), not as
    /// separate world sprites of the item entities.
    /// </summary>
    bool IsHiddenInInventoryOrEquippedSlot(NetEntity ent, TransformComponentState xf)
    {
        _prototypes.TryGetValue(ent, out var selfProto);
        _sprites.TryGetValue(ent, out var selfSpr);
        // Never hide mobs / observers themselves.
        if (IsPlayerLike(selfProto, selfSpr))
            return false;

        var cur = xf.ParentID;
        for (var depth = 0; depth < 8 && cur.IsValid(); depth++)
        {
            if (_mapEntities.Contains(cur) || _grids.ContainsKey(cur))
                return false;

            _prototypes.TryGetValue(cur, out var parentProto);
            _sprites.TryGetValue(cur, out var parentSpr);
            if (IsPlayerLike(parentProto, parentSpr))
                return true; // item/clothing entity parented under a mob → inventory/slot

            // Hands / inventory container entities often named *Inventory* / *Hands*.
            if (!string.IsNullOrEmpty(parentProto)
                && (parentProto.Contains("Inventory", StringComparison.OrdinalIgnoreCase)
                    || parentProto.Contains("Hands", StringComparison.OrdinalIgnoreCase)
                    || parentProto.Contains("Pocket", StringComparison.OrdinalIgnoreCase)
                    || parentProto.Contains("Belt", StringComparison.OrdinalIgnoreCase)
                    || parentProto.Contains("Backpack", StringComparison.OrdinalIgnoreCase)
                    || parentProto.Contains("Satchel", StringComparison.OrdinalIgnoreCase)
                    || parentProto.Contains("Duffel", StringComparison.OrdinalIgnoreCase)))
                return true;

            if (!_xforms.TryGetValue(cur, out var parentXf))
                break;
            cur = parentXf.ParentID;
        }

        return false;
    }

    /// <summary>
    /// Grid-local snap occupancy for IconSmooth walls/windows/grilles (like PC IconSmoothSystem).
    /// Key: (gridNetEntity, tileX, tileY) packed into a long via grid hash + coords.
    /// </summary>
    Dictionary<(int gx, int gy), byte> BuildIconSmoothOccupancy(NetEntity eyeMap)
    {
        var occ = new Dictionary<(int gx, int gy), byte>(512);
        foreach (var (ent, _) in _xforms)
        {
            if (eyeMap.IsValid())
            {
                var map = ResolveMapUid(ent);
                if (map.IsValid() && map != eyeMap)
                    continue;
            }

            _prototypes.TryGetValue(ent, out var proto);
            _sprites.TryGetValue(ent, out var spr);
            if (!IsIconSmoothPath(spr?.Path, proto))
                continue;

            var wp = ResolveWorldPos(ent);
            // Snap like SharedMapSystem / IconSmooth: floor to tile indices.
            var gx = (int)MathF.Floor(wp.X);
            var gy = (int)MathF.Floor(wp.Y);
            occ[(gx, gy)] = 1;
        }

        return occ;
    }

    string? ResolveIconSmoothState(
        string? path, string? proto, string? current,
        Vector2 worldPos, float worldRot,
        Dictionary<(int gx, int gy), byte> occ)
    {
        if (!IsIconSmoothPath(path, proto))
            return current;

        var gx = (int)MathF.Floor(worldPos.X);
        var gy = (int)MathF.Floor(worldPos.Y);
        // Cardinal neighbors in world tile space (north-up; rotation ignored for station grids).
        var n = occ.ContainsKey((gx, gy + 1));
        var s = occ.ContainsKey((gx, gy - 1));
        var e = occ.ContainsKey((gx + 1, gy));
        var w = occ.ContainsKey((gx - 1, gy));
        var mask = (n ? 1 : 0) | (s ? 2 : 0) | (e ? 4 : 0) | (w ? 8 : 0);

        // Prefer explicit network/IconSmooth state when already a connection key.
        if (!string.IsNullOrEmpty(current)
            && !string.Equals(current, "full", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(current, "icon", StringComparison.OrdinalIgnoreCase))
            return current;

        // SS14 wall RSI convention: "full" when surrounded; else numeric / directional.
        if (mask == 15)
            return "full";
        // Many forks ship states "0"…"15" or just rely on "full" fill + corners.
        // Keep "full" for partial too — avoids flicker to missing states; still correct fill.
        return "full";
    }

    WorldEntityDraw[] PatchDrawSnapshot(WorldEntityDraw[] prev, NetEntity controlled, Vector2 worldEye)
    {
        var arr = new WorldEntityDraw[prev.Length];
        for (var i = 0; i < prev.Length; i++)
        {
            var e = prev[i];
            if (e.IsControlled || (controlled.IsValid() && e.Entity == controlled))
            {
                arr[i] = e with { X = worldEye.X, Y = worldEye.Y };
                continue;
            }

            if (_movedScratch.Contains(e.Entity) && _xforms.ContainsKey(e.Entity))
            {
                var wp = ResolveWorldPos(e.Entity);
                arr[i] = e with { X = wp.X, Y = wp.Y };
            }
            else if (!_xforms.ContainsKey(e.Entity) && !e.IsControlled)
            {
                // Left store between snapshots — keep last pose until full rebuild.
                arr[i] = e;
            }
            else
            {
                arr[i] = e;
            }
        }

        return arr;
    }

    Vector2 ResolveWorldPos(NetEntity ent)
    {
        if (_worldPosCache.TryGetValue(ent, out var cached))
            return cached;
        if (!_xforms.TryGetValue(ent, out _))
            return default;

        var mapUid = ResolveMapUid(ent);
        var visiting = _visitScratch;
        visiting.Clear();
        var cur = ent;
        var pos = Vector2.Zero;
        for (var depth = 0; depth < 24; depth++)
        {
            // Match SharedTransformSystem.GetWorldPosition: stop before applying the map entity.
            if (_mapEntities.Contains(cur) && cur != ent)
                break;
            if (!_xforms.TryGetValue(cur, out var t)) break;
            if (!visiting.Add(cur)) break;
            pos = t.Rotation.RotateVec(pos) + t.LocalPosition;
            if (!t.ParentID.IsValid() || t.ParentID == cur) break;
            if (mapUid.IsValid() && t.ParentID == mapUid)
                break; // next is map — done
            cur = t.ParentID;
        }

        _worldPosCache[ent] = pos;
        return pos;
    }

    NetEntity ResolveMapUid(NetEntity ent)
    {
        if (_mapUidCache.TryGetValue(ent, out var cached))
            return cached;
        if (!_xforms.TryGetValue(ent, out _))
            return default;

        var visiting = _visitScratch;
        visiting.Clear();
        var cur = ent;
        NetEntity found = default;
        var sawMapComponent = false;
        for (var depth = 0; depth < 24; depth++)
        {
            if (_mapEntities.Contains(cur))
            {
                found = cur;
                sawMapComponent = true;
                break;
            }

            if (!_xforms.TryGetValue(cur, out var t)) break;
            if (!visiting.Add(cur)) break;
            if (!t.ParentID.IsValid() || t.ParentID == cur)
            {
                // Only treat as map root when MapComponent was seen — otherwise orphan
                // ghosts after LeavePvs were "maps" and broke eye filtering.
                if (sawMapComponent)
                    found = cur;
                break;
            }

            cur = t.ParentID;
        }

        // Do not cache misses — parent may arrive next packet.
        if (found.IsValid())
            _mapUidCache[ent] = found;
        return found;
    }

        float ResolveWorldRot(NetEntity ent)
    {
        if (!_xforms.TryGetValue(ent, out _))
            return 0;

        var mapUid = ResolveMapUid(ent);
        var visiting = _visitScratch;
        visiting.Clear();
        var cur = ent;
        Angle rot = default;
        for (var depth = 0; depth < 24; depth++)
        {
            // Match GetWorldPosition: stop before map entity rotation.
            if (_mapEntities.Contains(cur) && cur != ent)
                break;
            if (!_xforms.TryGetValue(cur, out var t)) break;
            if (!visiting.Add(cur)) break;
            if (!t.NoLocalRotation)
                rot = t.Rotation + rot;
            if (!t.ParentID.IsValid() || t.ParentID == cur) break;
            if (mapUid.IsValid() && t.ParentID == mapUid)
                break;
            cur = t.ParentID;
        }

        return (float)rot.Theta;
    }

    /// <summary>
    /// PC ContainerSystem occlusion: entities parented under closed storage furniture stay hidden.
    /// </summary>
    bool IsOccludedByContainer(NetEntity ent, TransformComponentState xf)
    {
        var visiting = new HashSet<NetEntity>();
        var child = ent;
        var parent = xf.ParentID;
        for (var depth = 0; depth < 12; depth++)
        {
            if (!parent.IsValid() || !visiting.Add(parent))
                break;
            if (_grids.ContainsKey(parent) || _mapEntities.Contains(parent))
                break;

            _prototypes.TryGetValue(parent, out var parentProto);
            if (LooksLikeClosedStorage(parentProto))
                return true;

            // Also hide when parent is clearly a storage RSI (proto may be missing).
            if (_sprites.TryGetValue(parent, out var parentSpr)
                && LooksLikeClosedStorage(parentSpr.Path)
                && !IsPlayerLike(parentProto, parentSpr.Path))
                return true;

            if (!_xforms.TryGetValue(parent, out var pxf))
                break;
            child = parent;
            parent = pxf.ParentID;
        }

        return false;
    }

    static bool LooksLikeClosedStorage(string? protoOrPath)
    {
        if (string.IsNullOrWhiteSpace(protoOrPath))
            return false;
        var p = protoOrPath;
        return p.Contains("Locker", StringComparison.OrdinalIgnoreCase)
               || p.Contains("Closet", StringComparison.OrdinalIgnoreCase)
               || p.Contains("Crate", StringComparison.OrdinalIgnoreCase)
               || p.Contains("Fridge", StringComparison.OrdinalIgnoreCase)
               || p.Contains("Freezer", StringComparison.OrdinalIgnoreCase)
               || p.Contains("OreBox", StringComparison.OrdinalIgnoreCase)
               || p.Contains("FilingCabinet", StringComparison.OrdinalIgnoreCase)
               || p.Contains("SecureCabinet", StringComparison.OrdinalIgnoreCase)
               || p.Contains("WallCabinet", StringComparison.OrdinalIgnoreCase)
               || p.Contains("Dumpster", StringComparison.OrdinalIgnoreCase)
               || p.Contains("BodyBag", StringComparison.OrdinalIgnoreCase)
               || p.Contains("SuitStorage", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// PC SharedMoverController.GetParentGridAngle: parent grid/map world rotation plus
    /// InputMover relative camera rotation. This is independent of entity facing.
    /// </summary>
    public float GetGridCameraRotation(NetEntity ent)
    {
        lock (this)
        {
            if (!ent.IsValid())
                return 0f;
            if (_movers.TryGetValue(ent, out var mover))
            {
                var relative = mover.RelativeEntity;
                if (relative.IsValid() && _xforms.ContainsKey(relative))
                    return ResolveWorldRot(relative) + mover.RelativeRotation;
            }

            var grid = FindParentGrid(ent);
            if (grid.IsValid())
                return ResolveWorldRot(grid);
            var map = ResolveMapUid(ent);
            return map.IsValid() && _xforms.ContainsKey(map) ? ResolveWorldRot(map) : 0f;
        }
    }

    static bool TryReadInputMover(
        object state,
        out (NetEntity RelativeEntity, float RelativeRotation, float TargetRelativeRotation) mover)
    {
        mover = default;
        var t = state.GetType();
        if (!t.Name.Contains("InputMoverComponentState", StringComparison.OrdinalIgnoreCase)
            && !t.Name.Contains("InputMover", StringComparison.OrdinalIgnoreCase))
            return false;

        object? Read(string name) => t.GetProperty(name)?.GetValue(state) ?? t.GetField(name)?.GetValue(state);
        var relativeObj = Read("RelativeEntity");
        var relative = relativeObj is NetEntity net ? net : default;
        static float AngleValue(object? value) => value switch
        {
            Angle a => (float)a.Theta,
            float f => f,
            double d => (float)d,
            _ => 0f,
        };
        mover = (relative, AngleValue(Read("RelativeRotation")), AngleValue(Read("TargetRelativeRotation")));
        return true;
    }

    /// <summary>World rotation of the grid under an entity, snapped to cardinals (legacy helper).</summary>
    public float GetSnappedGridRotation(NetEntity ent)
    {
        lock (this)
        {
            if (!ent.IsValid() || !_xforms.ContainsKey(ent))
                return 0f;
            var grid = FindParentGrid(ent);
            if (!grid.IsValid())
                return 0f;
            return SnapToCardinal(ResolveWorldRot(grid));
        }
    }

    NetEntity FindParentGrid(NetEntity ent)
    {
        var visiting = new HashSet<NetEntity>();
        var cur = ent;
        for (var depth = 0; depth < 24; depth++)
        {
            if (!visiting.Add(cur)) break;
            if (_grids.ContainsKey(cur))
                return cur;
            if (!_xforms.TryGetValue(cur, out var t)) break;
            if (!t.ParentID.IsValid() || t.ParentID == cur) break;
            cur = t.ParentID;
        }

        return default;
    }

    public static float SnapToCardinal(float radians)
    {
        var twoPi = MathF.PI * 2f;
        var a = radians % twoPi;
        if (a < 0) a += twoPi;
        var q = MathF.PI * 0.5f;
        return MathF.Round(a / q) * q;
    }

    static bool IsGhostComponentState(object state)
    {
        var tn = state.GetType().Name;
        return tn.Contains("GhostComponent", StringComparison.OrdinalIgnoreCase)
               && !tn.Contains("GhostRole", StringComparison.OrdinalIgnoreCase)
               && !tn.Contains("GhostHearing", StringComparison.OrdinalIgnoreCase);
    }

    bool TryExtractAudio(object state, NetEntity ent)
    {
        var tn = state.GetType().Name;
        // AutoGenerateComponentState → *AudioComponent*State (field deltas included).
        if (!tn.Contains("AudioComponent", StringComparison.OrdinalIgnoreCase)
            || tn.Contains("AudioParams", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!_audio.TryGetValue(ent, out var vis))
        {
            vis = new AudioVisual();
            _audio[ent] = vis;
        }

        var t = state.GetType();
        var file = t.GetProperty("FileName")?.GetValue(state) as string
                   ?? t.GetField("FileName")?.GetValue(state) as string;
        if (!string.IsNullOrWhiteSpace(file))
            vis.FileName = file.Trim();

        var global = t.GetProperty("Global")?.GetValue(state)
                     ?? t.GetField("Global")?.GetValue(state);
        if (global is bool gb)
            vis.Global = gb;

        var stateObj = t.GetProperty("State")?.GetValue(state)
                       ?? t.GetField("State")?.GetValue(state);
        if (stateObj is not null)
        {
            // Robust.Shared.Audio.Components.AudioState: Stopped=0, Playing=1, Paused=2
            if (stateObj is Enum en)
                vis.Playing = Convert.ToInt32(en) == 1;
            else
            {
                var sn = stateObj.ToString() ?? "";
                vis.Playing = sn.Equals("Playing", StringComparison.OrdinalIgnoreCase);
            }
        }

        var paramsObj = t.GetProperty("Params")?.GetValue(state)
                        ?? t.GetField("Params")?.GetValue(state);
        if (paramsObj is not null)
        {
            var pt = paramsObj.GetType();
            var vol = pt.GetProperty("Volume")?.GetValue(paramsObj)
                      ?? pt.GetField("Volume")?.GetValue(paramsObj)
                      ?? pt.GetField("_volume")?.GetValue(paramsObj);
            if (vol is float vf)
                vis.VolumeDb = vf;
            else if (vol is double vd)
                vis.VolumeDb = (float)vd;

            var maxD = pt.GetProperty("MaxDistance")?.GetValue(paramsObj)
                       ?? pt.GetField("MaxDistance")?.GetValue(paramsObj);
            if (maxD is float mf)
                vis.MaxDistance = MathF.Max(1f, mf);
            else if (maxD is double md)
                vis.MaxDistance = MathF.Max(1f, (float)md);

            var loop = pt.GetProperty("Loop")?.GetValue(paramsObj)
                       ?? pt.GetField("Loop")?.GetValue(paramsObj);
            if (loop is bool lb)
                vis.Loop = lb;
        }

        return !string.IsNullOrWhiteSpace(vis.FileName) || _audio.ContainsKey(ent);
    }

    List<WorldAudioCue> BuildAudioCueList(Vector2 focus, float viewTiles, NetEntity eyeMap)
    {
        var list = new List<WorldAudioCue>(Math.Min(_audio.Count, 64));
        var hearR2 = viewTiles * viewTiles * 1.5f;
        foreach (var (ent, a) in _audio)
        {
            if (string.IsNullOrWhiteSpace(a.FileName))
                continue;

            if (eyeMap.IsValid())
            {
                var map = ResolveMapUid(ent);
                if (map.IsValid() && map != eyeMap)
                    continue;
            }

            float x = focus.X, y = focus.Y;
            if (_xforms.ContainsKey(ent))
            {
                var wp = ResolveWorldPos(ent);
                x = wp.X;
                y = wp.Y;
                if (!a.Global)
                {
                    var dx = x - focus.X;
                    var dy = y - focus.Y;
                    if (dx * dx + dy * dy > hearR2)
                        continue;
                }
            }
            else if (!a.Global)
                continue;

            list.Add(new WorldAudioCue(
                ent, a.FileName, x, y, a.VolumeDb, a.MaxDistance, a.Global, a.Loop, a.Playing));
            if (list.Count >= 48)
                break;
        }

        return list;
    }

    void TryApplyGhostFlags(object state)
    {
        var tn = state.GetType().Name;
        if (!tn.Contains("Ghost", StringComparison.OrdinalIgnoreCase))
            return;

        var t = state.GetType();
        var canReturn = t.GetProperty("CanReturnToBody")?.GetValue(state)
                        ?? t.GetField("CanReturnToBody")?.GetValue(state);
        if (canReturn is bool br)
            _canReturnToBody = br;

        var canRoles = t.GetProperty("CanTakeGhostRoles")?.GetValue(state)
                       ?? t.GetField("CanTakeGhostRoles")?.GetValue(state);
        if (canRoles is bool bt)
            _canTakeGhostRoles = bt;
    }

    static bool TryReadTransform(object state, out TransformComponentState xf)
    {
        if (state is TransformComponentState direct)
        {
            xf = direct;
            return true;
        }

        var tn = state.GetType().Name;
        if (!tn.Contains("TransformComponentState", StringComparison.Ordinal))
        {
            xf = default;
            return false;
        }

        try
        {
            var t = state.GetType();
            var local = t.GetField("LocalPosition")?.GetValue(state) is Vector2 lp ? lp
                : t.GetProperty("LocalPosition")?.GetValue(state) is Vector2 lp2 ? lp2 : default;
            var rot = t.GetField("Rotation")?.GetValue(state) is Angle a ? a
                : t.GetProperty("Rotation")?.GetValue(state) is Angle a2 ? a2 : default;
            var parent = t.GetField("ParentID")?.GetValue(state) is NetEntity p ? p
                : t.GetProperty("ParentID")?.GetValue(state) is NetEntity p2 ? p2 : default;
            var noRot = t.GetField("NoLocalRotation")?.GetValue(state) as bool? ?? false;
            var anchored = t.GetField("Anchored")?.GetValue(state) as bool? ?? false;
            xf = new TransformComponentState(local, rot, parent, noRot, anchored);
            return true;
        }
        catch
        {
            xf = default;
            return false;
        }
    }
}
