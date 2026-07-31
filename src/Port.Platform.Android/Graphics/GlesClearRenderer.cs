using Android.Graphics;
using Android.Opengl;
using Java.Nio;
using Javax.Microedition.Khronos.Opengles;
using EGLConfig = Javax.Microedition.Khronos.Egl.EGLConfig;
using GLSurfaceView = Android.Opengl.GLSurfaceView;

namespace Port.Platform.Android.Graphics;

/// <summary>
/// GLES2 ghost viewport: camera + entity markers + optional textured RSI quads.
/// </summary>
public sealed class GlesClearRenderer : Java.Lang.Object, GLSurfaceView.IRenderer
{
    public struct EntitySprite
    {
        public float X;
        public float Y;
        public float Rotation;
        public string? RsiPath;
        public string? StateName;
        public int DrawDepth;
        public byte R, G, B;
        public bool IsControlled;
        public bool NoRotation;
        public string? Label;
        public int DirOverride;
        public float ScaleX;
        public float ScaleY;
        public float RotationOffset;
    }

    public struct TileSprite
    {
        public float X;
        public float Y;
        public byte R, G, B;
        public string? RsiPath;
        public string? StateName;
        public float Rotation;
        public byte Variant;
        public byte RotationMirroring;
    }

    public struct SpeechBubbleSprite
    {
        public float X;
        public float Y;
        public string Text;
        public int Argb;
        public float Alpha;
        public float StackOffset;
    }

    readonly object _gate = new();
    float _r = 0.04f, _g = 0.08f, _b = 0.16f;
    bool _pulse = true;
    bool _ghostMode;
    bool _fullbright;
    bool _drawFov = true;
    float _camX, _camY;
    float _camRot;
    float _zoom = 1f;
    long _frames;
    long _fpsWindowStartMs;
    int _fpsWindowFrames;
    float _fps;
    int _width;
    int _height;
    string _lastError = "";
    bool _ready;
    EntitySprite[] _entities = Array.Empty<EntitySprite>();
    EntitySprite[] _entitiesBack = Array.Empty<EntitySprite>();
    int _entityCount;
    TileSprite[] _tiles = Array.Empty<TileSprite>();
    TileSprite[] _tilesBack = Array.Empty<TileSprite>();
    int _tileCount;
    SpeechBubbleSprite[] _bubbles = Array.Empty<SpeechBubbleSprite>();
    SpeechBubbleSprite[] _bubblesBack = Array.Empty<SpeechBubbleSprite>();
    int _bubbleCount;
    string? _contentFilesRoot;
    Port.Content.AczOnDemandFetcher? _texFetcher;
    int _drawnLast;
    int _texturedLast;
    int _tilesDrawnLast;
    int _texMissLast;
    int _uvMissLast;
    int _rsiPathLast;
    int _texCached;
    int _pendingTexLast;
    int _loadFailLast;
    string _lastMissSample = "";
    long _lastDiagFrame;
    bool _fovEnabled = true;
    bool _lightingEnabled = true;
    float _ambientLight = 0.78f;

    int _program;
    int _aPos;
    int _aColor;
    int _uScreen;
    int _uCam;
    int _texProgram;
    int _texAPos;
    int _texAUv;
    int _texUScreen;
    int _texUSampler;
    int _texUTint;
    bool _glReady;

    readonly record struct TexEntry(
        int Id,
        float FrameW,
        float FrameH,
        float U0,
        float V0,
        float U1,
        float V1,
        string? AtlasKey,
        bool FolderMode,
        long LastUsedFrame = 0,
        bool Pinned = false,
        int AtlasPixelW = 0,
        int AtlasPixelH = 0);

    readonly Dictionary<string, TexEntry> _texCache = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Port.Content.RsiAtlas.Loaded> _atlasMeta = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, long> _texLastUsed = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _texNeeded = new(StringComparer.OrdinalIgnoreCase);
    readonly Queue<string> _pendingPngLoad = new();
    readonly Queue<string> _pendingRsiLoad = new();
    readonly Queue<string> _pendingTexLoad = new();
    readonly HashSet<string> _queuedTex = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, long> _texRetryAtFrame = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> _texEvictScratch = new();
    long _texBytes;

    /// <summary>
    /// Soft residency budget — must-loads always proceed (evict unpinned first).
    /// Hard pin of IconSmooth sheets filled the old 2k cache and walls vanished.
    /// </summary>
    const int MaxTexCache = 12_000;
    const long MaxTexBytes = 512L * 1024 * 1024;
    // PNG/atlas decode on the GL thread — keep budgets modest to avoid frame spikes.
    const int LoadsPerFrame = 8;
    const int LoadsPerFrameBurst = 16;
    long _burstLoadsUntilFrame;
    int _pendingBurstFrames; // set from any thread; consumed on GL thread
    readonly Dictionary<string, int> _texFailCount = new(StringComparer.OrdinalIgnoreCase);
    const string ParallaxLayerPath = "Textures/Parallaxes/layer1.png";
    const float ParallaxSlowness = 0.998046875f;
    readonly HashSet<string> _iconSmoothPrefetched = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, long> _recentlyVisibleUntil = new(StringComparer.OrdinalIgnoreCase);
    const int RecentlyVisibleFrames = 1800; // ~30s at 60fps

    // Clyde-style texture batching: one draw call per RSI bind.
    readonly List<TexQuad> _texQuads = new(2048);
    const int MaxBatchQuads = 1024;
    float[] _batchPos = new float[MaxBatchQuads * 12];
    float[] _batchUv = new float[MaxBatchQuads * 12];
    float[] _batchCol = new float[MaxBatchQuads * 24];
    FloatBuffer? _batchPosBuf;
    FloatBuffer? _batchUvBuf;
    FloatBuffer? _batchColBuf;
    int _texAColor;

    readonly record struct TexQuad(
        int TexId,
        int Depth,
        float SortY,
        float Sx,
        float Sy,
        float SizeX,
        float SizeY,
        float Rot,
        float R,
        float G,
        float B,
        float A,
        float U0,
        float V0,
        float U1,
        float V1);

    FloatBuffer? _posBuf;
    FloatBuffer? _colBuf;
    FloatBuffer? _uvBuf;
    float[] _posScratch = new float[MaxVerts * 2];
    float[] _colScratch = new float[MaxVerts * 4];
    float[] _uvScratch = new float[6 * 2];
    float[] _texPosScratch = new float[6 * 2];

    const int MaxEntities = 16_000;
    const int MaxTileSolidQuads = 24_000;
    const int MaxVerts = MaxTileSolidQuads * 6; // tiles + entity solid fallbacks share scratch
    const float PixelsPerTile = 32f;

    public long FrameCount
    {
        get { lock (_gate) return _frames; }
    }

    public float Fps
    {
        get { lock (_gate) return _fps; }
    }

    public int Width
    {
        get { lock (_gate) return _width; }
    }

    public int Height
    {
        get { lock (_gate) return _height; }
    }

    public bool IsReady
    {
        get { lock (_gate) return _ready; }
    }

    public string LastError
    {
        get { lock (_gate) return _lastError; }
    }

    public void SetClearColor(float r, float g, float b)
    {
        lock (_gate)
        {
            _r = r;
            _g = g;
            _b = b;
        }
    }

    public void SetPulse(bool enabled)
    {
        lock (_gate) _pulse = enabled;
    }

    public void SetGhostMode(bool enabled)
    {
        lock (_gate)
        {
            _ghostMode = enabled;
            if (enabled)
                _pulse = false;
        }
        // Do not QueueTexture here: UI thread races GL OnDrawFrame maps.
        // Parallax/ghost RSI are queued on the GL thread in DrawParallaxBackground / DrawEntities.
    }

    public void SetFullbright(bool enabled)
    {
        lock (_gate) _fullbright = enabled;
    }

    public void SetDrawFov(bool enabled)
    {
        lock (_gate) _drawFov = enabled;
    }

    public void SetCamera(float x, float y)
    {
        lock (_gate)
        {
            _camX = x;
            _camY = y;
        }
    }

    public void SetCameraRotation(float radians)
    {
        lock (_gate) _camRot = radians;
    }

    public void SetZoom(float zoom)
    {
        lock (_gate)
            _zoom = Math.Clamp(zoom, 0.35f, 3.5f);
    }

    public void SetFovEnabled(bool enabled)
    {
        lock (_gate) _fovEnabled = enabled;
    }

    public void SetLightingEnabled(bool enabled)
    {
        lock (_gate) _lightingEnabled = enabled;
    }

    public void SetAmbientLight(float ambient01)
    {
        lock (_gate) _ambientLight = Math.Clamp(ambient01, 0.15f, 1f);
    }

    public void SetContentFilesRoot(string? root)
    {
        lock (_gate) _contentFilesRoot = root;
    }

    public void SetTextureFetcher(Port.Content.AczOnDemandFetcher? fetcher)
    {
        lock (_gate) _texFetcher = fetcher;
    }

    public void SetEntities(EntitySprite[] entities, int count)
    {
        lock (_gate)
        {
            // Double-buffer: never Array.Copy into the array GL is currently reading.
            if (_entitiesBack.Length < count)
                _entitiesBack = new EntitySprite[Math.Max(count, 256)];
            if (count > 0)
                Array.Copy(entities, _entitiesBack, count);
            (_entities, _entitiesBack) = (_entitiesBack, _entities);
            _entityCount = count;
        }
    }

    public void SetTiles(TileSprite[] tiles, int count)
    {
        lock (_gate)
        {
            if (_tilesBack.Length < count)
                _tilesBack = new TileSprite[Math.Max(count, 512)];
            if (count > 0)
                Array.Copy(tiles, _tilesBack, count);
            (_tiles, _tilesBack) = (_tilesBack, _tiles);
            _tileCount = count;
        }
    }

    public void SetSpeechBubbles(SpeechBubbleSprite[] bubbles, int count)
    {
        lock (_gate)
        {
            if (_bubblesBack.Length < count)
                _bubblesBack = new SpeechBubbleSprite[Math.Max(count, 16)];
            if (count > 0)
                Array.Copy(bubbles, _bubblesBack, count);
            (_bubbles, _bubblesBack) = (_bubblesBack, _bubbles);
            _bubbleCount = count;
        }
    }

    public string Format()
    {
        lock (_gate)
        {
            if (!string.IsNullOrEmpty(_lastError))
                return $"gles: ERROR {_lastError}";
            if (!_ready)
                return "gles: waiting for surface";
            if (!_ghostMode)
                return $"gles: OK {_width}x{_height}";
            var miss = _texMissLast + _uvMissLast;
            var ratio = _rsiPathLast > 0 ? (100f * _texturedLast / _rsiPathLast) : 0f;
            return $"ents={_entityCount} tiles={_tileCount}/{_tilesDrawnLast} " +
                   $"texOK={_texturedLast}/{_rsiPathLast}({ratio:0}%) " +
                   $"missTex={_texMissLast} missUv={_uvMissLast} " +
                   $"cache={_texCached} pend={_pendingTexLast} retry={_loadFailLast}";
        }
    }

    /// <summary>Multi-line sprite/texture report for clipboard / debug panel.</summary>
    public string FormatDiag()
    {
        lock (_gate)
        {
            var sb = new System.Text.StringBuilder(512);
            sb.AppendLine(Format());
            if (!string.IsNullOrEmpty(_lastError))
                sb.AppendLine($"lastError: {_lastError}");
            sb.AppendLine($"contentRoot: {_contentFilesRoot ?? "(null)"}");
            sb.AppendLine($"fetcher: {(_texFetcher?.IsReady == true ? "ready" : "no")} " +
                          $"idx={_texFetcher?.IndexedRsicCount ?? 0}");
            sb.AppendLine($"fps={_fps:0.0} frames={_frames} zoom={_zoom:0.00} " +
                          $"cam=({_camX:0.0},{_camY:0.0})");
            if (!string.IsNullOrEmpty(_lastMissSample))
                sb.AppendLine($"lastMiss: {_lastMissSample}");
            sb.AppendLine($"texBytes={_texBytes / (1024 * 1024.0):0.0}MB " +
                          $"queued={_queuedTex.Count} pendingQ={_pendingTexLoad.Count}");
            return sb.ToString().TrimEnd();
        }
    }

    void NoteMiss(string reason, string? path, string? state)
    {
        _lastMissSample = $"{reason} {path ?? "?"} | {state ?? "-"}";
        // Rate-limit DiagLog — once / ~45 frames (~0.75s).
        if (_frames - _lastDiagFrame < 45)
            return;
        _lastDiagFrame = _frames;
        Port.Content.DiagLog.Warn($"gles {_lastMissSample} " +
                                  $"ok={_texturedLast}/{_rsiPathLast} missT={_texMissLast} missU={_uvMissLast} " +
                                  $"cache={_texCached} pend={_pendingTexLoad.Count}");
    }

    public void OnSurfaceCreated(IGL10? gl, EGLConfig? config)
    {
        try
        {
            // EGL context recreate invalidates every GL texture name — drop CPU-side caches
            // so we never bind dead ids (black / vanishing sprites).
            DropAllTextureCaches(deleteGlObjects: false);
            GLES20.GlClearColor(0.02f, 0.03f, 0.06f, 1f);
            GLES20.GlEnable(GLES20.GlBlend);
            GLES20.GlBlendFunc(GLES20.GlSrcAlpha, GLES20.GlOneMinusSrcAlpha);
            InitProgram();
            lock (_gate)
            {
                _ready = true;
                _lastError = "";
            }
        }
        catch (Exception ex)
        {
            lock (_gate) _lastError = ex.Message;
        }
    }

