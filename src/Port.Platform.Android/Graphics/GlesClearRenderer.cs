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
    }

    public struct TileSprite
    {
        public float X;
        public float Y;
        public byte R, G, B;
        public string? RsiPath;
        public string? StateName;
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
    int _entityCount;
    TileSprite[] _tiles = Array.Empty<TileSprite>();
    int _tileCount;
    SpeechBubbleSprite[] _bubbles = Array.Empty<SpeechBubbleSprite>();
    int _bubbleCount;
    string? _contentFilesRoot;
    Port.Content.AczOnDemandFetcher? _texFetcher;
    int _drawnLast;
    int _texturedLast;
    int _tilesDrawnLast;
    int _texMissLast;
    int _rsiPathLast;
    int _texCached;

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
        int AtlasPixelW = 0,
        int AtlasPixelH = 0);

    readonly Dictionary<string, TexEntry> _texCache = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Port.Content.RsiAtlas.Loaded> _atlasMeta = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, long> _texLastUsed = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _texNeeded = new(StringComparer.OrdinalIgnoreCase);
    readonly Queue<string> _pendingPngLoad = new();
    readonly Queue<string> _pendingRsiLoad = new();
    readonly HashSet<string> _queuedTex = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, long> _texRetryAtFrame = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> _texEvictScratch = new();
    // Keep well under typical mobile GLES texture limits. Oversizing + hard eviction
    // deleted live textures → black screen (dead ids / OOM thrash).
    const int MaxTexCache = 768;

    // Clyde-style texture batching: one draw call per RSI bind.
    readonly List<TexQuad> _texQuads = new(2048);
    const int MaxBatchQuads = 384;
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

    const int MaxEntities = 4800;
    const int MaxTileSolidQuads = 6000;
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
            {
                _pulse = false;
                // Prefetch observer RSI so the ghost is visible ASAP.
                QueueTexture("Mobs/Ghosts/ghost_human.rsi");
            }
        }
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
            if (_entities.Length < count)
                _entities = new EntitySprite[Math.Max(count, 256)];
            if (count > 0)
                Array.Copy(entities, _entities, count);
            _entityCount = count;
        }
    }

    public void SetTiles(TileSprite[] tiles, int count)
    {
        lock (_gate)
        {
            if (_tiles.Length < count)
                _tiles = new TileSprite[Math.Max(count, 512)];
            if (count > 0)
                Array.Copy(tiles, _tiles, count);
            _tileCount = count;
        }
    }

    public void SetSpeechBubbles(SpeechBubbleSprite[] bubbles, int count)
    {
        lock (_gate)
        {
            if (_bubbles.Length < count)
                _bubbles = new SpeechBubbleSprite[Math.Max(count, 16)];
            if (count > 0)
                Array.Copy(bubbles, _bubbles, count);
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
            return _ghostMode
                ? $"ents={_entityCount} tiles={_tileCount} tex={_texturedLast}"
                : $"gles: OK {_width}x{_height}";
        }
    }

    public void OnSurfaceCreated(IGL10? gl, EGLConfig? config)
    {
        try
        {
            GLES20.GlClearColor(0.02f, 0.03f, 0.06f, 1f);
            GLES20.GlEnable(GLES20.GlBlend);
            GLES20.GlBlendFunc(GLES20.GlSrcAlpha, GLES20.GlOneMinusSrcAlpha);
            // Context (re)create invalidates every GL texture id — drop CPU-side cache or
            // we keep drawing dead ids forever → black screen, no tile/sprite fallbacks.
            lock (_gate)
                DropAllTexturesUnlocked();
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

    void DropAllTexturesUnlocked()
    {
        // Do not GlDeleteTextures — context is already gone/new.
        _texCache.Clear();
        _atlasMeta.Clear();
        _texLastUsed.Clear();
        _texNeeded.Clear();
        _queuedTex.Clear();
        _texRetryAtFrame.Clear();
        _pendingPngLoad.Clear();
        _pendingRsiLoad.Clear();
        _bubbleTex.Clear();
        _texQuads.Clear();
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
        bool pulse, ghost;
        float camX, camY, camRot, zoom;
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
            // Pin every sprite/tile path used this frame BEFORE any load/evict (walls were
            // disappearing mid-flight when tile loads evicted RSI not yet marked needed).
            for (var i = 0; i < count; i++)
            {
                var p = ents[i].RsiPath;
                if (!string.IsNullOrEmpty(p))
                    _texNeeded.Add(p);
            }
            for (var i = 0; i < tileCount; i++)
            {
                var p = tiles[i].RsiPath;
                if (!string.IsNullOrEmpty(p))
                    _texNeeded.Add(p);
            }
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
                if (_fullbright)
                {
                    r = 0.08f;
                    g = 0.09f;
                    b = 0.11f;
                }
                else
                {
                    r = 0.02f;
                    g = 0.025f;
                    b = 0.04f;
                }
            }
        }

        GLES20.GlClearColor(r, g, b, 1f);
        GLES20.GlClear(GLES20.GlColorBufferBit);

        if (!ghost || !_glReady || _width <= 0 || _height <= 0)
        {
            lock (_gate) { _drawnLast = 0; _texturedLast = 0; _tilesDrawnLast = 0; }
            return;
        }

        var cosR = MathF.Cos(-camRot);
        var sinR = MathF.Sin(-camRot);

        DrawWorldGrid(camX, camY, zoom, cosR, sinR);

        // One texture pump per frame (was duplicated in tiles + entities).
        PumpTextureLoads(contentRoot, texFetcher);

        DrawTiles(tiles, tileCount, camX, camY, zoom, cosR, sinR, contentRoot, texFetcher);

        if (count > 0)
            DrawEntities(ents, count, camX, camY, zoom, cosR, sinR, camRot, contentRoot, texFetcher);

        DrawSpeechBubbles(bubbles, bubbleCount, camX, camY, zoom, cosR, sinR);

        bool fov;
        lock (_gate) fov = _drawFov && !_fullbright;
        if (fov)
            DrawFovVignette();
    }

    void DrawFovVignette()
    {
        // Soft edge darkening — PC DrawFov approximation (full occlusion later).
        if (_program == 0 || _width <= 0 || _height <= 0)
            return;

        var vert = 0;
        var need = 24 * 2;
        if (_posScratch.Length < need) _posScratch = new float[need];
        if (_colScratch.Length < need * 2) _colScratch = new float[need * 2];

        var hx = _width * 0.5f;
        var hy = _height * 0.5f;
        var edge = MathF.Min(_width, _height) * 0.22f;
        const float ca = 0.55f;

        void Put(float px, float py, float a)
        {
            _posScratch[vert * 2] = px;
            _posScratch[vert * 2 + 1] = py;
            _colScratch[vert * 4] = 0f;
            _colScratch[vert * 4 + 1] = 0f;
            _colScratch[vert * 4 + 2] = 0f;
            _colScratch[vert * 4 + 3] = a;
            vert++;
        }

        void Quad(float x0, float y0, float x1, float y1, float a)
        {
            Put(x0, y0, a); Put(x1, y0, a); Put(x1, y1, a);
            Put(x0, y0, a); Put(x1, y1, a); Put(x0, y1, a);
        }

        Quad(-hx, hy - edge, hx, hy, ca);           // top
        Quad(-hx, -hy, hx, -hy + edge, ca);         // bottom
        Quad(-hx, -hy, -hx + edge, hy, ca * 0.85f); // left
        Quad(hx - edge, -hy, hx, hy, ca * 0.85f);   // right

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

    void DrawEntities(
        EntitySprite[] ents, int count,
        float camX, float camY, float zoom, float cosR, float sinR, float camRot,
        string? contentRoot, Port.Content.AczOnDemandFetcher? texFetcher)
    {
        var halfW = _width * 0.5f;
        var halfH = _height * 0.5f;
        var vert = 0;
        var textured = 0;
        var rsiPaths = 0;
        var viewPad = 200f / zoom;
        var animTime = _frames / 60.0;
        _texQuads.Clear();

        // Prioritize own ghost + nearby mobs at front of load queue.
        string? controlledPath = null;
        for (var i = 0; i < count; i++)
        {
            if (!ents[i].IsControlled || string.IsNullOrEmpty(ents[i].RsiPath)) continue;
            controlledPath = ents[i].RsiPath;
            break;
        }

        if (controlledPath is not null)
            QueueTexturePriority(controlledPath);

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

            if (!string.IsNullOrEmpty(e.Label))
            {
                DrawNameplate(sx, sy + 28f, e.Label!, e.IsControlled);
                if (string.IsNullOrEmpty(e.RsiPath))
                    continue;
            }

            if (!string.IsNullOrEmpty(e.RsiPath))
            {
                rsiPaths++;
                if (contentRoot is not null)
                    QueueTexture(e.RsiPath!);
            }

            TexEntry tex = default;
            var hasTex = !string.IsNullOrEmpty(e.RsiPath)
                         && _texCache.TryGetValue(e.RsiPath!, out tex)
                         && tex.Id != 0;
            if (hasTex)
                _texLastUsed[e.RsiPath!] = _frames;

            // PC SpriteSystem: world rotation → RSI meta directions.
            var eyeRelRot = e.NoRotation ? 0f : e.Rotation;
            if (hasTex)
            {
                var uv = ResolveUv(tex, e.StateName, eyeRelRot, animTime, e.DirOverride);
                var sizeX = Math.Max(8f, uv.FrameW) * zoom;
                var sizeY = Math.Max(8f, uv.FrameH) * zoom;
                if (e.IsControlled)
                {
                    sizeX *= 1.15f;
                    sizeY *= 1.15f;
                }

                // PC GhostSystem translucency for observer sprites.
                var alpha = e.IsControlled ? 0.92f : 1f;
                if (LooksLikeGhostPath(e.RsiPath))
                    alpha = e.IsControlled ? 0.9f : 0.7f;

                _texQuads.Add(new TexQuad(
                    tex.Id, e.DrawDepth, sy, sx, sy, sizeX, sizeY, 0f,
                    e.R / 255f, e.G / 255f, e.B / 255f, alpha,
                    uv.U0, uv.V0, uv.U1, uv.V1));
                textured++;
                continue;
            }

            // Keep a faint placeholder while RSI loads — skipping caused blink / "missing" ents.
            if (vert + 6 > MaxVerts)
                continue;

            var marker = (e.IsControlled ? 28f : (string.IsNullOrEmpty(e.RsiPath) ? 10f : 16f)) * zoom;
            float cr = e.R / 255f, cg = e.G / 255f, cb = e.B / 255f, ca = 0.35f;
            if (e.IsControlled) { cr = 0.85f; cg = 0.92f; cb = 1f; ca = 0.85f; }
            else if (string.IsNullOrEmpty(e.RsiPath)) { cr = 0.35f; cg = 0.45f; cb = 0.6f; ca = 0.4f; }

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
            _texMissLast = _texRetryAtFrame.Count;
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
        var posBytes = MaxBatchQuads * 12 * sizeof(float);
        var colBytes = MaxBatchQuads * 24 * sizeof(float);
        if (_batchPosBuf is null || _batchPosBuf.Capacity() < posBytes)
            _batchPosBuf = ByteBuffer.AllocateDirect(posBytes).Order(ByteOrder.NativeOrder())!.AsFloatBuffer();
        if (_batchUvBuf is null || _batchUvBuf.Capacity() < posBytes)
            _batchUvBuf = ByteBuffer.AllocateDirect(posBytes).Order(ByteOrder.NativeOrder())!.AsFloatBuffer();
        if (_batchColBuf is null || _batchColBuf.Capacity() < colBytes)
            _batchColBuf = ByteBuffer.AllocateDirect(colBytes).Order(ByteOrder.NativeOrder())!.AsFloatBuffer();
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
        // Prefer RSI meta atlas Sample (exact state + directions + delays) whenever available.
        if (tex.AtlasKey is not null
            && (_atlasMeta.TryGetValue(tex.AtlasKey, out var atlas)
                || _atlasMeta.TryGetValue(tex.AtlasKey.Replace('\\', '/'), out atlas)))
        {
            var animTime = ShouldAnimate(state) ? time : 0;
            // Folder RSI: one state sheet PNG → UV inside that sheet. Packed .rsic → full atlas.
            return Port.Content.RsiAtlas.Sample(
                atlas, state, rotation, animTime, folderPerStateSheet: tex.FolderMode, dirOverride);
        }

        // No meta: first cell only — never the whole packed sheet.
        return SingleCellUv(tex);
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
        string? contentRoot, Port.Content.AczOnDemandFetcher? fetcher)
    {
        if (tileCount <= 0)
        {
            lock (_gate) _tilesDrawnLast = 0;
            return;
        }

        var halfW = _width * 0.5f;
        var halfH = _height * 0.5f;
        var size = PixelsPerTile * zoom;
        var pad = size * 1.5f;
        var vert = 0;
        var drawn = 0;
        _texQuads.Clear();
        // Tile quads rotate with the camera so floors stay aligned to the grid.
        var tileCamRot = MathF.Atan2(sinR, cosR);

        for (var i = 0; i < tileCount; i++)
        {
            ref readonly var t = ref tiles[i];
            var dx = t.X * PixelsPerTile - camX;
            var dy = t.Y * PixelsPerTile - camY;
            var sx = (dx * cosR - dy * sinR) * zoom;
            var sy = (dx * sinR + dy * cosR) * zoom;
            if (MathF.Abs(sx) > halfW + pad || MathF.Abs(sy) > halfH + pad)
                continue;

            if (!string.IsNullOrEmpty(t.RsiPath) && contentRoot is not null)
                QueueTexture(t.RsiPath!);

            var mir = t.RotationMirroring % 4;
            var tileRot = tileCamRot + mir * (MathF.PI * 0.5f);

            if (!string.IsNullOrEmpty(t.RsiPath)
                && _texCache.TryGetValue(t.RsiPath!, out var tex)
                && tex.Id != 0)
            {
                _texLastUsed[t.RsiPath!] = _frames;
                var uv = ResolveTileUv(tex, t.Variant);
                _texQuads.Add(new TexQuad(
                    tex.Id, -100, sy, sx, sy, size, size, tileRot,
                    t.R / 255f, t.G / 255f, t.B / 255f, 1f,
                    uv.U0, uv.V0, uv.U1, uv.V1));
                drawn++;
                continue;
            }

            if (_program == 0 || vert + 6 > MaxVerts)
                continue;

            var cr = t.R / 255f;
            var cg = t.G / 255f;
            var cb = t.B / 255f;
            const float ca = 0.9f;
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

    void DrawWorldGrid(float camX, float camY, float zoom, float cosR, float sinR)
    {
        if (_program == 0 || _width <= 0 || _height <= 0)
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

        if (_posBuf is null || _posBuf.Capacity() < 12 * sizeof(float))
            _posBuf = ByteBuffer.AllocateDirect(12 * sizeof(float) * 4).Order(ByteOrder.NativeOrder())!.AsFloatBuffer();
        _posBuf.Position(0);
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

    void QueueTexturePriority(string rsiPath)
    {
        if (string.IsNullOrWhiteSpace(rsiPath))
            return;
        _texNeeded.Add(rsiPath);
        if (_texCache.ContainsKey(rsiPath))
        {
            _texLastUsed[rsiPath] = _frames;
            return;
        }

        if (_queuedTex.Contains(rsiPath))
            return;
        _queuedTex.Add(rsiPath);
        // Jump the queue: drain into temp then re-enqueue priority first.
        if (rsiPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            var rest = _pendingPngLoad.ToArray();
            _pendingPngLoad.Clear();
            _pendingPngLoad.Enqueue(rsiPath);
            foreach (var p in rest)
                if (!string.Equals(p, rsiPath, StringComparison.OrdinalIgnoreCase))
                    _pendingPngLoad.Enqueue(p);
        }
        else
        {
            var rest = _pendingRsiLoad.ToArray();
            _pendingRsiLoad.Clear();
            _pendingRsiLoad.Enqueue(rsiPath);
            foreach (var p in rest)
                if (!string.Equals(p, rsiPath, StringComparison.OrdinalIgnoreCase))
                    _pendingRsiLoad.Enqueue(p);
        }
    }

    void QueueTexture(string rsiPath)
    {
        if (string.IsNullOrWhiteSpace(rsiPath))
            return;

        _texNeeded.Add(rsiPath);
        if (_texCache.ContainsKey(rsiPath))
        {
            _texLastUsed[rsiPath] = _frames;
            return;
        }

        if (_queuedTex.Contains(rsiPath))
            return;
        if (_texRetryAtFrame.TryGetValue(rsiPath, out var retryAt) && _frames < retryAt)
            return;

        if (rsiPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            if (_pendingPngLoad.Count > 400)
                TrimPendingQueue(_pendingPngLoad, keep: 220);
            if (_pendingPngLoad.Count > 400)
                return;
            _queuedTex.Add(rsiPath);
            _pendingPngLoad.Enqueue(rsiPath);
            return;
        }

        if (_pendingRsiLoad.Count > 350)
            TrimPendingQueue(_pendingRsiLoad, keep: 200);
        if (_pendingRsiLoad.Count > 350)
            return;
        _queuedTex.Add(rsiPath);
        _pendingRsiLoad.Enqueue(rsiPath);
    }

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

    void PumpTextureLoads(string? contentRoot, Port.Content.AczOnDemandFetcher? fetcher)
    {
        if (contentRoot is null)
            return;

        // Floors + RSI: keep GL thread busy loading — blue dots were from starve.
        var pngBudget = _texCache.Count < MaxTexCache * 3 / 4 ? 16 : 10;
        var rsiBudget = _texCache.Count < MaxTexCache * 3 / 4 ? 12 : 8;

        for (var n = 0; n < pngBudget && _pendingPngLoad.Count > 0; n++)
        {
            var path = _pendingPngLoad.Dequeue();
            try { LoadOnePng(contentRoot, path, fetcher); }
            catch
            {
                _queuedTex.Remove(path);
                _texRetryAtFrame[path] = _frames + 45;
            }
        }

        for (var n = 0; n < rsiBudget && _pendingRsiLoad.Count > 0; n++)
        {
            var path = _pendingRsiLoad.Dequeue();
            try { LoadOneRsi(contentRoot, path, fetcher); }
            catch
            {
                _queuedTex.Remove(path);
                _texRetryAtFrame[path] = _frames + 45;
            }
        }
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
            // Never delete textures still required by the current frame.
            if (_texNeeded.Contains(kv.Key))
                continue;
            if (IsPinnedTexture(kv.Key))
                continue;
            _texLastUsed.TryGetValue(kv.Key, out var last);
            // Soft-age only — refuse new loads rather than thrashing live sprites.
            if (_frames - last < 900)
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
            var key = _texEvictScratch[i];
            if (!_texCache.TryGetValue(key, out var tex))
                continue;
            if (_texNeeded.Contains(key))
                continue;
            if (tex.Id != 0)
                GLES20.GlDeleteTextures(1, new[] { tex.Id }, 0);
            if (tex.AtlasKey is not null)
            {
                _atlasMeta.Remove(tex.AtlasKey);
                _atlasMeta.Remove(key);
            }
            _texCache.Remove(key);
            _texLastUsed.Remove(key);
            _queuedTex.Remove(key);
            need--;
        }
    }

    static bool IsPinnedTexture(string path) =>
        path.Contains("/Walls/", StringComparison.OrdinalIgnoreCase)
        || path.Contains("Structures/Walls", StringComparison.OrdinalIgnoreCase)
        || path.Contains("/Windows/", StringComparison.OrdinalIgnoreCase)
        || path.Contains("Structures/Windows", StringComparison.OrdinalIgnoreCase)
        || path.Contains("/Doors/", StringComparison.OrdinalIgnoreCase)
        || path.Contains("Structures/Doors", StringComparison.OrdinalIgnoreCase)
        || path.Contains("Airlock", StringComparison.OrdinalIgnoreCase)
        || path.Contains("/Closets/", StringComparison.OrdinalIgnoreCase)
        || path.Contains("/Lockers/", StringComparison.OrdinalIgnoreCase)
        || path.Contains("Structures/Storage", StringComparison.OrdinalIgnoreCase)
        || path.Contains("/Ghosts/", StringComparison.OrdinalIgnoreCase)
        || path.Contains("ghost_human", StringComparison.OrdinalIgnoreCase)
        || path.Contains("/Mobs/", StringComparison.OrdinalIgnoreCase)
        || path.Contains("/Tiles/", StringComparison.OrdinalIgnoreCase)
        || path.Contains("Tiles/", StringComparison.OrdinalIgnoreCase);

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
            _texCache[path] = entry;
            _texLastUsed[path] = _frames;
            _texRetryAtFrame.Remove(path);
        }
        else
        {
            _queuedTex.Remove(path);
            _texRetryAtFrame[path] = _frames + 60;
        }
    }

    void LoadOneRsi(string contentRoot, string path, Port.Content.AczOnDemandFetcher? fetcher)
    {
        var src = Port.Content.RsiMeta.FindRsiSource(contentRoot, path);
        if (src is null)
        {
            _ = Port.Content.RsiMeta.TryGetPreviewFrameOrFetch(contentRoot, path, fetcher);
            _queuedTex.Remove(path);
            _texRetryAtFrame[path] = _frames + 30;
            return;
        }

        var atlas = Port.Content.RsiAtlas.TryLoad(src.Value.Path);
        var frame = Port.Content.RsiMeta.TryGetPreviewFrame(src.Value.Path);
        if (frame is null)
        {
            _queuedTex.Remove(path);
            _texRetryAtFrame[path] = _frames + 60;
            return;
        }

        // Use atlas UV whenever meta parsed. FolderMode only means the GPU texture is a
        // single-state sheet (folder RSI), not that we should ignore meta directions.
        var folderMode = !src.Value.IsRsic;
        var atlasKey = src.Value.Path;
        if (!TryMakeRoomForTexture())
        {
            _queuedTex.Remove(path);
            _texRetryAtFrame[path] = _frames + 90;
            return;
        }

        var rsiEntry = LoadTextureEntry(frame.Value, atlas, atlasKey, folderMode);
        if (rsiEntry.Id != 0)
        {
            _texCache[path] = rsiEntry;
            if (atlas is not null)
            {
                _atlasMeta[atlasKey] = atlas;
                _atlasMeta[path] = atlas;
            }
            _texLastUsed[path] = _frames;
            _texRetryAtFrame.Remove(path);
        }
        else
        {
            _queuedTex.Remove(path);
            _texRetryAtFrame[path] = _frames + 60;
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
        return new TexEntry(tex[0], fw, fh, 0, 0, u1, v1, null, false, w, h);
    }

    static TexEntry LoadTextureEntry(
        Port.Content.RsiMeta.FrameInfo frame,
        Port.Content.RsiAtlas.Loaded? atlas,
        string atlasKey,
        bool folderMode)
    {
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
        return new TexEntry(tex[0], fw, fh, 0f, 0f, u1, v1, atlasKey, folderMode, aw, ah);
    }

    static int LoadTexture(string pngPath)
    {
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

    void EnsureBuffers(int verts)
    {
        var posBytes = verts * 2 * sizeof(float);
        var colBytes = verts * 4 * sizeof(float);
        if (_posBuf is null || _posBuf.Capacity() < posBytes)
            _posBuf = ByteBuffer.AllocateDirect(Math.Max(posBytes, 4096)).Order(ByteOrder.NativeOrder())!.AsFloatBuffer();
        if (_colBuf is null || _colBuf.Capacity() < colBytes)
            _colBuf = ByteBuffer.AllocateDirect(Math.Max(colBytes, 8192)).Order(ByteOrder.NativeOrder())!.AsFloatBuffer();
    }

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
