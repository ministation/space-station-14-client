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
    }

    public struct TileSprite
    {
        public float X;
        public float Y;
        public byte R, G, B;
        public string? RsiPath;
        public string? StateName;
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
    float _camX, _camY;
    float _camRot;
    float _zoom = 1f;
    long _frames;
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
        bool FolderMode);

    readonly Dictionary<string, TexEntry> _texCache = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Port.Content.RsiAtlas.Loaded> _atlasMeta = new(StringComparer.OrdinalIgnoreCase);
    readonly Queue<string> _pendingTexLoad = new();
    readonly HashSet<string> _queuedTex = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, long> _texRetryAtFrame = new(StringComparer.OrdinalIgnoreCase);

    FloatBuffer? _posBuf;
    FloatBuffer? _colBuf;
    FloatBuffer? _uvBuf;
    float[] _posScratch = new float[MaxVerts * 2];
    float[] _colScratch = new float[MaxVerts * 4];
    float[] _uvScratch = new float[6 * 2];
    float[] _texPosScratch = new float[6 * 2];

    const int MaxEntities = 3500;
    const int MaxVerts = MaxEntities * 6; // 2 tris per quad
    const float PixelsPerTile = 32f;

    public long FrameCount
    {
        get { lock (_gate) return _frames; }
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
                r = 0.02f;
                g = 0.025f;
                b = 0.04f;
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
        DrawTiles(tiles, tileCount, camX, camY, zoom, cosR, sinR, contentRoot, texFetcher);

        if (count > 0)
            DrawEntities(ents, count, camX, camY, zoom, cosR, sinR, camRot, contentRoot, texFetcher);

        DrawSpeechBubbles(bubbles, bubbleCount, camX, camY, zoom, cosR, sinR);
    }

    void DrawEntities(
        EntitySprite[] ents, int count,
        float camX, float camY, float zoom, float cosR, float sinR, float camRot,
        string? contentRoot, Port.Content.AczOnDemandFetcher? texFetcher)
    {
        PumpTextureLoads(contentRoot, texFetcher);

        var halfW = _width * 0.5f;
        var halfH = _height * 0.5f;
        var vert = 0;
        var textured = 0;
        var rsiPaths = 0;
        var texDrawBudget = 2800;
        var viewPad = 200f / zoom;
        var animTime = _frames / 60.0;

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
                rsiPaths++;
                if (contentRoot is not null)
                    QueueTexture(e.RsiPath!);
            }

            TexEntry tex = default;
            var hasTex = !string.IsNullOrEmpty(e.RsiPath)
                         && _texCache.TryGetValue(e.RsiPath!, out tex)
                         && tex.Id != 0;

            // PC SpriteSystem: direction from on-screen angle (worldRotation + eyeRotation).
            var eyeRelRot = e.NoRotation ? 0f : (e.Rotation - camRot);
            var drawRot = e.NoRotation ? 0f : (e.Rotation - camRot);
            if (hasTex && texDrawBudget > 0)
            {
                var uv = ResolveUv(tex, e.StateName, eyeRelRot, animTime);
                var sizeX = Math.Max(8f, uv.FrameW) * zoom;
                var sizeY = Math.Max(8f, uv.FrameH) * zoom;
                if (e.IsControlled)
                {
                    sizeX *= 1.25f;
                    sizeY *= 1.25f;
                }

                DrawTexturedQuad(sx, sy, sizeX, sizeY, tex.Id, uv, drawRot,
                    e.R / 255f, e.G / 255f, e.B / 255f, e.IsControlled ? 0.92f : 1f);
                textured++;
                texDrawBudget--;
                continue;
            }

            // Fallback markers while textures load — otherwise viewport stays black.
            if (vert + 6 > MaxVerts)
                continue;

            var marker = (e.IsControlled ? 22f : (string.IsNullOrEmpty(e.RsiPath) ? 10f : 14f)) * zoom;
            float cr, cg, cb, ca;
            if (e.IsControlled) { cr = 0.55f; cg = 0.95f; cb = 1f; ca = 1f; }
            else if (!string.IsNullOrEmpty(e.RsiPath)) { cr = e.R / 255f; cg = e.G / 255f; cb = e.B / 255f; ca = 0.92f; }
            else { cr = 0.35f; cg = 0.45f; cb = 0.6f; ca = 0.75f; }

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

    Port.Content.RsiAtlas.UvRect ResolveUv(TexEntry tex, string? state, float rotation, double time)
    {
        if (tex.AtlasKey is not null && _atlasMeta.TryGetValue(tex.AtlasKey, out var atlas))
            return Port.Content.RsiAtlas.Sample(atlas, state, rotation, time, tex.FolderMode);
        return new Port.Content.RsiAtlas.UvRect(tex.U0, tex.V0, tex.U1, tex.V1, tex.FrameW, tex.FrameH);
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

        PumpTextureLoads(contentRoot, fetcher);

        var halfW = _width * 0.5f;
        var halfH = _height * 0.5f;
        var size = PixelsPerTile * zoom;
        var pad = size * 1.5f;
        var vert = 0;
        var drawn = 0;
        var animTime = _frames / 60.0;
        var texBudget = 2200;
        // Tile quads rotate with the camera so floors stay aligned to the grid.
        var tileRot = MathF.Atan2(sinR, cosR);

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

            if (!string.IsNullOrEmpty(t.RsiPath)
                && _texCache.TryGetValue(t.RsiPath!, out var tex)
                && tex.Id != 0
                && texBudget > 0)
            {
                var uv = ResolveUv(tex, t.StateName, 0, animTime);
                DrawTexturedQuad(sx, sy, size, size, tex.Id, uv, tileRot,
                    t.R / 255f, t.G / 255f, t.B / 255f, 1f);
                drawn++;
                texBudget--;
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
            GLES20.GlUniform4f(_texUTint, r, g, b, a);
        GLES20.GlEnableVertexAttribArray(_texAPos);
        GLES20.GlEnableVertexAttribArray(_texAUv);
        GLES20.GlVertexAttribPointer(_texAPos, 2, GLES20.GlFloat, false, 0, _posBuf);
        GLES20.GlVertexAttribPointer(_texAUv, 2, GLES20.GlFloat, false, 0, _uvBuf);
        GLES20.GlDrawArrays(GLES20.GlTriangles, 0, 6);
        GLES20.GlDisableVertexAttribArray(_texAPos);
        GLES20.GlDisableVertexAttribArray(_texAUv);
    }

    void QueueTexture(string rsiPath)
    {
        if (_texCache.ContainsKey(rsiPath) || _queuedTex.Contains(rsiPath))
            return;
        if (_texRetryAtFrame.TryGetValue(rsiPath, out var retryAt) && _frames < retryAt)
            return;
        if (_pendingTexLoad.Count > 220)
            return;
        _queuedTex.Add(rsiPath);
        _pendingTexLoad.Enqueue(rsiPath);
    }

    void PumpTextureLoads(string? contentRoot, Port.Content.AczOnDemandFetcher? fetcher)
    {
        if (contentRoot is null || _pendingTexLoad.Count == 0)
            return;

        for (var n = 0; n < 14 && _pendingTexLoad.Count > 0; n++)
        {
            var path = _pendingTexLoad.Dequeue();
            try
            {
                // Floor tiles are plain PNGs (Textures/Tiles/*.png), not RSI/rsic.
                if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                {
                    var pngFull = ResolvePngPath(contentRoot, path);
                    if (pngFull is null)
                    {
                        // Ask ACZ for Textures/<path> if indexed.
                        fetcher?.EnsureFile(
                            path.StartsWith("Textures/", StringComparison.OrdinalIgnoreCase)
                                ? path
                                : "Textures/" + path.TrimStart('/'));
                        _queuedTex.Remove(path);
                        _texRetryAtFrame[path] = _frames + 30;
                        continue;
                    }

                    var entry = LoadPngTextureEntry(pngFull);
                    if (entry.Id != 0)
                    {
                        _texCache[path] = entry;
                        _texRetryAtFrame.Remove(path);
                    }
                    else
                    {
                        _queuedTex.Remove(path);
                        _texRetryAtFrame[path] = _frames + 90;
                    }

                    continue;
                }

                var src = Port.Content.RsiMeta.FindRsiSource(contentRoot, path);
                if (src is null)
                {
                    _ = Port.Content.RsiMeta.TryGetPreviewFrameOrFetch(contentRoot, path, fetcher);
                    _queuedTex.Remove(path);
                    _texRetryAtFrame[path] = _frames + 30;
                    continue;
                }

                var atlas = Port.Content.RsiAtlas.TryLoad(src.Value.Path);
                var frame = Port.Content.RsiMeta.TryGetPreviewFrame(src.Value.Path);
                if (frame is null)
                {
                    _queuedTex.Remove(path);
                    _texRetryAtFrame[path] = _frames + 60;
                    continue;
                }

                var rsiEntry = LoadTextureEntry(frame.Value, atlas, src.Value.Path, folderMode: !src.Value.IsRsic);
                if (rsiEntry.Id != 0)
                {
                    _texCache[path] = rsiEntry;
                    if (atlas is not null)
                        _atlasMeta[src.Value.Path] = atlas;
                    _texRetryAtFrame.Remove(path);
                }
                else
                {
                    _queuedTex.Remove(path);
                    _texRetryAtFrame[path] = _frames + 90;
                }
            }
            catch
            {
                _queuedTex.Remove(path);
                _texRetryAtFrame[path] = _frames + 90;
            }
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
        return new TexEntry(tex[0], w, h, 0, 0, 1, 1, null, false);
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
        var u1 = Math.Min(1f, fw / (float)Math.Max(1, bmp.Width));
        var v1 = Math.Min(1f, fh / (float)Math.Max(1, bmp.Height));
        bmp.Recycle();
        return new TexEntry(tex[0], fw, fh, 0f, 0f, u1, v1, atlasKey, folderMode);
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
            uniform vec2 u_screen;
            varying vec2 v_uv;
            void main() {
              vec2 ndc = vec2(a_pos.x / (u_screen.x * 0.5), a_pos.y / (u_screen.y * 0.5));
              gl_Position = vec4(ndc, 0.0, 1.0);
              v_uv = a_uv;
            }
            """;
        const string tfs = """
            precision mediump float;
            varying vec2 v_uv;
            uniform sampler2D u_tex;
            uniform vec4 u_tint;
            void main() {
              vec4 c = texture2D(u_tex, v_uv);
              if (c.a < 0.05) discard;
              gl_FragColor = c * u_tint;
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