    void DropAllTextureCaches(bool deleteGlObjects)
    {
        if (deleteGlObjects && _texCache.Count > 0)
        {
            var ids = new int[_texCache.Count];
            var n = 0;
            foreach (var e in _texCache.Values)
            {
                if (e.Id != 0)
                    ids[n++] = e.Id;
            }

            if (n > 0)
                GLES20.GlDeleteTextures(n, ids, 0);
        }

        _texCache.Clear();
        _atlasMeta.Clear();
        _texLastUsed.Clear();
        _pendingTexLoad.Clear();
        _pendingPngLoad.Clear();
        _pendingRsiLoad.Clear();
        _queuedTex.Clear();
        _texRetryAtFrame.Clear();
        _texFailCount.Clear();
        _texNeeded.Clear();
        _burstLoadsUntilFrame = 0;
        // Bubble textures are also GL objects — clear without delete on context loss.
        if (deleteGlObjects)
        {
            foreach (var v in _bubbleTex.Values)
            {
                if (v.Id != 0)
                {
                    var ids = new[] { v.Id };
                    GLES20.GlDeleteTextures(1, ids, 0);
                }
            }
        }

        _bubbleTex.Clear();
        _texQuads.Clear();
        _texCached = 0;
        _texBytes = 0;
    }

    public void OnSurfaceChanged(IGL10? gl, int width, int height)
    {
        GLES20.GlViewport(0, 0, width, height);
        lock (_gate)
        {
            _width = width;
            _height = height;
        }
    }

    public void OnDrawFrame(IGL10? gl)
    {
        float r, g, b;
        bool pulse, ghost, fovOn, lightOn;
        float camX, camY, camRot, zoom, ambient;
        EntitySprite[] ents;
        TileSprite[] tiles;
        SpeechBubbleSprite[] bubbles;
        int count, tileCount, bubbleCount;
        string? contentRoot;
        Port.Content.AczOnDemandFetcher? texFetcher;
        lock (_gate)
        {
            r = _r;
            g = _g;
            b = _b;
            pulse = _pulse;
            ghost = _ghostMode;
            // Temporarily force ghost rendering to fullbright without FoV/shadows.
            fovOn = false;
            lightOn = false;
            ambient = 1f;
            camX = _camX;
            camY = _camY;
            camRot = _camRot;
            zoom = Math.Max(0.35f, _zoom);
            count = _entityCount;
            ents = _entities;
            tileCount = _tileCount;
            tiles = _tiles;
            bubbleCount = _bubbleCount;
            bubbles = _bubbles;
            contentRoot = _contentFilesRoot;
            texFetcher = _texFetcher;
            _frames++;
            _fpsWindowFrames++;
            _texNeeded.Clear();
            // Only pin what can be on-screen — marking all 1200 ents prevented eviction and
            // burned CPU before frustum cull. DrawEntities/DrawTiles add the rest.
            var nowMs = Environment.TickCount64;
            if (_fpsWindowStartMs == 0)
                _fpsWindowStartMs = nowMs;
            else if (nowMs - _fpsWindowStartMs >= 500)
            {
                _fps = _fpsWindowFrames * 1000f / Math.Max(1, nowMs - _fpsWindowStartMs);
                _fpsWindowFrames = 0;
                _fpsWindowStartMs = nowMs;
            }
            if (pulse && !ghost)
            {
                var t = (_frames % 120) / 120f;
                var wave = (MathF.Sin(t * MathF.PI * 2f) + 1f) * 0.5f;
                r = 0.04f + 0.55f * wave;
                g = 0.06f + 0.28f * wave;
                b = 0.10f + 0.05f * wave;
            }
            else if (ghost)
            {
                // Lit station floor under fullbright; darkened when lighting on.
                var dim = lightOn ? ambient : 1f;
                r = 0.035f * dim;
                g = 0.045f * dim;
                b = 0.07f * dim;
            }
        }

        GLES20.GlClearColor(r, g, b, 1f);
        GLES20.GlClear(GLES20.GlColorBufferBit);

        if (!ghost || !_glReady || _width <= 0 || _height <= 0)
        {
            lock (_gate) { _drawnLast = 0; _texturedLast = 0; _tilesDrawnLast = 0; }
            return;
        }

        try
        {
            var cosR = MathF.Cos(-camRot);
            var sinR = MathF.Sin(-camRot);

            // Consume cross-thread burst arm (LeavePvs) on the GL thread only.
            var burstArm = Interlocked.Exchange(ref _pendingBurstFrames, 0);
            if (burstArm > 0)
                _burstLoadsUntilFrame = Math.Max(_burstLoadsUntilFrame, _frames + burstArm);

            // Space backdrop (PC Default parallax layer1 + starfield) instead of black grid.
            DrawParallaxBackground(camX, camY, zoom, cosR, sinR, contentRoot, texFetcher);
            // Draw first (marks _texNeeded for visible only), then decode — avoids pinning 1k offscreen.
            DrawTiles(tiles, tileCount, camX, camY, zoom, cosR, sinR, contentRoot, texFetcher, lightOn ? ambient : 1f);

            if (count > 0)
                DrawEntities(ents, count, camX, camY, zoom, cosR, sinR, camRot, contentRoot, texFetcher, lightOn ? ambient : 1f);

            PumpTextureLoads(contentRoot, texFetcher);

            DrawSpeechBubbles(bubbles, bubbleCount, camX, camY, zoom, cosR, sinR);

            // GlGetError every frame syncs the GPU (same class of stall as GlIsTexture).
            // Sample rarely; OnSurfaceCreated already drops caches on context loss.
            if ((_frames & 255) == 0)
            {
                var glErr = GLES20.GlGetError();
                if (glErr != GLES20.GlNoError)
                {
                    Port.Content.DiagLog.Warn($"gles GL error 0x{glErr:X} — dropping texture caches");
                    DropAllTextureCaches(deleteGlObjects: false);
                }
            }

            if (fovOn)
                DrawFovOcclusionApprox(ents, count, camX, camY, zoom, cosR, sinR);
            else if (lightOn)
                DrawSoftVignette(0.22f);
        }
        catch (Exception ex)
        {
            // Never let a managed draw bug kill the GL thread / whole process.
            Port.Content.DiagLog.Error($"gles OnDrawFrame {ex.GetType().Name}: {ex.Message}");
            try { DropAllTextureCaches(deleteGlObjects: false); } catch { /* ignore */ }
        }
    }

    void DrawEntities(
        EntitySprite[] ents, int count,
        float camX, float camY, float zoom, float cosR, float sinR, float camRot,
        string? contentRoot, Port.Content.AczOnDemandFetcher? texFetcher,
        float lightMul = 1f)
    {
        var halfW = _width * 0.5f;
        var halfH = _height * 0.5f;
        var vert = 0;
        var textured = 0;
        var rsiPaths = 0;
        var missTex = 0;
        var missUv = 0;
        // Screen AABB + modest margin — load/draw only what the eye can see.
        var viewPad = MathF.Max(96f, 160f / MathF.Max(0.35f, zoom));
        var animTime = _frames / 60.0;
        _texQuads.Clear();

        // Prioritize own ghost + nearby mobs at front of load queue.
        string? controlledPath = null;
        string? controlledState = null;
        for (var i = 0; i < count; i++)
        {
            if (!ents[i].IsControlled || string.IsNullOrEmpty(ents[i].RsiPath)) continue;
            controlledPath = ents[i].RsiPath;
            controlledState = ents[i].StateName ?? "animated";
            break;
        }

        if (controlledPath is not null)
            QueueTexturePriority(controlledPath, controlledState);

        var nameplates = 0;
        const int maxNameplates = 12;
        for (var i = 0; i < count; i++)
        {
            ref readonly var e = ref ents[i];
            var wx = e.X * PixelsPerTile;
            var wy = e.Y * PixelsPerTile;
            var dx = wx - camX;
            var dy = wy - camY;
            var sx = (dx * cosR - dy * sinR) * zoom;
            var sy = (dx * sinR + dy * cosR) * zoom;

            if (MathF.Abs(sx) > halfW + viewPad || MathF.Abs(sy) > halfH + viewPad)
                continue;

            if (!string.IsNullOrEmpty(e.RsiPath))
            {
                _texNeeded.Add(MakeTexKey(e.RsiPath!, e.StateName));
                _texNeeded.Add(e.RsiPath!);
            }

            if (!string.IsNullOrEmpty(e.Label)
                && (e.IsControlled || nameplates < maxNameplates))
            {
                DrawNameplate(sx, sy + 28f, e.Label!, e.IsControlled);
                nameplates++;
                if (string.IsNullOrEmpty(e.RsiPath))
                    continue;
            }
            else if (!string.IsNullOrEmpty(e.Label) && string.IsNullOrEmpty(e.RsiPath))
            {
                continue;
            }

            var texKey = !string.IsNullOrEmpty(e.RsiPath) ? MakeTexKey(e.RsiPath!, e.StateName) : null;
            if (!string.IsNullOrEmpty(e.RsiPath))
            {
                rsiPaths++;
                if (contentRoot is not null)
                {
                    // Skip queue without explicit state — prevents first-meta PNG binding.
                    // Never pin IconSmooth sheets — that filled the cache and walls vanished.
                    if (!string.IsNullOrWhiteSpace(e.StateName) || e.IsControlled)
                        QueueTexture(e.RsiPath!, PreferPinPath(e.RsiPath!, e.IsControlled), e.StateName ?? "animated");
                    if (e.DirOverride >= 0 && !string.IsNullOrWhiteSpace(e.StateName))
                        PrefetchIconSmoothSheet(e.RsiPath!, e.StateName!, texFetcher);
                }
            }

            TexEntry tex = default;
            string? cacheKey = null;
            var hasTex = false;
            // Trust CPU cache ids — GlIsTexture every sprite was a GPU sync stall.
            // Context loss clears caches in OnSurfaceCreated / DropAllTextureCaches.
            if (texKey is not null
                && TryGetCachedTex(texKey, e.RsiPath!, out tex, out cacheKey)
                && tex.Id != 0)
            {
                hasTex = true;
                _texCache[cacheKey!] = tex with { LastUsedFrame = _frames };
                _texLastUsed[cacheKey!] = _frames;
            }

            // PC: NoRotation keeps the quad upright. Wallmount lights / SnapCardinals force
            // RSI dir 0 so 4-dir sheets don't spin with camera or entity yaw.
            var dirRot = e.NoRotation ? e.Rotation : e.Rotation - camRot;
            var drawRot = e.RotationOffset;
            var dirOv = e.DirOverride >= 0
                ? e.DirOverride
                : (e.NoRotation ? 0 : -1);
            if (hasTex)
            {
                if (string.IsNullOrWhiteSpace(e.StateName) && !e.IsControlled)
                    continue;
                var stateForUv = e.StateName ?? (e.IsControlled ? "animated" : null);
                var uv = ResolveUv(tex, stateForUv, dirRot, animTime, dirOv);
                if (uv.FrameW < 1f || uv.FrameH < 1f)
                {
                    missUv++;
                    NoteMiss("uv0", e.RsiPath, e.StateName);
                    continue;
                }
                var sizeX = Math.Max(8f, uv.FrameW) * zoom * (e.ScaleX == 0 ? 1f : MathF.Abs(e.ScaleX));
                var sizeY = Math.Max(8f, uv.FrameH) * zoom * (e.ScaleY == 0 ? 1f : MathF.Abs(e.ScaleY));

                // PC GhostSystem translucency for observer sprites (no size hack — avoids crop look).
                var alpha = e.IsControlled ? 0.92f : 1f;
                if (LooksLikeGhostPath(e.RsiPath))
                    alpha = e.IsControlled ? 0.9f : 0.7f;

                _texQuads.Add(new TexQuad(
                    tex.Id, e.DrawDepth, sy, sx, sy, sizeX, sizeY, drawRot,
                    e.R / 255f * lightMul, e.G / 255f * lightMul, e.B / 255f * lightMul, alpha,
                    uv.U0, uv.V0, uv.U1, uv.V1));
                textured++;
                continue;
            }

            // Avoid colored debug squares for missing RSI; better to hide until loaded.
            if (!string.IsNullOrEmpty(e.RsiPath))
            {
                missTex++;
                NoteMiss("noTex", e.RsiPath, e.StateName);
                continue;
            }

            // Keep a faint placeholder only for color-only entities.
            if (vert + 6 > MaxVerts)
                continue;

            var marker = (e.IsControlled ? 22f : (string.IsNullOrEmpty(e.RsiPath) ? 10f : 14f)) * zoom;
            float cr, cg, cb, ca;
            if (e.IsControlled) { cr = 0.55f; cg = 0.95f; cb = 1f; ca = 1f; }
            else if (!string.IsNullOrEmpty(e.RsiPath))
            {
                cr = e.R / 255f * lightMul;
                cg = e.G / 255f * lightMul;
                cb = e.B / 255f * lightMul;
                ca = 0.92f;
            }
            else { cr = 0.35f * lightMul; cg = 0.45f * lightMul; cb = 0.6f * lightMul; ca = 0.75f; }

            var x0 = sx - marker * 0.5f;
            var y0 = sy - marker * 0.5f;
            var x1 = sx + marker * 0.5f;
            var y1 = sy + marker * 0.5f;
            void Put(float px, float py)
            {
                _posScratch[vert * 2] = px;
                _posScratch[vert * 2 + 1] = py;
                _colScratch[vert * 4] = cr;
                _colScratch[vert * 4 + 1] = cg;
                _colScratch[vert * 4 + 2] = cb;
                _colScratch[vert * 4 + 3] = ca;
                vert++;
            }
            Put(x0, y0); Put(x1, y0); Put(x1, y1);
            Put(x0, y0); Put(x1, y1); Put(x0, y1);
        }

        FlushTexQuads();

        if (vert > 0)
        {
            EnsureBuffers(vert);
            _posBuf!.Position(0);
            _posBuf.Put(_posScratch, 0, vert * 2);
            _posBuf.Position(0);
            _colBuf!.Position(0);
            _colBuf.Put(_colScratch, 0, vert * 4);
            _colBuf.Position(0);

            GLES20.GlUseProgram(_program);
            GLES20.GlUniform2f(_uScreen, _width, _height);
            GLES20.GlEnableVertexAttribArray(_aPos);
            GLES20.GlEnableVertexAttribArray(_aColor);
            GLES20.GlVertexAttribPointer(_aPos, 2, GLES20.GlFloat, false, 0, _posBuf);
            GLES20.GlVertexAttribPointer(_aColor, 4, GLES20.GlFloat, false, 0, _colBuf);
            GLES20.GlDrawArrays(GLES20.GlTriangles, 0, vert);
            GLES20.GlDisableVertexAttribArray(_aPos);
            GLES20.GlDisableVertexAttribArray(_aColor);
        }

        lock (_gate)
        {
            _drawnLast = vert / 6 + textured;
            _texturedLast = textured;
            _rsiPathLast = rsiPaths;
            _texCached = _texCache.Count;
            _texMissLast = missTex;
            _uvMissLast = missUv;
            _pendingTexLast = _pendingTexLoad.Count;
            _loadFailLast = _texRetryAtFrame.Count;
        }
    }

