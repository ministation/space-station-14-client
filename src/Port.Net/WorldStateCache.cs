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
    const int MaxDrawEntities = 4800;
    const int MaxDrawTiles = 12000;
    const int MaxLayersPerEntity = 12;
    const int ChunkDefault = 16;
    /// <summary>PC DrawDepth.BelowFloor (Default=0).</summary>
    const int BelowFloorDepth = -13;

    readonly Dictionary<NetEntity, TransformComponentState> _xforms = new();
    readonly Dictionary<NetEntity, GameStateDecoder.SpriteVisual> _sprites = new();
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
    readonly HashSet<NetEntity> _ghostEntities = new();
    readonly Dictionary<NetEntity, AudioVisual> _audio = new();
    Vector2 _lastEyeOffset;
    bool _lastDrawFov = true;

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
                _mapUidCache.Clear();
            }
        }
    }

    /// <summary>Rebuild floor tiles near the controlled eye after LeavePvs (drop stale areas).</summary>
    public IReadOnlyList<WorldTileDraw> RebuildTilesNearEye(float viewTiles = 40f)
    {
        lock (this)
        {
            _worldPosCache.Clear();
            if (!_lastControlled.IsValid() || !_xforms.ContainsKey(_lastControlled))
                return Array.Empty<WorldTileDraw>();
            var focus = ResolveWorldPos(_lastControlled);
            var eyeMap = ResolveMapUid(_lastControlled);
            return BuildTileDrawList(focus, viewTiles, eyeMap);
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

    public void Clear()
    {
        lock (this)
        {
            _xforms.Clear();
            _sprites.Clear();
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
            _lastEyeOffset = default;
            _lastDrawFov = true;
            _lastControlled = default;
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
                    _lastEyeOffset = default;
                    _lastDrawFov = true;
                    _lastControlled = default;
                    PrototypeHits = 0;
                }

                foreach (var del in deletions)
                {
                    _xforms.Remove(del);
                    _sprites.Remove(del);
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
                NetEntity ctrlScan = default;
                if (me?.ControlledEntity is { } cScan && cScan.IsValid())
                    ctrlScan = cScan;
                else if (_lastControlled.IsValid())
                    ctrlScan = _lastControlled;

                foreach (var es in entities)
                {
                    foreach (var change in es.ComponentChanges.Span)
                    {
                        if (change.State is null)
                            continue;

                        if (TryReadTransform(change.State, out var xf))
                        {
                            // Legacy Leave-PVS signal: NaN local position.
                            if (float.IsNaN(xf.LocalPosition.X) || float.IsNaN(xf.LocalPosition.Y))
                            {
                                _xforms.Remove(es.NetEntity);
                                _sprites.Remove(es.NetEntity);
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
                            // Parent/grid motion invalidates descendant world caches — clear once per packet.
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
                                // Only attach prototype art if we have no network SpriteComponent yet.
                                if (!_sprites.TryGetValue(es.NetEntity, out var existing) || !existing.FromNetwork)
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

                        if (ctrlScan.IsValid() && es.NetEntity == ctrlScan)
                            TryApplyGhostFlags(change.State);

                        if (IsGhostComponentState(change.State))
                            _ghostEntities.Add(es.NetEntity);

                        if (TryApplyContainerState(es.NetEntity, change.State))
                            continue;

                        if (TryExtractAudio(change.State, es.NetEntity))
                            continue;

                        var before = _sprites.Count;
                        GameStateDecoder.TryExtractSpritePublic(change.State, es.NetEntity, _sprites);
                        if (_sprites.Count != before || _sprites.ContainsKey(es.NetEntity))
                            packetSprites++;
                    }
                }

                if (invalidatePos)
                {
                    _worldPosCache.Clear();
                    _mapUidCache.Clear();
                }

                // Late bind sprites for entities that never got a SpriteComponentState.
                foreach (var (ent, proto) in _prototypes)
                {
                    if (_sprites.TryGetValue(ent, out var spr) && spr.FromNetwork)
                        continue;
                    if (!_sprites.TryGetValue(ent, out spr) || string.IsNullOrEmpty(spr.Path))
                        TryAttachPrototypeSprite(ent, proto);
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

                var drawList = _drawListScratch;
                drawList.Clear();
                if (drawList.Capacity < Math.Min(_xforms.Count * 2, MaxDrawEntities * 2))
                    drawList.Capacity = Math.Min(_xforms.Count * 2, MaxDrawEntities * 2);
                Vector2 sum = default;
                var nSum = 0;
                // Viewport stream: only draw near the eye (+ margin). Far store kept until LeavePvs.
                // Wider than a phone screen at zoom-out so free-cam pan still has floors.
                const float viewTiles = 56f;
                var viewR2 = viewTiles * viewTiles;

                // IconSmooth occupancy: parent/grid-local lookup matching PC Snapgrid.
                var smoothTiles = _smoothTilesScratch;
                smoothTiles.Clear();
                var smoothByEnt = _smoothByEntScratch;
                smoothByEnt.Clear();
                foreach (var (ent, xf0) in _xforms)
                {
                    if (!xf0.ParentID.IsValid() || _grids.ContainsKey(ent))
                        continue;

                    var wp0 = ResolveWorldPos(ent);
                    var dx0 = wp0.X - worldEye.X;
                    var dy0 = wp0.Y - worldEye.Y;
                    if (dx0 * dx0 + dy0 * dy0 > viewR2 * 1.35f)
                        continue;

                    _prototypes.TryGetValue(ent, out var proto0);
                    _sprites.TryGetValue(ent, out var spr0);
                    var path0 = spr0?.Path;
                    if (string.IsNullOrEmpty(path0) && proto0 is not null)
                        path0 = _protos?.TryGetSprite(proto0);
                    if (string.IsNullOrEmpty(path0)
                        || !TryResolveIconSmooth(proto0, path0, out var smooth0)
                        || smooth0.Mode == IconSmoothMode.NoSprite)
                        continue;

                    var tx0 = (int)MathF.Floor(xf0.LocalPosition.X);
                    var ty0 = (int)MathF.Floor(xf0.LocalPosition.Y);
                    var parentKey = xf0.ParentID.Id;
                    smoothTiles.Add((parentKey, tx0, ty0, smooth0.Key));
                    var depth0 = spr0 is { FromNetwork: true, HasDrawDepth: true }
                        ? spr0.DrawDepth
                        : ClassifyDepth(path0, spr0?.DrawDepth ?? GameStateDecoder.GuessDepth(path0), proto0);
                    smoothByEnt[ent] = (smooth0, path0!, depth0, parentKey, tx0, ty0);
                }

                const int maxCornerSmooth = 480;
                var cornerSmoothCount = 0;
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

                    // Container occlusion: hide contents of closed lockers/crates.
                    if (!isCtrl && IsHiddenByContainer(ent, xf))
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
                        var key = smoothEnt.Data.Key;
                        var bas = smoothEnt.Data.StateBase;
                        bool Has(int ox, int oy) => smoothTiles.Contains((parent, tx + ox, ty + oy, key));

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
                            var d = Has(1, 0) && Has(1, -1) && Has(0, -1) ? 1 : 0;
                            drawList.Add(new WorldEntityDraw(
                                ent, wp.X, wp.Y, worldRot, path, r, g, b, false, depth,
                                $"{bas}{d}", true, 0, 0, true, null, -1, isGhost));
                        }
                        else if (cornerSmoothCount < maxCornerSmooth)
                        {
                            var n = Has(0, 1);
                            var ne = Has(1, 1);
                            var e = Has(1, 0);
                            var se = Has(1, -1);
                            var s = Has(0, -1);
                            var sw = Has(-1, -1);
                            var w = Has(-1, 0);
                            var nw = Has(-1, 1);

                            byte cNE = 0, cNW = 0, cSW = 0, cSE = 0;
                            if (n) { cNE |= 1; cNW |= 4; }
                            if (ne) cNE |= 2;
                            if (e) { cNE |= 4; cSE |= 1; }
                            if (se) cSE |= 2;
                            if (s) { cSE |= 4; cSW |= 1; }
                            if (sw) cSW |= 2;
                            if (w) { cSW |= 4; cNW |= 1; }
                            if (nw) cNW |= 2;

                            void AddCorner(byte fill, int dir) =>
                                drawList.Add(new WorldEntityDraw(
                                    ent, wp.X, wp.Y, worldRot, path, r, g, b, false, depth,
                                    $"{bas}{fill}", true, 0, 0, true, null, dir, isGhost));

                            AddCorner(cSE, 2);
                            AddCorner(cNE, 1);
                            AddCorner(cNW, 3);
                            AddCorner(cSW, 0);
                            cornerSmoothCount++;
                        }
                        else
                        {
                            drawList.Add(new WorldEntityDraw(
                                ent, wp.X, wp.Y, worldRot, path, r, g, b, false, depth,
                                "full", true, 0, 0, true, null, -1, isGhost));
                        }

                        sum += wp;
                        nSum++;
                        continue;
                    }

                    // Non-mob structures without network layers: YAML sprite+state fill-in.
                    // Airlocks/doors/machines keep networked layers below (open/closed etc.).
                    if (!isCtrl && !IsPlayerLike(protoId, spr)
                        && spr is not { FromNetwork: true, Layers.Count: > 0 })
                    {
                        var yamlPath = !string.IsNullOrEmpty(protoId) ? _protos?.TryGetSprite(protoId) : null;
                        var yamlState = !string.IsNullOrEmpty(protoId) ? _protos?.TryGetState(protoId) : null;
                        var path = (!string.IsNullOrEmpty(spr?.Path) ? spr!.Path : null) ?? yamlPath;
                        if (string.IsNullOrEmpty(path) && spr?.Layers is { Count: > 0 })
                            path = spr.Layers[0].Path ?? spr.Path;
                        if (!string.IsNullOrEmpty(path))
                        {
                            var stateName = (!string.IsNullOrEmpty(spr?.State) ? spr!.State : null)
                                            ?? yamlState
                                            ?? (spr?.Layers is { Count: > 0 } ? spr.Layers[0].State : null)
                                            ?? DefaultSpriteState(path, protoId);
                            byte r = 255, g = 255, b = 255;
                            if (spr is { HasColor: true })
                            {
                                r = spr.R;
                                g = spr.G;
                                b = spr.B;
                            }

                            var depth = spr is { FromNetwork: true, HasDrawDepth: true }
                                ? spr.DrawDepth
                                : ClassifyDepth(path, spr?.DrawDepth ?? GameStateDecoder.GuessDepth(path), protoId);
                            if (!IsHiddenFromDefaultEye(protoId, path, depth, spr?.FromNetwork == true))
                            {
                                drawList.Add(new WorldEntityDraw(
                                    ent, wp.X, wp.Y, worldRot, path, r, g, b, false, depth,
                                    stateName, true, 0, 0, spr?.NoRotation == true, null, -1, isGhost));
                                sum += wp;
                                nSum++;
                                continue;
                            }
                        }
                    }

                    var layersAdded = 0;
                    if (spr?.Layers is { Count: > 0 })
                    {
                        // Mobs: full clothing stack. Doors/airlocks/machines: a few layers.
                        var maxLayers = IsPlayerLike(protoId, spr) || isCtrl
                            ? MaxLayersPerEntity
                            : (LooksLikeDoorOrMachine(protoId, spr.Path) ? 4 : 2);
                        var baseDepth = spr.FromNetwork && spr.HasDrawDepth
                            ? spr.DrawDepth
                            : ClassifyDepth(spr.Path, spr.DrawDepth != 0 ? spr.DrawDepth : GameStateDecoder.GuessDepth(spr.Path), protoId);

                        foreach (var layer in spr.Layers)
                        {
                            if (layersAdded >= maxLayers) break;
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

                            var layerState = layer.State ?? spr.State
                                             ?? (isCtrl ? "animated" : DefaultSpriteState(path, protoId));
                            // Layer offset is in local entity space → world via rotation.
                            var ox = layer.OffsetX;
                            var oy = layer.OffsetY;
                            var cos = MathF.Cos(worldRot);
                            var sin = MathF.Sin(worldRot);
                            var wxL = wp.X + ox * cos - oy * sin;
                            var wyL = wp.Y + ox * sin + oy * cos;
                            // Stable stack order within same DrawDepth.
                            var sortDepth = layerDepth * 16 + layersAdded;
                            drawList.Add(new WorldEntityDraw(
                                ent, wxL, wyL, worldRot,
                                path, lr, lg, lb, isCtrl, sortDepth, layerState,
                                true, 0, 0, spr.NoRotation || isCtrl, null, -1, isGhost));
                            layersAdded++;
                        }
                    }

                    if (layersAdded == 0)
                    {
                        byte r = spr?.R ?? 0, g = spr?.G ?? 0, b = spr?.B ?? 0;
                        var path = spr?.Path;
                        var stateName = spr?.State;
                        if (string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(protoId)
                            && spr is not { FromNetwork: true })
                            path = _protos?.TryGetSprite(protoId);

                        // Structures with empty network layers: fill from YAML prototype.
                        if (string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(protoId)
                            && IsStructureProto(protoId))
                            path = _protos?.TryGetSprite(protoId);

                        if (isCtrl)
                        {
                            path = _protos?.TryGetSprite("MobObserver")
                                   ?? _protos?.TryGetSprite("MobObserverBase")
                                   ?? path
                                   ?? "Mobs/Ghosts/ghost_human.rsi";
                            if (string.IsNullOrEmpty(path) || path.Contains("null", StringComparison.OrdinalIgnoreCase))
                                path = "Mobs/Ghosts/ghost_human.rsi";
                            stateName = "animated";
                            r = 255;
                            g = 248;
                            b = 240;
                            isGhost = true;
                        }

                        stateName ??= DefaultSpriteState(path, protoId);

                        if (spr is not { HasColor: true } && r == 0 && g == 0 && b == 0)
                        {
                            if (isCtrl) { r = 255; g = 248; b = 240; }
                            else if (!string.IsNullOrEmpty(path)) { r = 255; g = 255; b = 255; }
                            else if (!string.IsNullOrEmpty(protoId)) { r = 140; g = 160; b = 120; }
                            else { continue; }
                        }

                        var depth = isCtrl
                            ? 100
                            : (spr is { FromNetwork: true, HasDrawDepth: true }
                                ? spr.DrawDepth
                                : ClassifyDepth(path, spr?.DrawDepth ?? GameStateDecoder.GuessDepth(path), protoId));
                        drawList.Add(new WorldEntityDraw(
                            ent, wp.X, wp.Y, worldRot,
                            path, r, g, b, isCtrl, depth, stateName,
                            true, 0, 0, spr?.NoRotation == true || isCtrl
                                || (protoId?.Contains("Observer", StringComparison.OrdinalIgnoreCase) ?? false)
                                || (protoId?.Contains("Ghost", StringComparison.OrdinalIgnoreCase) ?? false),
                            null, -1, isGhost));
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
                // Prefer entities near the eye if still over budget (avoid LINQ alloc thrash).
                if (drawList.Count > MaxDrawEntities)
                {
                    drawList.Sort((a, b) =>
                    {
                        var da = Dist2(a.X, a.Y, focus.X, focus.Y);
                        var db = Dist2(b.X, b.Y, focus.X, focus.Y);
                        if (a.IsControlled != b.IsControlled)
                            return a.IsControlled ? -1 : 1;
                        return da.CompareTo(db);
                    });
                    drawList.RemoveRange(MaxDrawEntities, drawList.Count - MaxDrawEntities);
                }

                // Stable depth sort preserves multi-layer clothing order.
                drawList.Sort((a, b) =>
                {
                    var c = a.DrawDepth.CompareTo(b.DrawDepth);
                    if (c != 0) return c;
                    c = a.Y.CompareTo(b.Y);
                    if (c != 0) return c;
                    return a.Entity.Id.CompareTo(b.Entity.Id);
                });

                var tiles = BuildTileDrawList(focus, viewTiles, eyeMap);
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
                world = new WorldSnapshot(
                    eye,
                    drawList.ToArray(),
                    state.ToSequence,
                    detail,
                    tiles,
                    audio);
                error = detail;
                return drawList.Count > 0 || tiles.Count > 0 || audio.Count > 0
                       || foundXform || foundEye || entities.Count > 0;
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

            error = detail;
            return false;
        }
    }

    void TryAttachPrototypeSprite(NetEntity ent, string prototypeId)
    {
        if (_sprites.TryGetValue(ent, out var existing) && existing.FromNetwork)
            return;

        var path = _protos?.TryGetSprite(prototypeId);
        if (string.IsNullOrWhiteSpace(path))
            return;
        if (existing is not null && !string.IsNullOrEmpty(existing.Path))
        {
            if (string.IsNullOrEmpty(existing.State))
                existing.State = _protos?.TryGetState(prototypeId) ?? DefaultSpriteState(existing.Path, prototypeId);
            return;
        }

        _sprites[ent] = new GameStateDecoder.SpriteVisual
        {
            FromNetwork = false,
            Path = path,
            State = _protos?.TryGetState(prototypeId) ?? DefaultSpriteState(path, prototypeId),
            R = 255,
            G = 255,
            B = 255,
            DrawDepth = ClassifyDepth(path, GameStateDecoder.GuessDepth(path), prototypeId),
            NoRotation = prototypeId.Contains("Observer", StringComparison.OrdinalIgnoreCase)
                         || prototypeId.Contains("Ghost", StringComparison.OrdinalIgnoreCase)
                         || path.Contains("Ghost", StringComparison.OrdinalIgnoreCase),
        };
        PrototypeHits++;
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
        if (p.Contains("SpawnPoint", StringComparison.OrdinalIgnoreCase)
            || p.Contains("SpawnMarker", StringComparison.OrdinalIgnoreCase)
            || p.Contains("TimedSpawner", StringComparison.OrdinalIgnoreCase)
            || p.Contains("WarpPoint", StringComparison.OrdinalIgnoreCase)
            || p.Contains("MarkerBase", StringComparison.OrdinalIgnoreCase)
            || p.Contains("/Markers/", StringComparison.OrdinalIgnoreCase)
            || p.Contains("/Spawners/", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Spawners/", StringComparison.OrdinalIgnoreCase)
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
        // Mobile ghost viewport: IconSmooth ONLY for station walls + windows.
        // Floors, grilles, tables, lockers, pipes, etc. must use normal Sprite states.
        if (!IsWallOrWindowForSmooth(proto, path))
            return false;

        var fromProto = _protos?.TryGetIconSmooth(proto);
        if (fromProto is { } sm && sm.Mode != IconSmoothMode.NoSprite)
        {
            // Keep YAML key/base/mode when present (PC IconSmoothComponent).
            data = sm;
            return true;
        }

        var p = path ?? "";
        if (IsWindowForSmooth(proto, p))
        {
            data = new IconSmoothData("windows", "window", IconSmoothMode.Corners);
            return true;
        }

        // Real wall RSI / proto only.
        data = new IconSmoothData("walls", GuessSmoothBase(p, "wall"), IconSmoothMode.Corners);
        return true;
    }

    static bool IsWallOrWindowForSmooth(string? proto, string? path)
        => IsWindowForSmooth(proto, path) || IsWallForSmooth(proto, path);

    static bool IsWindowForSmooth(string? proto, string? path)
    {
        var protoHit = proto ?? "";
        var p = path ?? "";
        if (protoHit.Contains("Window", StringComparison.OrdinalIgnoreCase)
            && !protoHit.Contains("Wallmount", StringComparison.OrdinalIgnoreCase))
            return true;
        return p.Contains("/Windows/", StringComparison.OrdinalIgnoreCase)
               || p.Contains("Structures/Windows", StringComparison.OrdinalIgnoreCase);
    }

    static bool IsWallForSmooth(string? proto, string? path)
    {
        var protoHit = proto ?? "";
        var p = path ?? "";

        // Path is authoritative — only Structures/Walls/*.rsi
        if (p.Contains("/Walls/", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Structures/Walls", StringComparison.OrdinalIgnoreCase))
        {
            // Exclude wall-mounted furniture that lives under odd paths.
            if (p.Contains("Wallmount", StringComparison.OrdinalIgnoreCase)
                || p.Contains("Closet", StringComparison.OrdinalIgnoreCase)
                || p.Contains("Locker", StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }

        // Proto id fallback — strict wall entities only (not WallCloset / WallLocker / …).
        if (string.IsNullOrEmpty(protoHit) || !protoHit.Contains("Wall", StringComparison.OrdinalIgnoreCase))
            return false;
        if (protoHit.Contains("Window", StringComparison.OrdinalIgnoreCase))
            return false;
        if (protoHit.Contains("Wallmount", StringComparison.OrdinalIgnoreCase)
            || protoHit.Contains("WallLight", StringComparison.OrdinalIgnoreCase)
            || protoHit.Contains("WallTerminal", StringComparison.OrdinalIgnoreCase)
            || protoHit.Contains("WallCabinet", StringComparison.OrdinalIgnoreCase)
            || protoHit.Contains("WallCloset", StringComparison.OrdinalIgnoreCase)
            || protoHit.Contains("WallLocker", StringComparison.OrdinalIgnoreCase)
            || protoHit.Contains("Closet", StringComparison.OrdinalIgnoreCase)
            || protoHit.Contains("Locker", StringComparison.OrdinalIgnoreCase)
            || protoHit.Contains("Cabinet", StringComparison.OrdinalIgnoreCase)
            || protoHit.Contains("Fireplace", StringComparison.OrdinalIgnoreCase)
            || protoHit.Contains("Shelf", StringComparison.OrdinalIgnoreCase)
            || protoHit.Contains("Rack", StringComparison.OrdinalIgnoreCase)
            || protoHit.Contains("Girder", StringComparison.OrdinalIgnoreCase)
            || protoHit.Contains("Grille", StringComparison.OrdinalIgnoreCase))
            return false;

        // Prefer names that look like wall tiles: WallSolid, ReinforcedWall, …
        return protoHit.Contains("ReinforcedWall", StringComparison.OrdinalIgnoreCase)
               || protoHit.StartsWith("Wall", StringComparison.OrdinalIgnoreCase)
               || protoHit.EndsWith("Wall", StringComparison.OrdinalIgnoreCase)
               || protoHit.Contains("WallSolid", StringComparison.OrdinalIgnoreCase)
               || protoHit.Contains("WallRock", StringComparison.OrdinalIgnoreCase)
               || protoHit.Contains("WallShuttle", StringComparison.OrdinalIgnoreCase);
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

    static float Dist2(float x, float y, float fx, float fy)
    {
        var dx = x - fx;
        var dy = y - fy;
        return dx * dx + dy * dy;
    }

    static string GuessSmoothBase(string path, string fallback)
    {
        // Window RSIs almost always use stateBase "window" (YAML IconSmooth.base), not the file name.
        if (fallback.Equals("window", StringComparison.OrdinalIgnoreCase))
            return "window";

        try
        {
            var name = Path.GetFileNameWithoutExtension(path.Replace('\\', '/'));
            if (name.EndsWith(".rsi", StringComparison.OrdinalIgnoreCase))
                name = name[..^4];
            if (!string.IsNullOrWhiteSpace(name))
                return name;
        }
        catch
        {
            /* ignore */
        }

        return fallback;
    }

    string? DefaultSpriteState(string? path, string? proto)
    {
        var fromProto = _protos?.TryGetState(proto);
        if (!string.IsNullOrWhiteSpace(fromProto))
            return fromProto;

        var p = (path ?? "") + " " + (proto ?? "");
        // IconSmooth walls: Sprite has no state until client IconSmooth; RSI icon state is "full".
        if (p.Contains("Wall", StringComparison.OrdinalIgnoreCase)
            || p.Contains("/Walls/", StringComparison.OrdinalIgnoreCase))
            return "full";
        if (p.Contains("Window", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Grille", StringComparison.OrdinalIgnoreCase))
            return "full";
        if (p.Contains("Ghost", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Observer", StringComparison.OrdinalIgnoreCase))
            return "animated";
        return null;
    }

    List<WorldTileDraw> BuildTileDrawList(Vector2 focus, float viewTiles, NetEntity eyeMap)
    {
        var list = new List<WorldTileDraw>(Math.Min(MaxDrawTiles, 2048));
        var minX = focus.X - viewTiles;
        var maxX = focus.X + viewTiles;
        var minY = focus.Y - viewTiles;
        var maxY = focus.Y + viewTiles;
        foreach (var (gridEnt, chunks) in _grids)
        {
            if (list.Count >= MaxDrawTiles) break;
            if (eyeMap.IsValid())
            {
                var gridMap = ResolveMapUid(gridEnt);
                if (gridMap.IsValid() && gridMap != eyeMap)
                    continue;
            }

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
                    // Variant UV: prefer numeric state from tile variant when present.
                    string? state = null;
                    try
                    {
                        // Robust Tile may expose Variant / Flags — best-effort.
                        var tt = tile.GetType();
                        var variant = tt.GetProperty("Variant")?.GetValue(tile)
                                      ?? tt.GetField("Variant")?.GetValue(tile);
                        if (variant is byte vb)
                            state = vb.ToString();
                        else if (variant is int vi)
                            state = vi.ToString();
                    }
                    catch { /* ignore */ }

                    // Textured floors: white modulate (pastel only for missing art).
                    if (!string.IsNullOrEmpty(rsi))
                    {
                        r = 255;
                        g = 255;
                        b = 255;
                    }

                    list.Add(new WorldTileDraw(
                        world.X, world.Y, r, g, b, rsi, state,
                        tile.Variant, tile.RotationMirroring, (float)gridRot.Theta));
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

    static void ColorForTile(int typeId, out byte r, out byte g, out byte b)
    {
        // Stable pastel-ish floor colours from type id — never pure black (missing-tile holes).
        unchecked
        {
            var h = (uint)typeId * 2654435761u;
            r = (byte)(90 + (h & 0x6F));
            g = (byte)(95 + ((h >> 8) & 0x5F));
            b = (byte)(105 + ((h >> 16) & 0x4F));
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
        for (var depth = 0; depth < 24; depth++)
        {
            if (_mapEntities.Contains(cur))
            {
                found = cur;
                break;
            }

            if (!_xforms.TryGetValue(cur, out var t)) break;
            if (!visiting.Add(cur)) break;
            if (!t.ParentID.IsValid() || t.ParentID == cur)
            {
                // Root with no MapComponent marked — treat as map-like root.
                found = cur;
                break;
            }

            cur = t.ParentID;
        }

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

    /// <summary>World rotation of the grid under an entity, snapped to cardinals (no diagonals).</summary>
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
