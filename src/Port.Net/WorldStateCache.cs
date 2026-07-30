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
    const int MaxDrawEntities = 4500;
    const int MaxDrawTiles = 12000;
    const int MaxLayersPerEntity = 8;
    const int ChunkDefault = 16;

    readonly Dictionary<NetEntity, TransformComponentState> _xforms = new();
    readonly Dictionary<NetEntity, GameStateDecoder.SpriteVisual> _sprites = new();
    readonly Dictionary<NetEntity, string> _prototypes = new();
    readonly HashSet<NetEntity> _hiddenFromEye = new();
    readonly ContainerOcclusionTracker _containers = new();
    readonly Dictionary<NetEntity, Dictionary<Vector2i, ChunkDatum>> _grids = new();
    readonly Dictionary<NetEntity, ushort> _gridChunkSize = new();
    readonly Dictionary<NetEntity, Vector2> _worldPosCache = new();
    /// <summary>Entities that have MapComponent — MapUid for GetWorldPosition / draw filter.</summary>
    readonly HashSet<NetEntity> _mapEntities = new();
    readonly Dictionary<NetEntity, NetEntity> _mapUidCache = new();

    PrototypeSpriteIndex? _protos;
    TilePrototypeIndex? _tiles;

    public int XformCount { get { lock (this) return _xforms.Count; } }
    public int SpriteCount { get { lock (this) return _sprites.Count; } }
    public int TileChunkCount { get { lock (this) return _grids.Sum(g => g.Value.Count); } }
    public int PrototypeHits { get; private set; }

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
                _hiddenFromEye.Remove(del);
                _containers.Remove(del);
                _grids.Remove(del);
                _gridChunkSize.Remove(del);
                _worldPosCache.Remove(del);
                _mapEntities.Remove(del);
                _mapUidCache.Clear();
            }
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
            _hiddenFromEye.Clear();
            _containers.Clear();
            _grids.Clear();
            _gridChunkSize.Clear();
            _worldPosCache.Clear();
            _mapEntities.Clear();
            _mapUidCache.Clear();
            PrototypeHits = 0;
        }
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
                    _hiddenFromEye.Clear();
                    _containers.Clear();
                    _grids.Clear();
                    _gridChunkSize.Clear();
                    _worldPosCache.Clear();
                    _mapEntities.Clear();
                    _mapUidCache.Clear();
                    PrototypeHits = 0;
                }

                foreach (var del in deletions)
                {
                    _xforms.Remove(del);
                    _sprites.Remove(del);
                    _prototypes.Remove(del);
                    _hiddenFromEye.Remove(del);
                    _containers.Remove(del);
                    _grids.Remove(del);
                    _gridChunkSize.Remove(del);
                    _worldPosCache.Remove(del);
                    _mapEntities.Remove(del);
                }

                if (deletions.Count > 0)
                    _mapUidCache.Clear();

                var packetMaps = 0;

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
                                _worldPosCache.Clear();
                                _mapUidCache.Clear();
                                continue;
                            }

                            _xforms[es.NetEntity] = xf;
                            // Parent/grid motion invalidates all descendant world caches.
                            _worldPosCache.Clear();
                            _mapUidCache.Clear();
                            packetXforms++;
                            continue;
                        }

                        if (change.State is MetaDataComponentState meta)
                        {
                            packetMeta++;
                            if (!string.IsNullOrWhiteSpace(meta.PrototypeId))
                            {
                                _prototypes[es.NetEntity] = meta.PrototypeId!;
                                // Only attach prototype art if we have no network SpriteComponent yet.
                                if (!_sprites.TryGetValue(es.NetEntity, out var existing) || !existing.FromNetwork)
                                    TryAttachPrototypeSprite(es.NetEntity, meta.PrototypeId!);
                            }

                            if (IsHiddenFromDefaultEye(meta))
                                _hiddenFromEye.Add(es.NetEntity);
                            else
                                _hiddenFromEye.Remove(es.NetEntity);
                            continue;
                        }

                        if (_containers.TryApplyComponentState(es.NetEntity, change.State))
                            continue;

                        // MapComponent — marks MapUid roots so we can separate stacked maps.
                        if (change.State is MapComponentState
                            || change.State.GetType().Name.Contains("MapComponentState", StringComparison.Ordinal))
                        {
                            _mapEntities.Add(es.NetEntity);
                            _mapUidCache.Clear();
                            _worldPosCache.Clear();
                            packetMaps++;
                            continue;
                        }

                        if (TryApplyGrid(es.NetEntity, change.State))
                        {
                            packetGrids++;
                            continue;
                        }

                        var before = _sprites.Count;
                        GameStateDecoder.TryExtractSpritePublic(change.State, es.NetEntity, _sprites);
                        if (_sprites.Count != before || _sprites.ContainsKey(es.NetEntity))
                            packetSprites++;
                    }
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
                    controlled = c;

                // Camera + tile cull MUST use world coords — LocalPosition is grid-relative.
                Vector2 worldEye = default;
                Angle rot = default;
                Vector2 eyeOff = default;
                var foundXform = false;
                var foundEye = false;
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
                            if (t.GetProperty("Offset")?.GetValue(change.State) is Vector2 vo) eyeOff = vo;
                            else if (t.GetField("Offset")?.GetValue(change.State) is Vector2 vo2) eyeOff = vo2;
                            foundEye = true;
                        }
                        break;
                    }
                }

                var drawList = new List<WorldEntityDraw>(Math.Min(_xforms.Count * 2, MaxDrawEntities * 2));
                Vector2 sum = default;
                var nSum = 0;
                // Viewport stream: only draw near the eye (+ margin). Far store kept until LeavePvs.
                const float viewTiles = 48f;
                var viewR2 = viewTiles * viewTiles;

                foreach (var (ent, xf) in _xforms)
                {
                    if (_grids.ContainsKey(ent) && !_sprites.ContainsKey(ent) && !_prototypes.ContainsKey(ent))
                        continue; // map-grid entity without sprite — tiles drawn separately

                    var isCtrl = controlled.IsValid() && ent == controlled;
                    if (!isCtrl && _containers.IsOccluded(ent))
                        continue;
                    if (!isCtrl && _hiddenFromEye.Contains(ent))
                        continue;
                    // Critical: only the eye's map — otherwise stations/asteroids stack in one space.
                    if (!isCtrl && eyeMap.IsValid())
                    {
                        var entMap = ResolveMapUid(ent);
                        if (entMap.IsValid() && entMap != eyeMap)
                            continue;
                    }

                    var wp = ResolveWorldPos(ent);
                    if (!isCtrl && foundXform)
                    {
                        var dx = wp.X - worldEye.X;
                        var dy = wp.Y - worldEye.Y;
                        if (dx * dx + dy * dy > viewR2)
                            continue;
                    }

                    _sprites.TryGetValue(ent, out var spr);
                    var worldRot = (float)ResolveWorldRot(ent);
                    _prototypes.TryGetValue(ent, out var protoId);

                    var layersAdded = 0;
                    if (spr?.Layers is { Count: > 0 })
                    {
                        foreach (var layer in spr.Layers)
                        {
                            if (layersAdded >= MaxLayersPerEntity)
                                break;
                            if (!layer.Visible) continue;
                            // Network layers must not inherit a prototype RSI path.
                            var path = layer.Path
                                       ?? (spr.FromNetwork ? spr.Path : null)
                                       ?? spr.Path;
                            if (string.IsNullOrEmpty(path) && !isCtrl) continue;
                            var depth = spr.FromNetwork && spr.DrawDepth != 0
                                ? (layer.Depth != 0 ? layer.Depth : spr.DrawDepth)
                                : ClassifyDepth(path, layer.Depth != 0 ? layer.Depth : (spr.DrawDepth != 0 ? spr.DrawDepth : GameStateDecoder.GuessDepth(path)), protoId);
                            byte lr = layer.R, lg = layer.G, lb = layer.B;
                            if (lr == 0 && lg == 0 && lb == 0) { lr = 255; lg = 255; lb = 255; }
                            var layerState = layer.State ?? spr.State ?? DefaultSpriteState(path, protoId);
                            if (ShouldHideSubfloor(path, protoId, layerState))
                                continue;
                            // Layer offset is in local entity space → world via rotation.
                            var ox = layer.OffsetX;
                            var oy = layer.OffsetY;
                            var cos = MathF.Cos(worldRot);
                            var sin = MathF.Sin(worldRot);
                            var wxL = wp.X + ox * cos - oy * sin;
                            var wyL = wp.Y + ox * sin + oy * cos;
                            drawList.Add(new WorldEntityDraw(
                                ent, wxL, wyL, worldRot,
                                path, lr, lg, lb, isCtrl, depth, layerState,
                                true, 0, 0, spr.NoRotation));
                            layersAdded++;
                        }
                    }

                    if (layersAdded == 0)
                    {
                        byte r = spr?.R ?? 0, g = spr?.G ?? 0, b = spr?.B ?? 0;
                        var path = spr?.Path;
                        var stateName = spr?.State;
                        // Prototype fill only when no network SpriteComponent at all.
                        if (string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(protoId)
                            && spr is not { FromNetwork: true })
                            path = _protos?.TryGetSprite(protoId);

                        if (!isCtrl && ShouldHideSubfloor(path, protoId, stateName))
                            continue;

                        // Doors/airlocks/lockers: YAML fill when network sent no layers.
                        if (string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(protoId))
                            path = _protos?.TryGetSprite(protoId);

                        if (isCtrl)
                        {
                            // Observer: use replicated sprite when present; else canonical ghost RSI.
                            if (string.IsNullOrEmpty(path))
                            {
                                path = _protos?.TryGetSprite(protoId)
                                       ?? _protos?.TryGetSprite("MobObserver")
                                       ?? _protos?.TryGetSprite("MobObserverBase")
                                       ?? "Mobs/Ghosts/ghost_human.rsi";
                            }

                            stateName ??= "animated";
                            if (r < 180) { r = 255; g = 248; b = 240; }
                        }

                        stateName ??= DefaultSpriteState(path, protoId);

                        if (r == 0 && g == 0 && b == 0)
                        {
                            if (isCtrl) { r = 255; g = 248; b = 240; }
                            else if (!string.IsNullOrEmpty(path)) { r = 255; g = 255; b = 255; }
                            else if (!string.IsNullOrEmpty(protoId)) { r = 140; g = 160; b = 120; }
                            else { continue; }
                        }

                        var depth = isCtrl
                            ? 90
                            : (spr is { FromNetwork: true, DrawDepth: not 0 }
                                ? spr.DrawDepth
                                : ClassifyDepth(path, spr?.DrawDepth ?? GameStateDecoder.GuessDepth(path), protoId));
                        drawList.Add(new WorldEntityDraw(
                            ent, wp.X, wp.Y, worldRot,
                            path, r, g, b, isCtrl, depth, stateName,
                            true, 0, 0, spr?.NoRotation == true
                                || (protoId?.Contains("Observer", StringComparison.OrdinalIgnoreCase) ?? false)
                                || (protoId?.Contains("Ghost", StringComparison.OrdinalIgnoreCase) ?? false)));
                    }

                    sum += wp;
                    nSum++;
                }

                if (!foundXform && nSum > 0)
                    worldEye = sum / nSum;

                var focus = worldEye;
                // Prefer entities near the eye if still over budget.
                if (drawList.Count > MaxDrawEntities)
                {
                    drawList = drawList
                        .Select(e =>
                        {
                            var dx = e.X - focus.X;
                            var dy = e.Y - focus.Y;
                            var d2 = dx * dx + dy * dy;
                            return (e, d2, near: d2 <= viewR2 || e.IsControlled);
                        })
                        .OrderByDescending(t => t.near)
                        .ThenBy(t => t.d2)
                        .Take(MaxDrawEntities)
                        .Select(t => t.e)
                        .ToList();
                }

                drawList.Sort((a, b) =>
                {
                    var d = a.DrawDepth.CompareTo(b.DrawDepth);
                    if (d != 0) return d;
                    var y = a.Y.CompareTo(b.Y);
                    if (y != 0) return y;
                    return b.IsControlled.CompareTo(a.IsControlled);
                });

                IconSmoothProcessor.Apply(
                    drawList,
                    ent => _prototypes.TryGetValue(ent, out var p) ? p : null,
                    proto => _protos?.IsIconSmooth(proto) == true);

                var tiles = BuildTileDrawList(focus, viewTiles, eyeMap);

                var detail =
                    $"from={state.FromSequence.Value} ents={entities.Count} " +
                    $"Δxf={packetXforms} Δspr={packetSprites} Δmeta={packetMeta} Δgrid={packetGrids} Δmap={packetMaps} " +
                    $"store={_xforms.Count}/{_sprites.Count} maps={_mapEntities.Count} tiles={tiles.Count} draw={drawList.Count} " +
                    $"eyeMap={(eyeMap.IsValid() ? eyeMap.ToString() : "-")} " +
                    $"ctrl={(controlled.IsValid() ? controlled.ToString() : "-")}";

                eye = new EyeSnapshot(
                    controlled, worldEye, rot, eyeOff, true,
                    entities.Count, players.Count, state.ToSequence, detail);
                world = new WorldSnapshot(eye, drawList, state.ToSequence, detail, tiles);
                error = detail;
                return drawList.Count > 0 || tiles.Count > 0 || foundXform || foundEye || entities.Count > 0;
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
        if (p.Contains("Tiles", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Floor", StringComparison.OrdinalIgnoreCase)
            || p.Contains("plating", StringComparison.OrdinalIgnoreCase))
            return 0;
        if (p.Contains("Wall", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Grille", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Window", StringComparison.OrdinalIgnoreCase))
            return 20;
        if (p.Contains("Cable", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Wire", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Apc", StringComparison.OrdinalIgnoreCase))
            return 30;
        if (p.Contains("Pipe", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Atmos", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Disposal", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Vent", StringComparison.OrdinalIgnoreCase))
            return 35;
        if (p.Contains("Airlock", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Door", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Windoor", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Firelock", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Shutter", StringComparison.OrdinalIgnoreCase))
            return 45;
        if (p.Contains("Mob", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Human", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Species", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Ghost", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Player", StringComparison.OrdinalIgnoreCase))
            return 70;
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
                    list.Add(new WorldTileDraw(world.X, world.Y, r, g, b, rsi));
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
        unchecked
        {
            var h = (uint)typeId * 2654435761u;
            r = (byte)(70 + (h & 0x7F));
            g = (byte)(80 + ((h >> 8) & 0x6F));
            b = (byte)(90 + ((h >> 16) & 0x5F));
        }
    }

    static bool IsHiddenFromDefaultEye(MetaDataComponentState meta)
    {
        try
        {
            var t = meta.GetType();
            var flags = t.GetProperty("Flags")?.GetValue(meta)
                        ?? t.GetField("Flags")?.GetValue(meta);
            if (flags is Enum e)
            {
                var name = e.ToString() ?? "";
                if (name.Contains("Hidden", StringComparison.OrdinalIgnoreCase))
                    return true;
                var val = Convert.ToInt32(e);
                if ((val & (1 << 2)) != 0)
                    return true;
            }
        }
        catch { /* ignore */ }

        return false;
    }

    static bool ShouldHideSubfloor(string? path, string? proto, string? state)
    {
        var p = (path ?? "") + " " + (proto ?? "") + " " + (state ?? "");
        if (p.Contains("Marker", StringComparison.OrdinalIgnoreCase)
            || p.Contains("SpawnPoint", StringComparison.OrdinalIgnoreCase))
            return true;
        if (p.Contains("Cable", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Wire", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Subfloor", StringComparison.OrdinalIgnoreCase)
            || p.Contains("FloorCable", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    Vector2 ResolveWorldPos(NetEntity ent)
    {
        if (_worldPosCache.TryGetValue(ent, out var cached))
            return cached;
        if (!_xforms.TryGetValue(ent, out _))
            return default;

        var mapUid = ResolveMapUid(ent);
        var visiting = new HashSet<NetEntity>();
        var cur = ent;
        var pos = Vector2.Zero;
        for (var depth = 0; depth < 24; depth++)
        {
            if (_mapEntities.Contains(cur) && cur != ent)
                break;
            if (!_xforms.TryGetValue(cur, out var t)) break;
            if (!visiting.Add(cur)) break;
            pos = t.Rotation.RotateVec(pos) + t.LocalPosition;
            if (!t.ParentID.IsValid() || t.ParentID == cur) break;
            if (mapUid.IsValid() && t.ParentID == mapUid)
                break;
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

        var visiting = new HashSet<NetEntity>();
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
        var visiting = new HashSet<NetEntity>();
        var cur = ent;
        Angle rot = default;
        for (var depth = 0; depth < 24; depth++)
        {
            if (!_xforms.TryGetValue(cur, out var t)) break;
            if (!visiting.Add(cur)) break;
            if (!t.NoLocalRotation)
                rot = t.Rotation + rot;
            if (!t.ParentID.IsValid() || t.ParentID == cur) break;
            cur = t.ParentID;
        }

        return (float)rot.Theta;
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