    static bool LooksLikeGhostPath(string? path) =>
        !string.IsNullOrEmpty(path)
        && (path.Contains("Ghost", StringComparison.OrdinalIgnoreCase)
            || path.Contains("Observer", StringComparison.OrdinalIgnoreCase));

    void FlushTexQuads()
    {
        if (_texQuads.Count == 0 || _texProgram == 0)
            return;

        // Clyde HLR: depth → texture bind → Y (stable within depth).
        _texQuads.Sort(static (a, b) =>
        {
            var c = a.Depth.CompareTo(b.Depth);
            if (c != 0) return c;
            c = a.TexId.CompareTo(b.TexId);
            if (c != 0) return c;
            return a.SortY.CompareTo(b.SortY);
        });

        EnsureBatchBuffers();
        GLES20.GlUseProgram(_texProgram);
        GLES20.GlEnable(GLES20.GlBlend);
        GLES20.GlBlendFunc(GLES20.GlSrcAlpha, GLES20.GlOneMinusSrcAlpha);
        GLES20.GlActiveTexture(GLES20.GlTexture0);
        GLES20.GlUniform1i(_texUSampler, 0);
        GLES20.GlUniform2f(_texUScreen, _width, _height);
        if (_texUTint >= 0)
            GLES20.GlUniform4f(_texUTint, 1f, 1f, 1f, 1f);

        var i = 0;
        while (i < _texQuads.Count)
        {
            var texId = _texQuads[i].TexId;
            var batch = 0;
            while (i < _texQuads.Count && _texQuads[i].TexId == texId && batch < MaxBatchQuads)
            {
                WriteQuadVerts(_texQuads[i], batch);
                batch++;
                i++;
            }

            GLES20.GlBindTexture(GLES20.GlTexture2d, texId);
            var verts = batch * 6;
            _batchPosBuf!.Position(0);
            _batchPosBuf.Put(_batchPos, 0, verts * 2);
            _batchPosBuf.Position(0);
            _batchUvBuf!.Position(0);
            _batchUvBuf.Put(_batchUv, 0, verts * 2);
            _batchUvBuf.Position(0);
            _batchColBuf!.Position(0);
            _batchColBuf.Put(_batchCol, 0, verts * 4);
            _batchColBuf.Position(0);

            GLES20.GlEnableVertexAttribArray(_texAPos);
            GLES20.GlEnableVertexAttribArray(_texAUv);
            if (_texAColor >= 0)
                GLES20.GlEnableVertexAttribArray(_texAColor);
            GLES20.GlVertexAttribPointer(_texAPos, 2, GLES20.GlFloat, false, 0, _batchPosBuf);
            GLES20.GlVertexAttribPointer(_texAUv, 2, GLES20.GlFloat, false, 0, _batchUvBuf);
            if (_texAColor >= 0)
                GLES20.GlVertexAttribPointer(_texAColor, 4, GLES20.GlFloat, false, 0, _batchColBuf);
            GLES20.GlDrawArrays(GLES20.GlTriangles, 0, verts);
            GLES20.GlDisableVertexAttribArray(_texAPos);
            GLES20.GlDisableVertexAttribArray(_texAUv);
            if (_texAColor >= 0)
                GLES20.GlDisableVertexAttribArray(_texAColor);
        }

        _texQuads.Clear();
    }

    void WriteQuadVerts(TexQuad q, int slot)
    {
        var hx = q.SizeX * 0.5f;
        var hy = q.SizeY * 0.5f;
        var c = MathF.Cos(q.Rot);
        var s = MathF.Sin(q.Rot);

        float x0 = q.Sx + (-hx) * c - (-hy) * s;
        float y0 = q.Sy + (-hx) * s + (-hy) * c;
        float x1 = q.Sx + hx * c - (-hy) * s;
        float y1 = q.Sy + hx * s + (-hy) * c;
        float x2 = q.Sx + hx * c - hy * s;
        float y2 = q.Sy + hx * s + hy * c;
        float x3 = q.Sx + (-hx) * c - hy * s;
        float y3 = q.Sy + (-hx) * s + hy * c;

        var pi = slot * 12;
        _batchPos[pi] = x0; _batchPos[pi + 1] = y0;
        _batchPos[pi + 2] = x1; _batchPos[pi + 3] = y1;
        _batchPos[pi + 4] = x2; _batchPos[pi + 5] = y2;
        _batchPos[pi + 6] = x0; _batchPos[pi + 7] = y0;
        _batchPos[pi + 8] = x2; _batchPos[pi + 9] = y2;
        _batchPos[pi + 10] = x3; _batchPos[pi + 11] = y3;

        var u0 = q.U0;
        var u1 = q.U1;
        var v0 = 1f - q.V0;
        var v1 = 1f - q.V1;
        _batchUv[pi] = u0; _batchUv[pi + 1] = v0;
        _batchUv[pi + 2] = u1; _batchUv[pi + 3] = v0;
        _batchUv[pi + 4] = u1; _batchUv[pi + 5] = v1;
        _batchUv[pi + 6] = u0; _batchUv[pi + 7] = v0;
        _batchUv[pi + 8] = u1; _batchUv[pi + 9] = v1;
        _batchUv[pi + 10] = u0; _batchUv[pi + 11] = v1;

        var ci = slot * 24;
        for (var v = 0; v < 6; v++)
        {
            var o = ci + v * 4;
            _batchCol[o] = q.R;
            _batchCol[o + 1] = q.G;
            _batchCol[o + 2] = q.B;
            _batchCol[o + 3] = q.A;
        }
    }

    void EnsureBatchBuffers()
    {
        // FloatBuffer.Capacity() returns float element count, not bytes.
        var posFloats = MaxBatchQuads * 12;
        var colFloats = MaxBatchQuads * 24;
        if (_batchPosBuf is null || _batchPosBuf.Capacity() < posFloats)
            _batchPosBuf = ByteBuffer.AllocateDirect(posFloats * sizeof(float)).Order(ByteOrder.NativeOrder())!.AsFloatBuffer();
        if (_batchUvBuf is null || _batchUvBuf.Capacity() < posFloats)
            _batchUvBuf = ByteBuffer.AllocateDirect(posFloats * sizeof(float)).Order(ByteOrder.NativeOrder())!.AsFloatBuffer();
        if (_batchColBuf is null || _batchColBuf.Capacity() < colFloats)
            _batchColBuf = ByteBuffer.AllocateDirect(colFloats * sizeof(float)).Order(ByteOrder.NativeOrder())!.AsFloatBuffer();
    }

    void DrawNameplate(float sx, float sy, string label, bool emphasize)
    {
        var argb = emphasize ? unchecked((int)0xFFE8F4FF) : unchecked((int)0xFFEDE6D8);
        var tex = EnsureBubbleTexture(label, argb, 0.92f);
        if (tex.Id == 0)
            return;
        var uv = new Port.Content.RsiAtlas.UvRect(0, 0, 1, 1, tex.W, tex.H);
        var scale = emphasize ? 1f : 0.85f;
        DrawTexturedQuad(sx, sy, tex.W * scale, tex.H * scale, tex.Id, uv, 0, 1f, 1f, 1f, 1f);
    }

    void DrawSpeechBubbles(
        SpeechBubbleSprite[] bubbles, int bubbleCount,
        float camX, float camY, float zoom, float cosR, float sinR)
    {
        if (bubbleCount <= 0 || !_glReady)
            return;

        var halfW = _width * 0.5f;
        var halfH = _height * 0.5f;
        var viewPad = 280f;
        // Screen-space size (not world-zoom scaled) so text stays readable.
        const float headOffsetPx = 22f;

        for (var i = 0; i < bubbleCount; i++)
        {
            ref readonly var b = ref bubbles[i];
            if (string.IsNullOrWhiteSpace(b.Text) || b.Alpha < 0.05f)
                continue;

            var wx = b.X * PixelsPerTile;
            var wy = b.Y * PixelsPerTile;
            var dx = wx - camX;
            var dy = wy - camY;
            var sx = (dx * cosR - dy * sinR) * zoom;
            var sy = (dx * sinR + dy * cosR) * zoom + headOffsetPx + b.StackOffset;

            if (MathF.Abs(sx) > halfW + viewPad || MathF.Abs(sy) > halfH + viewPad)
                continue;

            var tex = EnsureBubbleTexture(b.Text, b.Argb, b.Alpha);
            if (tex.Id == 0)
                continue;

            var uv = new Port.Content.RsiAtlas.UvRect(0, 0, 1, 1, tex.W, tex.H);
            DrawTexturedQuad(sx, sy, tex.W, tex.H, tex.Id, uv, 0, 1f, 1f, 1f, b.Alpha);
        }

        SweepBubbleTextures();
    }

    readonly record struct BubbleTex(int Id, float W, float H, long LastFrame);
    readonly Dictionary<string, BubbleTex> _bubbleTex = new(StringComparer.Ordinal);
    readonly List<string> _bubbleSweep = new();

    BubbleTex EnsureBubbleTexture(string text, int argb, float alpha)
    {
        // Quantize alpha so we don't thrash textures every frame while fading.
        var aQ = (int)Math.Clamp(MathF.Round(alpha * 8f), 1, 8);
        var key = aQ + "|" + argb.ToString("X8") + "|" + text;
        if (_bubbleTex.TryGetValue(key, out var hit) && hit.Id != 0)
        {
            _bubbleTex[key] = hit with { LastFrame = _frames };
            return hit;
        }

        try
        {
            var bmp = RenderBubbleBitmap(text, argb, aQ / 8f);
            if (bmp is null)
                return default;
            try
            {
                var id = UploadBitmap(bmp);
                if (id == 0)
                    return default;
                var entry = new BubbleTex(id, bmp.Width, bmp.Height, _frames);
                _bubbleTex[key] = entry;
                return entry;
            }
            finally
            {
                bmp.Recycle();
            }
        }
        catch
        {
            return default;
        }
    }

    void SweepBubbleTextures()
    {
        if (_bubbleTex.Count < 24)
            return;
        _bubbleSweep.Clear();
        foreach (var (k, v) in _bubbleTex)
        {
            if (_frames - v.LastFrame > 180)
                _bubbleSweep.Add(k);
        }

        foreach (var k in _bubbleSweep)
        {
            if (!_bubbleTex.TryGetValue(k, out var v))
                continue;
            if (v.Id != 0)
            {
                var ids = new[] { v.Id };
                GLES20.GlDeleteTextures(1, ids, 0);
            }

            _bubbleTex.Remove(k);
        }
    }

    static Bitmap? RenderBubbleBitmap(string text, int argb, float alpha)
    {
        var tr = (argb >> 16) & 0xFF;
        var tg = (argb >> 8) & 0xFF;
        var tb = argb & 0xFF;
        var ta = (int)Math.Clamp(255f * alpha, 30, 255);

        var textPaint = new Paint
        {
            AntiAlias = true,
            TextSize = 26f,
            Color = new Color(tr, tg, tb, ta),
        };
        textPaint.SetTypeface(Typeface.Create("sans-serif-medium", TypefaceStyle.Normal));
        textPaint.SetShadowLayer(2f, 0, 1f, new Color(0, 0, 0, (int)(160 * alpha)));

        const float maxW = 320f;
        const float padX = 14f;
        const float padY = 10f;
        var lines = WrapText(textPaint, text, maxW - padX * 2);
        if (lines.Count == 0)
            return null;

        float lineH = textPaint.FontSpacing;
        float contentW = 0;
        foreach (var line in lines)
            contentW = Math.Max(contentW, textPaint.MeasureText(line));
        var w = (int)Math.Ceiling(Math.Min(maxW, contentW + padX * 2));
        var h = (int)Math.Ceiling(lines.Count * lineH + padY * 2);
        w = Math.Max(32, w);
        h = Math.Max(24, h);

        var bmp = Bitmap.CreateBitmap(w, h, Bitmap.Config.Argb8888!);
        if (bmp is null)
            return null;
        using var canvas = new Canvas(bmp);
        var bg = new Paint
        {
            AntiAlias = true,
            Color = new Color(12, 16, 22, (int)(175 * alpha)),
        };
        canvas.DrawRoundRect(0, 0, w, h, 12f, 12f, bg);
        var border = new Paint
        {
            AntiAlias = true,
            Color = new Color(220, 210, 190, (int)(90 * alpha)),
        };
        border.SetStyle(Paint.Style.Stroke!);
        border.StrokeWidth = 1.5f;
        canvas.DrawRoundRect(1, 1, w - 1, h - 1, 12f, 12f, border);

        var y = padY - textPaint.Ascent();
        foreach (var line in lines)
        {
            var lw = textPaint.MeasureText(line);
            canvas.DrawText(line, (w - lw) * 0.5f, y, textPaint);
            y += lineH;
        }

        return bmp;
    }

    static List<string> WrapText(Paint paint, string text, float maxWidth)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(text))
            return result;
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            result.Add(text);
            return result;
        }

        var line = words[0];
        for (var i = 1; i < words.Length; i++)
        {
            var trial = line + " " + words[i];
            if (paint.MeasureText(trial) <= maxWidth)
                line = trial;
            else
            {
                result.Add(line);
                line = words[i];
                if (result.Count >= 4)
                {
                    line = TruncateToWidth(paint, line + "…", maxWidth);
                    break;
                }
            }
        }

        if (!string.IsNullOrEmpty(line))
            result.Add(TruncateToWidth(paint, line, maxWidth));
        return result;
    }

    static string TruncateToWidth(Paint paint, string s, float maxWidth)
    {
        if (paint.MeasureText(s) <= maxWidth)
            return s;
        while (s.Length > 1 && paint.MeasureText(s + "…") > maxWidth)
            s = s[..^1];
        return s + "…";
    }

    static Port.Content.RsiAtlas.UvRect ResolveTileUv(TexEntry tex, byte variant)
    {
        var aw = tex.AtlasPixelW > 0 ? tex.AtlasPixelW : (int)MathF.Max(tex.FrameW, 1);
        var ah = tex.AtlasPixelH > 0 ? tex.AtlasPixelH : (int)MathF.Max(tex.FrameH, 1);
        var fw = tex.FrameW > 1 ? tex.FrameW : 32f;
        var fh = tex.FrameH > 1 ? tex.FrameH : 32f;
        if (fw > 64) fw = 32;
        if (fh > 64) fh = 32;
        var dimX = Math.Max(1, (int)(aw / fw));
        var dimY = Math.Max(1, (int)(ah / fh));
        var maxCells = dimX * dimY;
        var cell = maxCells <= 1 ? 0 : variant % maxCells;
        var col = cell % dimX;
        var row = cell / dimX;
        var u0 = (col * fw) / aw;
        var v0 = (row * fh) / ah;
        var u1 = ((col + 1) * fw) / aw;
        var v1 = ((row + 1) * fh) / ah;
        return new Port.Content.RsiAtlas.UvRect(u0, v0, Math.Min(1f, u1), Math.Min(1f, v1), fw, fh);
    }

    Port.Content.RsiAtlas.UvRect ResolveUv(TexEntry tex, string? state, float rotation, double time, int dirOverride = -1)
    {
        // IconSmooth corners: FolderMode PNGs are one state × N dirs. Prefer geometric cells
        // BEFORE Sample — meta DirCount=1 or LoadFolder AtlasW=full.png (32) collapses every
        // DirOverride to dir0, which draws four stacked SE corners (white L brackets).
        if (dirOverride >= 0 && tex.FolderMode && TryDirCellUv(tex, dirOverride, out var folderDirUv))
            return folderDirUv;

        // Prefer RSI meta atlas Sample (exact state + directions + delays) whenever available.
        if (tex.AtlasKey is not null
            && (_atlasMeta.TryGetValue(tex.AtlasKey, out var atlas)
                || _atlasMeta.TryGetValue(tex.AtlasKey.Replace('\\', '/'), out atlas)))
        {
            // Always prefer the GL texture pixel size for UV — LoadFolder seeds AtlasW from the
            // first state PNG (often 32×32), which turns 4-dir 128×32 sheets into a full strip.
            var ow = tex.AtlasPixelW > 0 ? tex.AtlasPixelW : 0;
            var oh = tex.AtlasPixelH > 0 ? tex.AtlasPixelH : 0;
            foreach (var stateAlt in Port.Content.RsiMeta.PreferredStateAlternates(state))
            {
                var sampled = Port.Content.RsiAtlas.Sample(
                    atlas, stateAlt, rotation, time,
                    folderPerStateSheet: tex.FolderMode, dirOverride,
                    overrideAtlasW: ow, overrideAtlasH: oh);
                if (sampled.FrameW >= 1f && sampled.FrameH >= 1f)
                {
                    // Packed .rsic with DirCount=1 still collapses IconSmooth dirs — fix via strip.
                    if (dirOverride >= 0
                        && atlas.States.TryGetValue(stateAlt, out var st)
                        && st.DirCount <= 1
                        && TryDirCellUv(tex, dirOverride, out var stripUv))
                        return stripUv;
                    return sampled;
                }
            }
        }

        // Meta missing: geometric dir strip, else first cell only.
        if (dirOverride >= 0 && TryDirCellUv(tex, dirOverride, out var dirUv))
            return dirUv;

        return SingleCellUv(tex);
    }

    /// <summary>
    /// Geometric UV for a direction cell on a per-state sheet (IconSmooth solidN.png etc.).
    /// Refuses large packed atlases where dir index ≠ sheet column.
    /// </summary>
    static bool TryDirCellUv(TexEntry tex, int dirOverride, out Port.Content.RsiAtlas.UvRect uv)
    {
        uv = default;
        var aw = tex.AtlasPixelW > 0 ? tex.AtlasPixelW : 0;
        var ah = tex.AtlasPixelH > 0 ? tex.AtlasPixelH : 0;
        var fw = (int)MathF.Max(1f, tex.FrameW);
        var fh = (int)MathF.Max(1f, tex.FrameH);
        if (aw < fw || ah < fh)
            return false;

        var dimX = Math.Max(1, aw / fw);
        var dimY = Math.Max(1, ah / fh);
        var cells = dimX * dimY;
        if (cells <= 1)
            return false; // single cell — DirOverride cannot help

        // Per-state IconSmooth sheets are 4×1 or 1×4 (sometimes 4×2 for anim). Packed
        // .rsic atlases are much larger — never treat those as dir strips.
        if (!tex.FolderMode && cells > 8)
            return false;
        if (!tex.FolderMode && dimX > 1 && dimY > 1 && cells > 4)
            return false;

        var dir = Math.Clamp(dirOverride, 0, cells - 1);
        var col = dir % dimX;
        var row = dir / dimX;
        var u0 = (col * fw) / (float)aw;
        var v0 = (row * fh) / (float)ah;
        var u1 = Math.Min(1f, ((col + 1) * fw) / (float)aw);
        var v1 = Math.Min(1f, ((row + 1) * fh) / (float)ah);
        uv = new Port.Content.RsiAtlas.UvRect(u0, v0, u1, v1, fw, fh);
        return true;
    }

    static bool ShouldAnimate(string? state) =>
        !string.IsNullOrWhiteSpace(state)
        && (state.Equals("animated", StringComparison.OrdinalIgnoreCase)
            || state.Contains("anim", StringComparison.OrdinalIgnoreCase)
            || state.Contains("walk", StringComparison.OrdinalIgnoreCase)
            || state.Contains("run", StringComparison.OrdinalIgnoreCase)
            || state.Contains("move", StringComparison.OrdinalIgnoreCase)
            || state.Contains("flick", StringComparison.OrdinalIgnoreCase)
            || state.Contains("pulse", StringComparison.OrdinalIgnoreCase)
            || state.Contains("spin", StringComparison.OrdinalIgnoreCase)
            || state.Contains("loop", StringComparison.OrdinalIgnoreCase));

    static float SnapCardinal(float radians)
    {
        var twoPi = MathF.PI * 2f;
        var a = radians % twoPi;
        if (a < 0) a += twoPi;
        var q = MathF.PI * 0.5f;
        return MathF.Round(a / q) * q;
    }

    static Port.Content.RsiAtlas.UvRect SingleCellUv(TexEntry tex)
    {
        var aw = tex.AtlasPixelW > 0 ? tex.AtlasPixelW : (int)MathF.Max(tex.FrameW, 1);
        var ah = tex.AtlasPixelH > 0 ? tex.AtlasPixelH : (int)MathF.Max(tex.FrameH, 1);
        var fw = Math.Max(1f, tex.FrameW);
        var fh = Math.Max(1f, tex.FrameH);
        // Prefer stored cell UVs when already cropped; otherwise top-left frame of sheet.
        if (tex.U1 <= 1.01f && tex.V1 <= 1.01f
            && (tex.U1 - tex.U0) * aw <= fw * 1.5f
            && (tex.V1 - tex.V0) * ah <= fh * 1.5f
            && tex.U1 > tex.U0 && tex.V1 > tex.V0)
            return new Port.Content.RsiAtlas.UvRect(tex.U0, tex.V0, tex.U1, tex.V1, fw, fh);

        var u1 = Math.Min(1f, fw / aw);
        var v1 = Math.Min(1f, fh / ah);
        return new Port.Content.RsiAtlas.UvRect(0, 0, u1, v1, fw, fh);
    }

    void DrawTiles(TileSprite[] tiles, int tileCount, float camX, float camY, float zoom,
        float cosR, float sinR,
        string? contentRoot, Port.Content.AczOnDemandFetcher? fetcher,
        float lightMul = 1f)
    {
        if (tileCount <= 0)
        {
            lock (_gate) _tilesDrawnLast = 0;
            return;
        }

        // Hard cap — poisoned world snapshots with 10k tiles killed the GL thread.
        if (tileCount > 3_000)
            tileCount = 3_000;

        var halfW = _width * 0.5f;
        var halfH = _height * 0.5f;
        var size = PixelsPerTile * zoom;
        var pad = size * 1.5f;
        var vert = 0;
        var drawn = 0;
        var animTime = _frames / 60.0;
        _texQuads.Clear();
        // Tile quads: camera + per-grid rotation so shuttles stay aligned.
        var camTileRot = MathF.Atan2(sinR, cosR);

        for (var i = 0; i < tileCount; i++)
        {
            ref readonly var t = ref tiles[i];
            var dx = t.X * PixelsPerTile - camX;
            var dy = t.Y * PixelsPerTile - camY;
            var sx = (dx * cosR - dy * sinR) * zoom;
            var sy = (dx * sinR + dy * cosR) * zoom;
            if (MathF.Abs(sx) > halfW + pad || MathF.Abs(sy) > halfH + pad)
                continue;

            var tileRot = t.Rotation + camTileRot
                          + (t.RotationMirroring % 4) * (MathF.PI * 0.5f);

            if (!string.IsNullOrEmpty(t.RsiPath))
            {
                _texNeeded.Add(MakeTexKey(t.RsiPath!, t.StateName));
                _texNeeded.Add(t.RsiPath!);
                if (contentRoot is not null)
                    QueueTexture(t.RsiPath!, pin: false, state: t.StateName);
            }

            var hasTex = false;
            TexEntry tex = default;
            string? cacheKey = null;
            var texKey = !string.IsNullOrEmpty(t.RsiPath) ? MakeTexKey(t.RsiPath!, t.StateName) : null;
            if (texKey is not null
                && TryGetCachedTex(texKey, t.RsiPath!, out tex, out cacheKey)
                && tex.Id != 0)
            {
                hasTex = true;
                _texCache[cacheKey!] = tex with
                {
                    LastUsedFrame = _frames,
                    Pinned = tex.Pinned
                };
                _texLastUsed[cacheKey!] = _frames;
            }

            if (hasTex)
            {
                var uv = string.IsNullOrEmpty(t.StateName)
                    ? ResolveTileUv(tex, t.Variant)
                    : ResolveUv(tex, t.StateName, 0, animTime);
                if (uv.FrameW < 1f || uv.FrameH < 1f)
                    continue;
                _texQuads.Add(new TexQuad(
                    tex.Id, -100, sy, sx, sy, size, size, tileRot,
                    t.R / 255f * lightMul, t.G / 255f * lightMul, t.B / 255f * lightMul, 1f,
                    uv.U0, uv.V0, uv.U1, uv.V1));
                drawn++;
                continue;
            }

            // Avoid noisy placeholder quads for missing tile textures.
            if (!string.IsNullOrEmpty(t.RsiPath))
                continue;

            // Colored fallback — only for truly color-only tiles.
            if (_program == 0 || vert + 6 > MaxVerts)
                continue;

            var cr = t.R / 255f * lightMul;
            var cg = t.G / 255f * lightMul;
            var cb = t.B / 255f * lightMul;
            const float ca = 0.95f;
            var hx = size * 0.5f;
            var hy = size * 0.5f;
            var c = MathF.Cos(tileRot);
            var s = MathF.Sin(tileRot);

            void PutLocal(float lx, float ly)
            {
                var px = sx + lx * c - ly * s;
                var py = sy + lx * s + ly * c;
                _posScratch[vert * 2] = px;
                _posScratch[vert * 2 + 1] = py;
                _colScratch[vert * 4] = cr;
                _colScratch[vert * 4 + 1] = cg;
                _colScratch[vert * 4 + 2] = cb;
                _colScratch[vert * 4 + 3] = ca;
                vert++;
            }

            PutLocal(-hx, -hy); PutLocal(hx, -hy); PutLocal(hx, hy);
            PutLocal(-hx, -hy); PutLocal(hx, hy); PutLocal(-hx, hy);
            drawn++;
        }

        FlushTexQuads();

        if (vert > 0 && _program != 0)
        {
            EnsureBuffers(vert);
            _posBuf!.Position(0);
            _posBuf.Put(_posScratch, 0, vert * 2);
            _posBuf.Position(0);
            _colBuf!.Position(0);
            _colBuf.Put(_colScratch, 0, vert * 4);
            _colBuf.Position(0);

            GLES20.GlUseProgram(_program);
            GLES20.GlEnable(GLES20.GlBlend);
            GLES20.GlBlendFunc(GLES20.GlSrcAlpha, GLES20.GlOneMinusSrcAlpha);
            GLES20.GlUniform2f(_uScreen, _width, _height);
            GLES20.GlEnableVertexAttribArray(_aPos);
            GLES20.GlEnableVertexAttribArray(_aColor);
            GLES20.GlVertexAttribPointer(_aPos, 2, GLES20.GlFloat, false, 0, _posBuf);
            GLES20.GlVertexAttribPointer(_aColor, 4, GLES20.GlFloat, false, 0, _colBuf);
            GLES20.GlDrawArrays(GLES20.GlTriangles, 0, vert);
            GLES20.GlDisableVertexAttribArray(_aPos);
            GLES20.GlDisableVertexAttribArray(_aColor);
        }

        lock (_gate) _tilesDrawnLast = drawn;
    }

    /// <summary>
    /// PC Default parallax: tiled layer1.png at high slowness + lightweight starfield
    /// stand-in for GeneratedParallax star layers.
    /// </summary>
    void DrawParallaxBackground(
        float camX, float camY, float zoom, float cosR, float sinR,
        string? contentRoot, Port.Content.AczOnDemandFetcher? texFetcher)
    {
        QueueTexture(ParallaxLayerPath, pin: true);
        texFetcher?.EnsureFile(ParallaxLayerPath);

        DrawStarfieldOverlay(camX, camY, zoom);

        if (contentRoot is null)
            return;
        if (!TryGetCachedTex(ParallaxLayerPath, ParallaxLayerPath, out var tex, out var cacheKey)
            || tex.Id == 0)
            return;

        _texCache[cacheKey!] = tex with { LastUsedFrame = _frames, Pinned = true };
        var tileW = Math.Max(256f, tex.AtlasPixelW > 0 ? tex.AtlasPixelW : tex.FrameW);
        var tileH = Math.Max(256f, tex.AtlasPixelH > 0 ? tex.AtlasPixelH : tex.FrameH);
        // PC: origin = eye * slowness (home=0); tiles cover the view AABB.
        var originX = camX * ParallaxSlowness;
        var originY = camY * ParallaxSlowness;
        var halfW = _width * 0.5f / zoom;
        var halfH = _height * 0.5f / zoom;
        var reach = MathF.Max(halfW, halfH) * 1.6f;
        var startX = MathF.Floor((originX - reach) / tileW) * tileW;
        var startY = MathF.Floor((originY - reach) / tileH) * tileH;
        var uv = new Port.Content.RsiAtlas.UvRect(0, 0, 1, 1, tileW, tileH);
        var drawn = 0;
        for (var wx = startX; wx < originX + reach + tileW && drawn < 48; wx += tileW)
        for (var wy = startY; wy < originY + reach + tileH && drawn < 48; wy += tileH)
        {
            var cx = wx + tileW * 0.5f;
            var cy = wy + tileH * 0.5f;
            var dx = cx - camX;
            var dy = cy - camY;
            var sx = (dx * cosR - dy * sinR) * zoom;
            var sy = (dx * sinR + dy * cosR) * zoom;
            DrawTexturedQuad(sx, sy, tileW * zoom, tileH * zoom, tex.Id, uv, 0,
                0.55f, 0.55f, 0.65f, 0.85f);
            drawn++;
        }
    }

    void DrawStarfieldOverlay(float camX, float camY, float zoom)
    {
        if (_program == 0 || _width <= 0 || _height <= 0)
            return;

        // Cheap GeneratedParallax stand-in: deterministic stars drifting with camera.
        var halfW = _width * 0.5f;
        var halfH = _height * 0.5f;
        var seed = unchecked(
            (int)MathF.Floor(camX * 0.02f) * 73856093
            ^ (int)MathF.Floor(camY * 0.02f) * 19349663);
        var vert = 0;
        for (var i = 0; i < 120 && vert + 6 <= MaxVerts; i++)
        {
            seed = unchecked(seed * 1103515245 + 12345);
            var nx = ((seed >> 16) & 0x7FFF) / 32768f;
            seed = unchecked(seed * 1103515245 + 12345);
            var ny = ((seed >> 16) & 0x7FFF) / 32768f;
            seed = unchecked(seed * 1103515245 + 12345);
            var bright = 0.35f + 0.65f * (((seed >> 16) & 0x7FFF) / 32768f);
            var sx = (nx * 2f - 1f) * halfW;
            var sy = (ny * 2f - 1f) * halfH;
            // Slow drift vs camera for depth cue.
            sx -= (camX * (1f - 0.996f) * zoom) % (halfW * 2f);
            sy -= (camY * (1f - 0.989f) * zoom) % (halfH * 2f);
            var size = 1.2f + bright * 1.8f;
            vert = AppendSolidQuad(vert, sx - size, sy - size, sx + size, sy + size,
                bright, bright, bright * 0.95f, 0.55f + bright * 0.35f);
        }

        if (vert <= 0) return;
        // DrawTexturedQuad may have left a tiny (12-float) _posBuf — grow before Put.
        EnsureBuffers(vert);
        GLES20.GlUseProgram(_program);
        GLES20.GlUniform2f(_uScreen, _width, _height);
        _posBuf!.Position(0);
        _posBuf.Put(_posScratch, 0, vert * 2);
        _posBuf.Position(0);
        _colBuf!.Position(0);
        _colBuf.Put(_colScratch, 0, vert * 4);
        _colBuf.Position(0);
        GLES20.GlEnableVertexAttribArray(_aPos);
        GLES20.GlEnableVertexAttribArray(_aColor);
        GLES20.GlVertexAttribPointer(_aPos, 2, GLES20.GlFloat, false, 0, _posBuf);
        GLES20.GlVertexAttribPointer(_aColor, 4, GLES20.GlFloat, false, 0, _colBuf);
        GLES20.GlDrawArrays(GLES20.GlTriangles, 0, vert);
        GLES20.GlDisableVertexAttribArray(_aPos);
        GLES20.GlDisableVertexAttribArray(_aColor);
    }

    int AppendSolidQuad(int vert, float x0, float y0, float x1, float y1, float r, float g, float b, float a)
    {
        if (vert + 6 > MaxVerts) return vert;
        void Put(int idx, float x, float y)
        {
            _posScratch[idx * 2] = x;
            _posScratch[idx * 2 + 1] = y;
            _colScratch[idx * 4] = r;
            _colScratch[idx * 4 + 1] = g;
            _colScratch[idx * 4 + 2] = b;
            _colScratch[idx * 4 + 3] = a;
        }
        Put(vert, x0, y0); Put(vert + 1, x1, y0); Put(vert + 2, x1, y1);
        Put(vert + 3, x0, y0); Put(vert + 4, x1, y1); Put(vert + 5, x0, y1);
        return vert + 6;
    }

    void DrawWorldGrid(float camX, float camY, float zoom, float cosR, float sinR)
    {
        // Ghost mode uses parallax instead of the black tiled grid.
        if (_ghostMode || _program == 0 || _width <= 0 || _height <= 0)
            return;

        var halfW = _width * 0.5f;
        var halfH = _height * 0.5f;
        var tile = PixelsPerTile;
        // Expand range so rotated view still fills the screen.
        var reach = (MathF.Max(halfW, halfH) * 1.5f) / zoom;
        var startX = MathF.Floor((camX - reach) / tile) * tile;
        var endX = camX + reach + tile;
        var startY = MathF.Floor((camY - reach) / tile) * tile;
        var endY = camY + reach + tile;
        var vert = 0;
        var thick = 1.1f;

        void AddWorldSeg(float wx0, float wy0, float wx1, float wy1, float cr, float cg, float cb, float ca)
        {
            if (vert + 6 > MaxVerts) return;
            float ToSx(float wx, float wy)
            {
                var dx = wx - camX;
                var dy = wy - camY;
                return (dx * cosR - dy * sinR) * zoom;
            }
            float ToSy(float wx, float wy)
            {
                var dx = wx - camX;
                var dy = wy - camY;
                return (dx * sinR + dy * cosR) * zoom;
            }

            var x0 = ToSx(wx0, wy0);
            var y0 = ToSy(wx0, wy0);
            var x1 = ToSx(wx1, wy1);
            var y1 = ToSy(wx1, wy1);
            var lx = x1 - x0;
            var ly = y1 - y0;
            var len = MathF.Sqrt(lx * lx + ly * ly);
            if (len < 0.01f) return;
            var nx = -ly / len * thick * 0.5f;
            var ny = lx / len * thick * 0.5f;

            void Put(float px, float py)
            {
                _posScratch[vert * 2] = px;
                _posScratch[vert * 2 + 1] = py;
                _colScratch[vert * 4] = cr;
                _colScratch[vert * 4 + 1] = cg;
                _colScratch[vert * 4 + 2] = cb;
                _colScratch[vert * 4 + 3] = ca;
                vert++;
            }

            Put(x0 + nx, y0 + ny); Put(x1 + nx, y1 + ny); Put(x1 - nx, y1 - ny);
            Put(x0 + nx, y0 + ny); Put(x1 - nx, y1 - ny); Put(x0 - nx, y0 - ny);
        }

        for (var x = startX; x <= endX; x += tile)
        {
            var major = Math.Abs(x / tile) % 8 < 0.01f;
            var a = major ? 0.22f : 0.10f;
            AddWorldSeg(x, startY, x, endY, 0.25f, 0.45f, 0.35f, a);
        }

        for (var y = startY; y <= endY; y += tile)
        {
            var major = Math.Abs(y / tile) % 8 < 0.01f;
            var a = major ? 0.22f : 0.10f;
            AddWorldSeg(startX, y, endX, y, 0.25f, 0.45f, 0.35f, a);
        }

        if (vert <= 0)
            return;

        EnsureBuffers(vert);
        _posBuf!.Position(0);
        _posBuf.Put(_posScratch, 0, vert * 2);
        _posBuf.Position(0);
        _colBuf!.Position(0);
        _colBuf.Put(_colScratch, 0, vert * 4);
        _colBuf.Position(0);

        GLES20.GlUseProgram(_program);
        GLES20.GlUniform2f(_uScreen, _width, _height);
        GLES20.GlEnableVertexAttribArray(_aPos);
        GLES20.GlEnableVertexAttribArray(_aColor);
        GLES20.GlVertexAttribPointer(_aPos, 2, GLES20.GlFloat, false, 0, _posBuf);
        GLES20.GlVertexAttribPointer(_aColor, 4, GLES20.GlFloat, false, 0, _colBuf);
        GLES20.GlDrawArrays(GLES20.GlTriangles, 0, vert);
        GLES20.GlDisableVertexAttribArray(_aPos);
        GLES20.GlDisableVertexAttribArray(_aColor);
    }

    void DrawTexturedQuad(
        float sx, float sy, float sizeX, float sizeY, int texId,
        Port.Content.RsiAtlas.UvRect uv, float rotation,
        float r, float g, float b, float a)
    {
        if (_texProgram == 0 || _uvBuf is null || texId == 0)
            return;

        var hx = sizeX * 0.5f;
        var hy = sizeY * 0.5f;
        // Local corners, then rotate around center (entity facing).
        float c = MathF.Cos(rotation);
        float s = MathF.Sin(rotation);
        void Rot(float lx, float ly, out float ox, out float oy)
        {
            ox = sx + lx * c - ly * s;
            oy = sy + lx * s + ly * c;
        }

        Rot(-hx, -hy, out var x0, out var y0);
        Rot(hx, -hy, out var x1, out var y1);
        Rot(hx, hy, out var x2, out var y2);
        Rot(-hx, hy, out var x3, out var y3);

        _texPosScratch[0] = x0; _texPosScratch[1] = y0;
        _texPosScratch[2] = x1; _texPosScratch[3] = y1;
        _texPosScratch[4] = x2; _texPosScratch[5] = y2;
        _texPosScratch[6] = x0; _texPosScratch[7] = y0;
        _texPosScratch[8] = x2; _texPosScratch[9] = y2;
        _texPosScratch[10] = x3; _texPosScratch[11] = y3;

        var u0 = uv.U0;
        var u1 = uv.U1;
        var v0 = 1f - uv.V0;
        var v1 = 1f - uv.V1;
        _uvScratch[0] = u0; _uvScratch[1] = v0;
        _uvScratch[2] = u1; _uvScratch[3] = v0;
        _uvScratch[4] = u1; _uvScratch[5] = v1;
        _uvScratch[6] = u0; _uvScratch[7] = v0;
        _uvScratch[8] = u1; _uvScratch[9] = v1;
        _uvScratch[10] = u0; _uvScratch[11] = v1;

        // Grow shared solid buffers if needed; never replace a large buffer with a 12-float one.
        EnsureBuffers(6);
        _posBuf!.Position(0);
        _posBuf.Put(_texPosScratch, 0, 12);
        _posBuf.Position(0);
        _uvBuf.Position(0);
        _uvBuf.Put(_uvScratch, 0, 12);
        _uvBuf.Position(0);

        GLES20.GlUseProgram(_texProgram);
        GLES20.GlEnable(GLES20.GlBlend);
        GLES20.GlBlendFunc(GLES20.GlSrcAlpha, GLES20.GlOneMinusSrcAlpha);
        GLES20.GlActiveTexture(GLES20.GlTexture0);
        GLES20.GlBindTexture(GLES20.GlTexture2d, texId);
        GLES20.GlUniform1i(_texUSampler, 0);
        GLES20.GlUniform2f(_texUScreen, _width, _height);
        if (_texUTint >= 0)
            GLES20.GlUniform4f(_texUTint, 1f, 1f, 1f, 1f);
        if (_texAColor >= 0)
        {
            GLES20.GlDisableVertexAttribArray(_texAColor);
            GLES20.GlVertexAttrib4f(_texAColor, r, g, b, a);
        }
        GLES20.GlEnableVertexAttribArray(_texAPos);
        GLES20.GlEnableVertexAttribArray(_texAUv);
        GLES20.GlVertexAttribPointer(_texAPos, 2, GLES20.GlFloat, false, 0, _posBuf);
        GLES20.GlVertexAttribPointer(_texAUv, 2, GLES20.GlFloat, false, 0, _uvBuf);
        GLES20.GlDrawArrays(GLES20.GlTriangles, 0, 6);
        GLES20.GlDisableVertexAttribArray(_texAPos);
        GLES20.GlDisableVertexAttribArray(_texAUv);
    }

    void MarkNeededTextures(EntitySprite[] ents, int count, TileSprite[] tiles, int tileCount)
    {
        _texNeeded.Clear();
        for (var i = 0; i < tileCount; i++)
        {
            var p = tiles[i].RsiPath;
            if (string.IsNullOrEmpty(p)) continue;
            _texNeeded.Add(MakeTexKey(p!, tiles[i].StateName));
            _texNeeded.Add(p!);
        }

        for (var i = 0; i < count; i++)
        {
            var p = ents[i].RsiPath;
            if (string.IsNullOrEmpty(p)) continue;
            _texNeeded.Add(MakeTexKey(p!, ents[i].StateName));
            _texNeeded.Add(p!);
        }
    }

    static string MakeTexKey(string rsiPath, string? state)
    {
        if (string.IsNullOrWhiteSpace(state))
            return rsiPath;
        // Packed .rsic / plain PNG already contain all cells — one GPU texture per path.
        if (rsiPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            || rsiPath.EndsWith(".rsic", StringComparison.OrdinalIgnoreCase))
            return rsiPath;
        return rsiPath + "|" + state;
    }

    static void SplitTexKey(string key, out string path, out string? state)
    {
        var i = key.LastIndexOf('|');
        if (i > 0 && i < key.Length - 1)
        {
            var maybeState = key[(i + 1)..];
            if (maybeState.IndexOf('/') < 0 && maybeState.IndexOf('\\') < 0
                && !maybeState.EndsWith(".rsi", StringComparison.OrdinalIgnoreCase)
                && !maybeState.EndsWith(".rsic", StringComparison.OrdinalIgnoreCase)
                && !maybeState.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                path = key[..i];
                state = maybeState;
                return;
            }
        }

        path = key;
        state = null;
    }

    bool TryGetCachedTex(string texKey, string rsiPath, out TexEntry tex, out string? cacheKey)
    {
        if (_texCache.TryGetValue(texKey, out tex) && tex.Id != 0)
        {
            cacheKey = texKey;
            return true;
        }
        // A bare-path fallback is valid only for a packed .rsic atlas. Folder RSIs
        // own one GPU texture per exact state and must never cross-hit another sheet.
        if (texKey != rsiPath
            && _texCache.TryGetValue(rsiPath, out tex)
            && tex.Id != 0
            && !tex.FolderMode)
        {
            cacheKey = rsiPath;
            return true;
        }
        tex = default;
        cacheKey = null;
        return false;
    }

    static bool PreferPinPath(string path, bool isControlled)
    {
        return isControlled;
    }

    void QueueTexture(string rsiPath, bool pin = false, string? state = null)
    {
        if (string.IsNullOrWhiteSpace(rsiPath))
            return;
        var key = MakeTexKey(rsiPath, state);
        _texNeeded.Add(key);
        _texNeeded.Add(rsiPath);
        _recentlyVisibleUntil[key] = _frames + RecentlyVisibleFrames;

        if (_texCache.TryGetValue(key, out var existing))
        {
            _texCache[key] = existing with
            {
                LastUsedFrame = _frames,
                Pinned = existing.Pinned || pin,
            };
            _texLastUsed[key] = _frames;
            return;
        }

        // Packed .rsic only: bare path shares one atlas. Folder RSI must never cross-hit.
        // If the atlas lacks this state (and aliases), fall through to folder PNG load.
        if (key != rsiPath
            && _texCache.TryGetValue(rsiPath, out existing)
            && !existing.FolderMode)
        {
            var atlasOk = true;
            if (!string.IsNullOrWhiteSpace(state)
                && (_atlasMeta.TryGetValue(rsiPath, out var cachedAtlas)
                    || (existing.AtlasKey is not null && _atlasMeta.TryGetValue(existing.AtlasKey, out cachedAtlas))))
            {
                atlasOk = false;
                foreach (var alt in Port.Content.RsiMeta.PreferredStateAlternates(state))
                {
                    if (cachedAtlas.States.ContainsKey(alt))
                    {
                        atlasOk = true;
                        break;
                    }
                }
            }

            if (atlasOk)
            {
                _texCache[rsiPath] = existing with
                {
                    LastUsedFrame = _frames,
                    Pinned = existing.Pinned || pin,
                };
                _texLastUsed[rsiPath] = _frames;
                return;
            }
        }

        if (_queuedTex.Contains(key))
            return;
        if (_texRetryAtFrame.TryGetValue(key, out var retryAt) && _frames < retryAt)
            return;

        if (rsiPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            if (_texCache.Count >= MaxTexCache && !pin)
                TryEvictTextures(16);
            _queuedTex.Add(key);
            if (pin)
                PrependPending(_pendingTexLoad, key);
            else
                _pendingTexLoad.Enqueue(key);
            return;
        }

        if (_texCache.Count >= MaxTexCache && !pin && !_texNeeded.Contains(key))
            TryEvictTextures(16);

        _queuedTex.Add(key);
        if (pin)
            PrependPending(_pendingTexLoad, key);
        else
            _pendingTexLoad.Enqueue(key);
    }

    void PrefetchIconSmoothSheet(string rsiPath, string stateName, Port.Content.AczOnDemandFetcher? fetcher)
    {
        // state like solid3 / riveted0 → base "solid" / "riveted"
        if (!Port.Content.IconSmoothInfer.TrySplitNumbered(stateName, out var stateBase, out _))
            return;
        var prefetchKey = rsiPath + "|" + stateBase;
        if (!_iconSmoothPrefetched.Add(prefetchKey))
            return;

        // Only kick ACZ for the packed atlas / this state's PNG.
        // Do NOT FromRsi (disk) or QueueTexture(0..15) on the GL thread — that OOM/ANR'd after warp.
        if (fetcher is null || !fetcher.IsReady)
            return;
        foreach (var cand in Port.Content.AczOnDemandFetcher.CandidateTexturePaths(rsiPath, stateName))
            fetcher.EnsureFile(cand);
    }

    static void PrependPending(Queue<string> q, string key)
    {
        if (q.Count == 0)
        {
            q.Enqueue(key);
            return;
        }

        var rest = q.ToArray();
        q.Clear();
        q.Enqueue(key);
        foreach (var item in rest)
            q.Enqueue(item);
    }

    void QueueTexturePriority(string rsiPath, string? state = null) => QueueTexture(rsiPath, pin: true, state: state);

    void TrimPendingQueue(Queue<string> q, int keep)
    {
        // Drop stale (not needed this frame) entries so warp into a new area can load.
        var n = q.Count;
        for (var i = 0; i < n; i++)
        {
            var path = q.Dequeue();
            if (_texNeeded.Contains(path) && q.Count < keep)
                q.Enqueue(path);
            else
                _queuedTex.Remove(path);
        }
    }

    bool TryEvictTextures(int maxEvict)
    {
        _texEvictScratch.Clear();
        foreach (var (path, entry) in _texCache)
        {
            if (entry.Pinned) continue;
            if (_texNeeded.Contains(path)) continue;
            if (IsFloorTileKey(path)) continue;
            if (IsIconSmoothWallKey(path)) continue;
            if (_recentlyVisibleUntil.TryGetValue(path, out var until) && _frames <= until)
                continue;
            _texEvictScratch.Add(path);
        }

        if (_texEvictScratch.Count == 0)
            return false;

        _texEvictScratch.Sort((a, b) =>
        {
            var la = _texCache.TryGetValue(a, out var ea) ? ea.LastUsedFrame : 0;
            var lb = _texCache.TryGetValue(b, out var eb) ? eb.LastUsedFrame : 0;
            return la.CompareTo(lb);
        });

        var n = Math.Min(maxEvict, _texEvictScratch.Count);
        for (var i = 0; i < n; i++)
            EvictOne(_texEvictScratch[i]);

        return n > 0;
    }

    static bool IsFloorTileKey(string path) =>
        path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
        && (path.Contains("Tiles/", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/Tiles/", StringComparison.OrdinalIgnoreCase)
            || path.Contains("Textures/Tiles", StringComparison.OrdinalIgnoreCase));

    /// <summary>Protect IconSmooth wall/window sheets from cache thrash (blink).</summary>
    static bool IsIconSmoothWallKey(string path)
    {
        var p = path.Replace('\\', '/');
        if (!(p.Contains("/Walls/", StringComparison.OrdinalIgnoreCase)
              || p.Contains("/Windows/", StringComparison.OrdinalIgnoreCase)
              || p.Contains("Grille", StringComparison.OrdinalIgnoreCase)))
            return false;
        // Folder RSI keys are "path|solid3" — numbered fill states.
        var bar = p.LastIndexOf('|');
        if (bar < 0) return true;
        var state = p[(bar + 1)..];
        return Port.Content.RsiMeta.LooksLikeIconSmoothStateName(state)
               || Port.Content.IconSmoothInfer.TrySplitNumbered(state, out _, out _);
    }

    /// <summary>
    /// Last resort when cache is full of recently-visible entries but a must-load needs a slot.
    /// Still never touches pinned or currently-needed keys.
    /// </summary>
    void ForceEvictOldestUnneeded(int maxEvict)
    {
        _texEvictScratch.Clear();
        foreach (var (path, entry) in _texCache)
        {
            if (entry.Pinned) continue;
            if (_texNeeded.Contains(path)) continue;
            if (IsFloorTileKey(path)) continue;
            // Prefer keeping recently-seen wall sheets; allow eviction of stale ones when full.
            if (_recentlyVisibleUntil.TryGetValue(path, out var until) && _frames <= until)
                continue;
            _texEvictScratch.Add(path);
        }

        if (_texEvictScratch.Count == 0)
            return;

        _texEvictScratch.Sort((a, b) =>
        {
            var la = _texCache.TryGetValue(a, out var ea) ? ea.LastUsedFrame : 0;
            var lb = _texCache.TryGetValue(b, out var eb) ? eb.LastUsedFrame : 0;
            return la.CompareTo(lb);
        });

        var n = Math.Min(maxEvict, _texEvictScratch.Count);
        for (var i = 0; i < n; i++)
            EvictOne(_texEvictScratch[i]);
    }

    void EvictOne(string path)
    {
        if (!_texCache.TryGetValue(path, out var e))
            return;
        if (e.Pinned || _texNeeded.Contains(path))
            return;
        if (e.Id != 0)
            GLES20.GlDeleteTextures(1, new[] { e.Id }, 0);
        _texBytes = Math.Max(0, _texBytes - TextureBytes(e));
        _texCache.Remove(path);
        _texLastUsed.Remove(path);
        _queuedTex.Remove(path);
        _recentlyVisibleUntil.Remove(path);
    }

    static long TextureBytes(TexEntry entry)
    {
        var w = entry.AtlasPixelW > 0 ? entry.AtlasPixelW : (int)MathF.Max(1, entry.FrameW);
        var h = entry.AtlasPixelH > 0 ? entry.AtlasPixelH : (int)MathF.Max(1, entry.FrameH);
        return (long)w * h * 4;
    }

    void StoreTexture(string key, TexEntry entry)
    {
        if (_texCache.TryGetValue(key, out var previous))
        {
            if (previous.Id != 0 && previous.Id != entry.Id)
                GLES20.GlDeleteTextures(1, new[] { previous.Id }, 0);
            _texBytes = Math.Max(0, _texBytes - TextureBytes(previous));
        }

        _texCache[key] = entry;
        _texBytes += TextureBytes(entry);
        TrimTextureBudget();
    }

    void TrimTextureBudget()
    {
        if (_texCache.Count <= MaxTexCache && _texBytes <= MaxTexBytes)
            return;

        _texEvictScratch.Clear();
        foreach (var (key, entry) in _texCache)
        {
            if (entry.Pinned || _texNeeded.Contains(key))
                continue;
            _texEvictScratch.Add(key);
        }

        _texEvictScratch.Sort((a, b) =>
        {
            var la = _texCache.TryGetValue(a, out var ea) ? ea.LastUsedFrame : 0;
            var lb = _texCache.TryGetValue(b, out var eb) ? eb.LastUsedFrame : 0;
            return la.CompareTo(lb);
        });

        foreach (var key in _texEvictScratch)
        {
            if (_texCache.Count <= MaxTexCache && _texBytes <= MaxTexBytes)
                break;
            EvictOne(key);
        }
    }

    void PumpTextureLoads(string? contentRoot, Port.Content.AczOnDemandFetcher? fetcher)
    {
        if (contentRoot is null)
            return;

        var loadsBudget = _frames < _burstLoadsUntilFrame ? LoadsPerFrameBurst : LoadsPerFrame;
        if (_pendingTexLoad.Count > 40 || _texRetryAtFrame.Count > 20)
            loadsBudget = Math.Max(loadsBudget, LoadsPerFrameBurst);

        for (var n = 0; n < loadsBudget && _pendingTexLoad.Count > 0; n++)
        {
            var key = _pendingTexLoad.Dequeue();
            SplitTexKey(key, out var path, out var preferredState);
            try
            {
                var mustLoad = _texNeeded.Contains(key) || _texNeeded.Contains(path)
                               || (_recentlyVisibleUntil.TryGetValue(key, out var until) && _frames <= until);
                if (_texCache.Count >= MaxTexCache || _texBytes >= MaxTexBytes)
                {
                    // Aggressive room-making for visible keys; never refuse a must-load.
                    TryEvictTextures(mustLoad ? 256 : 64);
                    if ((_texCache.Count >= MaxTexCache || _texBytes >= MaxTexBytes) && mustLoad)
                        ForceEvictOldestUnneeded(128);
                    // Soft budget only — always load currently needed textures.
                    if ((_texCache.Count >= MaxTexCache || _texBytes >= MaxTexBytes) && !mustLoad)
                    {
                        _pendingTexLoad.Enqueue(key);
                        continue;
                    }
                }

                var wantPin = mustLoad && (path.Contains("Ghost", StringComparison.OrdinalIgnoreCase)
                                           || path.Contains("Parallax", StringComparison.OrdinalIgnoreCase)
                                           || string.Equals(path, ParallaxLayerPath, StringComparison.OrdinalIgnoreCase)
                                           || IsFloorTileKey(path));

                // Floor tiles / parallax are plain PNGs, not RSI/rsic.
                if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                {
                    var pngFull = ResolvePngPath(contentRoot, path);
                    if (pngFull is null)
                    {
                        fetcher?.EnsureFile(
                            path.StartsWith("Textures/", StringComparison.OrdinalIgnoreCase)
                                ? path
                                : "Textures/" + path.TrimStart('/'));
                        _queuedTex.Remove(key);
                        ScheduleTexRetry(key, soft: true);
                        continue;
                    }

                    var entry = LoadPngTextureEntry(pngFull);
                    if (entry.Id != 0)
                    {
                        StoreTexture(path, entry with
                        {
                            LastUsedFrame = _frames,
                            Pinned = wantPin,
                        });
                        _queuedTex.Remove(key);
                        ClearTexRetry(key);
                    }
                    else
                    {
                        _queuedTex.Remove(key);
                        ScheduleTexRetry(key, soft: false);
                    }

                    continue;
                }

                // Folder RSI requires an explicit state — never load first meta PNG.
                if (string.IsNullOrWhiteSpace(preferredState))
                {
                    _queuedTex.Remove(key);
                    continue;
                }

                var src = Port.Content.RsiMeta.FindRsiSource(contentRoot, path, preferredState);
                if (src is null)
                {
                    _ = Port.Content.RsiMeta.TryGetPreviewFrameOrFetch(
                        contentRoot, path, fetcher, preferredState: preferredState);
                    _queuedTex.Remove(key);
                    ScheduleTexRetry(key, soft: true);
                    NoteMiss("noSrc", path, preferredState);
                    continue;
                }

                // Folder RSI: load the PNG for the requested IconSmooth/sprite state.
                // Packed .rsic: one atlas texture shared by all states (cache under path).
                var atlas = Port.Content.RsiAtlas.TryLoad(src.Value.Path);
                var frame = Port.Content.RsiMeta.TryGetPreviewFrame(src.Value.Path, preferredState);
                if (frame is null)
                {
                    if (fetcher is not null && fetcher.IsReady)
                    {
                        foreach (var cand in Port.Content.AczOnDemandFetcher.CandidateTexturePaths(path, preferredState))
                            fetcher.EnsureFile(cand);
                    }

                    _queuedTex.Remove(key);
                    ScheduleTexRetry(key, soft: true);
                    NoteMiss(src.Value.IsRsic ? "noFrame.rsic" : "noFrame.folder", path, preferredState);
                    continue;
                }

                var folderMode = !src.Value.IsRsic;
                var storeKey = src.Value.IsRsic ? path : key;
                var rsiEntry = LoadTextureEntry(frame.Value, atlas, src.Value.Path, folderMode: folderMode);
                if (rsiEntry.Id != 0)
                {
                    var pinned = wantPin
                                 || LooksLikeGhostPath(path);
                    StoreTexture(storeKey, rsiEntry with
                    {
                        LastUsedFrame = _frames,
                        Pinned = pinned,
                    });
                    if (atlas is not null)
                    {
                        _atlasMeta[src.Value.Path] = atlas;
                        _atlasMeta[path] = atlas;
                    }
                    _queuedTex.Remove(key);
                    ClearTexRetry(key);
                }
                else
                {
                    _queuedTex.Remove(key);
                    ScheduleTexRetry(key, soft: false);
                }
            }
            catch
            {
                _queuedTex.Remove(key);
                ScheduleTexRetry(key, soft: false);
            }
        }
    }

    /// <summary>Briefly raise LoadsPerFrame after LeavePvs / warp storms (any thread).</summary>
    public void ArmTextureLoadBurst(int durationFrames = 60)
    {
        var frames = Math.Max(1, durationFrames);
        // GL thread consumes via Interlocked.Exchange — never touch _frames here.
        int prev;
        do
        {
            prev = _pendingBurstFrames;
        } while (Interlocked.CompareExchange(ref _pendingBurstFrames, Math.Max(prev, frames), prev) != prev);
    }

    void ScheduleTexRetry(string key, bool soft)
    {
        _texFailCount.TryGetValue(key, out var fails);
        fails++;
        _texFailCount[key] = fails;
        // Soft misses (ACZ still arriving): short retry. Hard/permanent: back off ~2s after N fails.
        int delay;
        if (fails >= 8)
            delay = 120; // ~2s at 60fps
        else if (soft)
            delay = fails <= 2 ? 12 : 36;
        else
            delay = fails <= 2 ? 20 : 48;
        _texRetryAtFrame[key] = _frames + delay;
    }

    void ClearTexRetry(string key)
    {
        _texRetryAtFrame.Remove(key);
        _texFailCount.Remove(key);
    }

    bool TryMakeRoomForTexture()
    {
        if (_texCache.Count < MaxTexCache)
            return true;
        EvictTexturesIfNeeded();
        return _texCache.Count < MaxTexCache;
    }

    void EvictTexturesIfNeeded()
    {
        if (_texCache.Count <= MaxTexCache)
            return;

        _texEvictScratch.Clear();
        foreach (var kv in _texCache)
        {
            if (kv.Value.Pinned) continue;
            if (_texNeeded.Contains(kv.Key)) continue;
            if (_recentlyVisibleUntil.TryGetValue(kv.Key, out var until) && _frames <= until)
                continue;
            _texLastUsed.TryGetValue(kv.Key, out var last);
            // Keep off-screen textures longer so pan/return doesn't blank the station.
            if (_frames - last < 2400)
                continue;
            _texEvictScratch.Add(kv.Key);
        }

        if (_texEvictScratch.Count == 0)
            return;

        _texEvictScratch.Sort((a, b) =>
        {
            _texLastUsed.TryGetValue(a, out var la);
            _texLastUsed.TryGetValue(b, out var lb);
            return la.CompareTo(lb);
        });

        var need = _texCache.Count - MaxTexCache + 8;
        for (var i = 0; i < _texEvictScratch.Count && need > 0; i++)
        {
            EvictOne(_texEvictScratch[i]);
            need--;
        }
    }

    void LoadOnePng(string contentRoot, string path, Port.Content.AczOnDemandFetcher? fetcher)
    {
        var pngFull = ResolvePngPath(contentRoot, path);
        if (pngFull is null)
        {
            fetcher?.EnsureFile(
                path.StartsWith("Textures/", StringComparison.OrdinalIgnoreCase)
                    ? path
                    : "Textures/" + path.TrimStart('/'));
            _queuedTex.Remove(path);
            _texRetryAtFrame[path] = _frames + 30;
            return;
        }

        if (!TryMakeRoomForTexture())
        {
            _queuedTex.Remove(path);
            _texRetryAtFrame[path] = _frames + 90;
            return;
        }

        var entry = LoadPngTextureEntry(pngFull);
        if (entry.Id != 0)
        {
            StoreTexture(path, entry);
            _texLastUsed[path] = _frames;
            _texRetryAtFrame.Remove(path);
        }
        else
        {
            _queuedTex.Remove(path);
            _texRetryAtFrame[path] = _frames + 60;
        }
    }

    void LoadOneRsi(string contentRoot, string pathOrKey, Port.Content.AczOnDemandFetcher? fetcher)
    {
        SplitTexKey(pathOrKey, out var path, out var preferredState);
        var src = Port.Content.RsiMeta.FindRsiSource(contentRoot, path, preferredState);
        if (src is null)
        {
            _ = Port.Content.RsiMeta.TryGetPreviewFrameOrFetch(
                contentRoot, path, fetcher, preferredState: preferredState);
            _queuedTex.Remove(pathOrKey);
            _texRetryAtFrame[pathOrKey] = _frames + 30;
            return;
        }

        var atlas = Port.Content.RsiAtlas.TryLoad(src.Value.Path);
        var frame = Port.Content.RsiMeta.TryGetPreviewFrame(src.Value.Path, preferredState);
        if (frame is null)
        {
            if (fetcher is not null && fetcher.IsReady)
            {
                foreach (var cand in Port.Content.AczOnDemandFetcher.CandidateTexturePaths(path, preferredState))
                    fetcher.EnsureFile(cand);
            }

            _queuedTex.Remove(pathOrKey);
            _texRetryAtFrame[pathOrKey] = _frames + 60;
            return;
        }

        // Use atlas UV whenever meta parsed. FolderMode only means the GPU texture is a
        // single-state sheet (folder RSI), not that we should ignore meta directions.
        var folderMode = !src.Value.IsRsic;
        var atlasKey = src.Value.Path;
        var storeKey = src.Value.IsRsic ? path : pathOrKey;
        if (!TryMakeRoomForTexture())
        {
            _queuedTex.Remove(pathOrKey);
            _texRetryAtFrame[pathOrKey] = _frames + 90;
            return;
        }

        var rsiEntry = LoadTextureEntry(frame.Value, atlas, atlasKey, folderMode);
        if (rsiEntry.Id != 0)
        {
            StoreTexture(storeKey, rsiEntry);
            if (atlas is not null)
            {
                _atlasMeta[atlasKey] = atlas;
                _atlasMeta[path] = atlas;
            }
            _texLastUsed[storeKey] = _frames;
            _texRetryAtFrame.Remove(pathOrKey);
        }
        else
        {
            _queuedTex.Remove(pathOrKey);
            _texRetryAtFrame[pathOrKey] = _frames + 60;
        }
    }

    static string? ResolvePngPath(string contentRoot, string relative)
    {
        relative = relative.Replace('\\', '/').TrimStart('/');
        string[] candidates =
        [
            System.IO.Path.Combine(contentRoot, "Textures", relative.Replace('/', System.IO.Path.DirectorySeparatorChar)),
            System.IO.Path.Combine(contentRoot, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)),
            System.IO.Path.Combine(contentRoot, "Resources", "Textures", relative.Replace('/', System.IO.Path.DirectorySeparatorChar)),
        ];
        if (relative.StartsWith("Textures/", StringComparison.OrdinalIgnoreCase))
        {
            var rest = relative["Textures/".Length..];
            candidates =
            [
                System.IO.Path.Combine(contentRoot, "Textures", rest.Replace('/', System.IO.Path.DirectorySeparatorChar)),
                System.IO.Path.Combine(contentRoot, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)),
                System.IO.Path.Combine(contentRoot, "Resources", "Textures", rest.Replace('/', System.IO.Path.DirectorySeparatorChar)),
            ];
        }

        foreach (var c in candidates)
        {
            if (File.Exists(c))
                return c;
        }

        return null;
    }

    int UploadBitmap(Bitmap bmp)
    {
        var tex = new int[1];
        GLES20.GlGenTextures(1, tex, 0);
        GLES20.GlBindTexture(GLES20.GlTexture2d, tex[0]);
        GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureMinFilter, GLES20.GlLinear);
        GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureMagFilter, GLES20.GlLinear);
        GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureWrapS, GLES20.GlClampToEdge);
        GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureWrapT, GLES20.GlClampToEdge);
        GLUtils.TexImage2D(GLES20.GlTexture2d, 0, bmp, 0);
        return tex[0];
    }

    static TexEntry LoadPngTextureEntry(string pngPath)
    {
        try
        {
            if (!LooksLikeCompletePng(pngPath))
                return default;
            var opts = new BitmapFactory.Options { InScaled = false };
            using var bmp = BitmapFactory.DecodeFile(pngPath, opts);
            if (bmp is null)
                return default;

            var tex = new int[1];
            GLES20.GlGenTextures(1, tex, 0);
            GLES20.GlBindTexture(GLES20.GlTexture2d, tex[0]);
            GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureMinFilter, GLES20.GlNearest);
            GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureMagFilter, GLES20.GlNearest);
            GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureWrapS, GLES20.GlClampToEdge);
            GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureWrapT, GLES20.GlClampToEdge);
            GLUtils.TexImage2D(GLES20.GlTexture2d, 0, bmp, 0);
            var w = bmp.Width;
            var h = bmp.Height;
            // SS14 tile sheets pack many 32×32 variants — always address one cell, never the whole strip.
            const float cell = 32f;
            var fw = w >= 32 ? cell : w;
            var fh = h >= 32 ? cell : h;
            var u1 = Math.Min(1f, fw / Math.Max(1, w));
            var v1 = Math.Min(1f, fh / Math.Max(1, h));
            bmp.Recycle();
            return new TexEntry(tex[0], fw, fh, 0, 0, u1, v1, null, false, 0, false, w, h);
        }
        catch (Exception ex)
        {
            Port.Content.DiagLog.Warn($"tex png FAIL {System.IO.Path.GetFileName(pngPath)}: {ex.GetType().Name}");
            return default;
        }
    }

    static TexEntry LoadTextureEntry(
        Port.Content.RsiMeta.FrameInfo frame,
        Port.Content.RsiAtlas.Loaded? atlas,
        string atlasKey,
        bool folderMode)
    {
        try
        {
            if (!LooksLikeCompletePng(frame.PngPath))
                return default;
            var opts = new BitmapFactory.Options { InScaled = false };
            using var bmp = BitmapFactory.DecodeFile(frame.PngPath, opts);
            if (bmp is null)
                return default;

            var tex = new int[1];
            GLES20.GlGenTextures(1, tex, 0);
            GLES20.GlBindTexture(GLES20.GlTexture2d, tex[0]);
            GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureMinFilter, GLES20.GlNearest);
            GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureMagFilter, GLES20.GlNearest);
            GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureWrapS, GLES20.GlClampToEdge);
            GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureWrapT, GLES20.GlClampToEdge);
            GLUtils.TexImage2D(GLES20.GlTexture2d, 0, bmp, 0);

            var fw = atlas?.FrameW ?? Math.Max(1, frame.FrameW);
            var fh = atlas?.FrameH ?? Math.Max(1, frame.FrameH);
            var aw = bmp.Width;
            var ah = bmp.Height;
            // Always store first-frame UVs as safe default; Sample overrides when atlas meta exists.
            var u1 = Math.Min(1f, fw / (float)Math.Max(1, aw));
            var v1 = Math.Min(1f, fh / (float)Math.Max(1, ah));
            bmp.Recycle();
            return new TexEntry(tex[0], fw, fh, 0f, 0f, u1, v1, atlasKey, folderMode, 0, false, aw, ah);
        }
        catch (Exception ex)
        {
            Port.Content.DiagLog.Warn($"tex rsi FAIL {System.IO.Path.GetFileName(frame.PngPath)}: {ex.GetType().Name}");
            return default;
        }
    }

    static int LoadTexture(string pngPath)
    {
        try
        {
            if (!LooksLikeCompletePng(pngPath))
                return 0;
            var opts = new BitmapFactory.Options { InScaled = false };
            using var bmp = BitmapFactory.DecodeFile(pngPath, opts);
            if (bmp is null)
                return 0;

            var tex = new int[1];
            GLES20.GlGenTextures(1, tex, 0);
            GLES20.GlBindTexture(GLES20.GlTexture2d, tex[0]);
            GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureMinFilter, GLES20.GlNearest);
            GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureMagFilter, GLES20.GlNearest);
            GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureWrapS, GLES20.GlClampToEdge);
            GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureWrapT, GLES20.GlClampToEdge);
            GLUtils.TexImage2D(GLES20.GlTexture2d, 0, bmp, 0);
            bmp.Recycle();
            return tex[0];
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>Reject truncated ACZ downloads before Skia touches them.</summary>
    static bool LooksLikeCompletePng(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length < 33) // sig+IHDR minimum
                return false;
            Span<byte> hdr = stackalloc byte[8];
            using var fs = File.OpenRead(path);
            if (fs.Read(hdr) != 8)
                return false;
            return hdr[0] == 0x89 && hdr[1] == (byte)'P' && hdr[2] == (byte)'N' && hdr[3] == (byte)'G';
        }
        catch
        {
            return false;
        }
    }

    void EnsureBuffers(int verts)
    {
        // FloatBuffer.Capacity() is element count.
        var posFloats = verts * 2;
        var colFloats = verts * 4;
        if (_posBuf is null || _posBuf.Capacity() < posFloats)
            _posBuf = ByteBuffer.AllocateDirect(Math.Max(posFloats * sizeof(float), 4096)).Order(ByteOrder.NativeOrder())!.AsFloatBuffer();
        if (_colBuf is null || _colBuf.Capacity() < colFloats)
            _colBuf = ByteBuffer.AllocateDirect(Math.Max(colFloats * sizeof(float), 8192)).Order(ByteOrder.NativeOrder())!.AsFloatBuffer();
    }

    /// <summary>
    /// Approximate PC DrawFov: darken beyond nearby wall/window occluders with radial falloff.
    /// Not full Clyde shadow FoV — enough for ghost observe readability.
    /// </summary>
    void DrawFovOcclusionApprox(
        EntitySprite[] ents, int count,
        float camX, float camY, float zoom, float cosR, float sinR)
    {
        if (_program == 0)
            return;

        // Soft vignette base (always).
        DrawSoftVignette(0.35f);

        // Shadow wedges behind wall-like occluders near the eye.
        var halfW = _width * 0.5f;
        var halfH = _height * 0.5f;
        var eyeReach = MathF.Max(halfW, halfH) * 1.15f;
        var vert = 0;
        const float shadowA = 0.42f;

        for (var i = 0; i < count && vert + 6 < MaxVerts; i++)
        {
            ref readonly var e = ref ents[i];
            if (e.IsControlled || string.IsNullOrEmpty(e.RsiPath))
                continue;
            if (!IsOccluderPath(e.RsiPath!))
                continue;

            var wx = e.X * PixelsPerTile;
            var wy = e.Y * PixelsPerTile;
            var dx = wx - camX;
            var dy = wy - camY;
            var sx = (dx * cosR - dy * sinR) * zoom;
            var sy = (dx * sinR + dy * cosR) * zoom;
            var dist = MathF.Sqrt(sx * sx + sy * sy);
            if (dist < 8f || dist > eyeReach)
                continue;

            // Extrude a dark quad away from eye through the occluder.
            var nx = sx / dist;
            var ny = sy / dist;
            var px = -ny;
            var py = nx;
            var half = 18f * zoom;
            var near = dist + 6f * zoom;
            var far = Math.Min(eyeReach, dist + 140f * zoom);
            void Put(float x, float y, float a)
            {
                _posScratch[vert * 2] = x;
                _posScratch[vert * 2 + 1] = y;
                _colScratch[vert * 4] = 0.01f;
                _colScratch[vert * 4 + 1] = 0.012f;
                _colScratch[vert * 4 + 2] = 0.02f;
                _colScratch[vert * 4 + 3] = a;
                vert++;
            }

            Put(sx + px * half, sy + py * half, shadowA * 0.55f);
            Put(sx - px * half, sy - py * half, shadowA * 0.55f);
            Put(nx * far - px * half * 1.6f, ny * far - py * half * 1.6f, shadowA);
            Put(sx + px * half, sy + py * half, shadowA * 0.55f);
            Put(nx * far - px * half * 1.6f, ny * far - py * half * 1.6f, shadowA);
            Put(nx * far + px * half * 1.6f, ny * far + py * half * 1.6f, shadowA);
            _ = near;
        }

        if (vert <= 0)
            return;

        EnsureBuffers(vert);
        _posBuf!.Position(0);
        _posBuf.Put(_posScratch, 0, vert * 2);
        _posBuf.Position(0);
        _colBuf!.Position(0);
        _colBuf.Put(_colScratch, 0, vert * 4);
        _colBuf.Position(0);
        GLES20.GlUseProgram(_program);
        GLES20.GlEnable(GLES20.GlBlend);
        GLES20.GlBlendFunc(GLES20.GlSrcAlpha, GLES20.GlOneMinusSrcAlpha);
        GLES20.GlUniform2f(_uScreen, _width, _height);
        GLES20.GlEnableVertexAttribArray(_aPos);
        GLES20.GlEnableVertexAttribArray(_aColor);
        GLES20.GlVertexAttribPointer(_aPos, 2, GLES20.GlFloat, false, 0, _posBuf);
        GLES20.GlVertexAttribPointer(_aColor, 4, GLES20.GlFloat, false, 0, _colBuf);
        GLES20.GlDrawArrays(GLES20.GlTriangles, 0, vert);
        GLES20.GlDisableVertexAttribArray(_aPos);
        GLES20.GlDisableVertexAttribArray(_aColor);
    }

    void DrawSoftVignette(float strength)
    {
        if (_program == 0 || _width <= 0 || _height <= 0)
            return;

        var halfW = _width * 0.5f;
        var halfH = _height * 0.5f;
        var vert = 0;
        // Four edge strips — cheap FoV-off / ambient darken without a second shader.
        void Quad(float x0, float y0, float x1, float y1, float a0, float a1)
        {
            if (vert + 6 > MaxVerts) return;
            void Put(float x, float y, float a)
            {
                _posScratch[vert * 2] = x;
                _posScratch[vert * 2 + 1] = y;
                _colScratch[vert * 4] = 0f;
                _colScratch[vert * 4 + 1] = 0f;
                _colScratch[vert * 4 + 2] = 0f;
                _colScratch[vert * 4 + 3] = a;
                vert++;
            }

            Put(x0, y0, a0); Put(x1, y0, a1); Put(x1, y1, a1);
            Put(x0, y0, a0); Put(x1, y1, a1); Put(x0, y1, a0);
        }

        var band = Math.Min(halfW, halfH) * 0.42f;
        var a = Math.Clamp(strength, 0f, 0.7f);
        Quad(-halfW, -halfH, -halfW + band, halfH, a, 0f); // left
        Quad(halfW - band, -halfH, halfW, halfH, 0f, a); // right
        Quad(-halfW, -halfH, halfW, -halfH + band, a, 0f); // bottom
        Quad(-halfW, halfH - band, halfW, halfH, 0f, a); // top

        if (vert <= 0) return;
        EnsureBuffers(vert);
        _posBuf!.Position(0);
        _posBuf.Put(_posScratch, 0, vert * 2);
        _posBuf.Position(0);
        _colBuf!.Position(0);
        _colBuf.Put(_colScratch, 0, vert * 4);
        _colBuf.Position(0);
        GLES20.GlUseProgram(_program);
        GLES20.GlEnable(GLES20.GlBlend);
        GLES20.GlBlendFunc(GLES20.GlSrcAlpha, GLES20.GlOneMinusSrcAlpha);
        GLES20.GlUniform2f(_uScreen, _width, _height);
        GLES20.GlEnableVertexAttribArray(_aPos);
        GLES20.GlEnableVertexAttribArray(_aColor);
        GLES20.GlVertexAttribPointer(_aPos, 2, GLES20.GlFloat, false, 0, _posBuf);
        GLES20.GlVertexAttribPointer(_aColor, 4, GLES20.GlFloat, false, 0, _colBuf);
        GLES20.GlDrawArrays(GLES20.GlTriangles, 0, vert);
        GLES20.GlDisableVertexAttribArray(_aPos);
        GLES20.GlDisableVertexAttribArray(_aColor);
    }

    static bool IsOccluderPath(string path) =>
        path.Contains("Wall", StringComparison.OrdinalIgnoreCase)
        || path.Contains("Window", StringComparison.OrdinalIgnoreCase)
        || path.Contains("Grille", StringComparison.OrdinalIgnoreCase)
        || path.Contains("Airlock", StringComparison.OrdinalIgnoreCase)
        || path.Contains("Firelock", StringComparison.OrdinalIgnoreCase)
        || path.Contains("Door", StringComparison.OrdinalIgnoreCase)
        || path.Contains("Shutter", StringComparison.OrdinalIgnoreCase);

    void InitProgram()
    {
        const string vs = """
            attribute vec2 a_pos;
            attribute vec4 a_color;
            uniform vec2 u_screen;
            varying vec4 v_color;
            void main() {
              vec2 ndc = vec2(a_pos.x / (u_screen.x * 0.5), a_pos.y / (u_screen.y * 0.5));
              gl_Position = vec4(ndc, 0.0, 1.0);
              v_color = a_color;
            }
            """;
        const string fs = """
            precision mediump float;
            varying vec4 v_color;
            void main() {
              gl_FragColor = v_color;
            }
            """;

        var v = Compile(GLES20.GlVertexShader, vs);
        var f = Compile(GLES20.GlFragmentShader, fs);
        _program = GLES20.GlCreateProgram();
        GLES20.GlAttachShader(_program, v);
        GLES20.GlAttachShader(_program, f);
        GLES20.GlLinkProgram(_program);
        _aPos = GLES20.GlGetAttribLocation(_program, "a_pos");
        _aColor = GLES20.GlGetAttribLocation(_program, "a_color");
        _uScreen = GLES20.GlGetUniformLocation(_program, "u_screen");
        _uCam = GLES20.GlGetUniformLocation(_program, "u_cam");

        const string tvs = """
            attribute vec2 a_pos;
            attribute vec2 a_uv;
            attribute vec4 a_color;
            uniform vec2 u_screen;
            varying vec2 v_uv;
            varying vec4 v_color;
            void main() {
              vec2 ndc = vec2(a_pos.x / (u_screen.x * 0.5), a_pos.y / (u_screen.y * 0.5));
              gl_Position = vec4(ndc, 0.0, 1.0);
              v_uv = a_uv;
              v_color = a_color;
            }
            """;
        const string tfs = """
            precision mediump float;
            varying vec2 v_uv;
            varying vec4 v_color;
            uniform sampler2D u_tex;
            uniform vec4 u_tint;
            void main() {
              vec4 c = texture2D(u_tex, v_uv);
              if (c.a < 0.05) discard;
              gl_FragColor = c * v_color * u_tint;
            }
            """;
        var tv = Compile(GLES20.GlVertexShader, tvs);
        var tf = Compile(GLES20.GlFragmentShader, tfs);
        _texProgram = GLES20.GlCreateProgram();
        GLES20.GlAttachShader(_texProgram, tv);
        GLES20.GlAttachShader(_texProgram, tf);
        GLES20.GlLinkProgram(_texProgram);
        _texAPos = GLES20.GlGetAttribLocation(_texProgram, "a_pos");
        _texAUv = GLES20.GlGetAttribLocation(_texProgram, "a_uv");
        _texAColor = GLES20.GlGetAttribLocation(_texProgram, "a_color");
        _texUScreen = GLES20.GlGetUniformLocation(_texProgram, "u_screen");
        _texUSampler = GLES20.GlGetUniformLocation(_texProgram, "u_tex");
        _texUTint = GLES20.GlGetUniformLocation(_texProgram, "u_tint");
        _uvBuf = ByteBuffer.AllocateDirect(12 * sizeof(float)).Order(ByteOrder.NativeOrder())!.AsFloatBuffer();

        _glReady = _program != 0;
    }

    static int Compile(int type, string src)
    {
        var sh = GLES20.GlCreateShader(type);
        GLES20.GlShaderSource(sh, src);
        GLES20.GlCompileShader(sh);
        return sh;
    }
}
