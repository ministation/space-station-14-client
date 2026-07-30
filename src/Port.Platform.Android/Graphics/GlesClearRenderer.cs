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
        public byte R, G, B;
        public bool IsControlled;
    }

    readonly object _gate = new();
    float _r = 0.04f, _g = 0.08f, _b = 0.16f;
    bool _pulse = true;
    bool _ghostMode;
    float _camX, _camY;
    long _frames;
    int _width;
    int _height;
    string _lastError = "";
    bool _ready;
    EntitySprite[] _entities = Array.Empty<EntitySprite>();
    int _entityCount;
    string? _contentFilesRoot;
    int _drawnLast;
    int _texturedLast;

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
    bool _glReady;

    readonly Dictionary<string, int> _texCache = new(StringComparer.OrdinalIgnoreCase);
    readonly Queue<string> _pendingTexLoad = new();
    readonly HashSet<string> _queuedTex = new(StringComparer.OrdinalIgnoreCase);

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

    public void SetContentFilesRoot(string? root)
    {
        lock (_gate) _contentFilesRoot = root;
    }

    public void SetEntities(EntitySprite[] entities, int count)
    {
        lock (_gate)
        {
            if (_entities.Length < count)
                _entities = new EntitySprite[Math.Max(count, 256)];
            Array.Copy(entities, _entities, count);
            _entityCount = count;
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
                ? $"gles ghost: {_width}x{_height} cam=({_camX:0},{_camY:0}) ents={_entityCount} draw={_drawnLast} tex={_texturedLast} frames={_frames}"
                : $"gles: OK {_width}x{_height} frames={_frames} pulse={_pulse}";
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
        float camX, camY;
        EntitySprite[] ents;
        int count;
        string? contentRoot;
        lock (_gate)
        {
            r = _r;
            g = _g;
            b = _b;
            pulse = _pulse;
            ghost = _ghostMode;
            camX = _camX;
            camY = _camY;
            count = _entityCount;
            ents = _entities;
            contentRoot = _contentFilesRoot;
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
            lock (_gate) { _drawnLast = 0; _texturedLast = 0; }
            return;
        }

        // Station-like grid so empty/loading viewport isn't a void.
        DrawWorldGrid(camX, camY);

        if (count <= 0)
        {
            lock (_gate) { _drawnLast = 0; _texturedLast = 0; }
            return;
        }

        PumpTextureLoads(contentRoot);

        var halfW = _width * 0.5f;
        var halfH = _height * 0.5f;
        var vert = 0;
        var textured = 0;
        var texDrawBudget = 500;

        for (var i = 0; i < count; i++)
        {
            ref readonly var e = ref ents[i];
            var wx = e.X * PixelsPerTile;
            var wy = e.Y * PixelsPerTile;
            var sx = wx - camX;
            var sy = wy - camY;

            if (MathF.Abs(sx) > halfW + 96 || MathF.Abs(sy) > halfH + 96)
                continue;

            if (!string.IsNullOrEmpty(e.RsiPath) && contentRoot is not null)
                QueueTexture(e.RsiPath!);

            var texId = 0;
            var hasTex = !string.IsNullOrEmpty(e.RsiPath)
                         && _texCache.TryGetValue(e.RsiPath!, out texId)
                         && texId != 0;

            if (hasTex && texDrawBudget > 0)
            {
                var size = e.IsControlled ? 32f : 28f;
                DrawTexturedQuad(sx, sy, size, texId,
                    e.R / 255f, e.G / 255f, e.B / 255f,
                    e.IsControlled ? 1f : 0.92f);
                textured++;
                texDrawBudget--;
                continue;
            }

            if (vert + 6 > MaxVerts)
                continue;

            var marker = e.IsControlled ? 18f : (string.IsNullOrEmpty(e.RsiPath) ? 10f : 14f);
            var cr = e.R / 255f;
            var cg = e.G / 255f;
            var cb = e.B / 255f;
            var ca = e.IsControlled ? 1f : 0.8f;
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
        }
    }

    void DrawWorldGrid(float camX, float camY)
    {
        if (_program == 0 || _width <= 0 || _height <= 0)
            return;

        var halfW = _width * 0.5f;
        var halfH = _height * 0.5f;
        var tile = PixelsPerTile;
        var startX = MathF.Floor((camX - halfW) / tile) * tile;
        var endX = camX + halfW + tile;
        var startY = MathF.Floor((camY - halfH) / tile) * tile;
        var endY = camY + halfH + tile;
        var vert = 0;
        const float thick = 1.1f;

        void AddQuad(float x0, float y0, float x1, float y1, float cr, float cg, float cb, float ca)
        {
            if (vert + 6 > MaxVerts) return;
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

        for (var x = startX; x <= endX; x += tile)
        {
            var sx = x - camX;
            var major = Math.Abs(x / tile) % 8 < 0.01f;
            var a = major ? 0.22f : 0.10f;
            AddQuad(sx - thick * 0.5f, -halfH, sx + thick * 0.5f, halfH, 0.25f, 0.45f, 0.35f, a);
        }

        for (var y = startY; y <= endY; y += tile)
        {
            var sy = y - camY;
            var major = Math.Abs(y / tile) % 8 < 0.01f;
            var a = major ? 0.22f : 0.10f;
            AddQuad(-halfW, sy - thick * 0.5f, halfW, sy + thick * 0.5f, 0.25f, 0.45f, 0.35f, a);
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

    void DrawTexturedQuad(float sx, float sy, float size, int texId, float r, float g, float b, float a)
    {
        if (_texProgram == 0 || _uvBuf is null)
            return;

        var x0 = sx - size * 0.5f;
        var y0 = sy - size * 0.5f;
        var x1 = sx + size * 0.5f;
        var y1 = sy + size * 0.5f;

        // pos
        _texPosScratch[0] = x0; _texPosScratch[1] = y0;
        _texPosScratch[2] = x1; _texPosScratch[3] = y0;
        _texPosScratch[4] = x1; _texPosScratch[5] = y1;
        _texPosScratch[6] = x0; _texPosScratch[7] = y0;
        _texPosScratch[8] = x1; _texPosScratch[9] = y1;
        _texPosScratch[10] = x0; _texPosScratch[11] = y1;
        // uv (flip V for GL)
        _uvScratch[0] = 0; _uvScratch[1] = 1;
        _uvScratch[2] = 1; _uvScratch[3] = 1;
        _uvScratch[4] = 1; _uvScratch[5] = 0;
        _uvScratch[6] = 0; _uvScratch[7] = 1;
        _uvScratch[8] = 1; _uvScratch[9] = 0;
        _uvScratch[10] = 0; _uvScratch[11] = 0;

        if (_posBuf is null || _posBuf.Capacity() < 12 * sizeof(float))
            _posBuf = ByteBuffer.AllocateDirect(12 * sizeof(float) * 4).Order(ByteOrder.NativeOrder())!.AsFloatBuffer();
        _posBuf.Position(0);
        _posBuf.Put(_texPosScratch, 0, 12);
        _posBuf.Position(0);
        _uvBuf.Position(0);
        _uvBuf.Put(_uvScratch, 0, 12);
        _uvBuf.Position(0);

        GLES20.GlUseProgram(_texProgram);
        GLES20.GlActiveTexture(GLES20.GlTexture0);
        GLES20.GlBindTexture(GLES20.GlTexture2d, texId);
        GLES20.GlUniform1i(_texUSampler, 0);
        GLES20.GlUniform2f(_texUScreen, _width, _height);
        GLES20.GlEnableVertexAttribArray(_texAPos);
        GLES20.GlEnableVertexAttribArray(_texAUv);
        GLES20.GlVertexAttribPointer(_texAPos, 2, GLES20.GlFloat, false, 0, _posBuf);
        GLES20.GlVertexAttribPointer(_texAUv, 2, GLES20.GlFloat, false, 0, _uvBuf);
        // modulate via vertex color unused — solid sample
        GLES20.GlDrawArrays(GLES20.GlTriangles, 0, 6);
        GLES20.GlDisableVertexAttribArray(_texAPos);
        GLES20.GlDisableVertexAttribArray(_texAUv);
        _ = (r, g, b, a);
    }

    void QueueTexture(string rsiPath)
    {
        if (_texCache.ContainsKey(rsiPath) || _queuedTex.Contains(rsiPath))
            return;
        if (_pendingTexLoad.Count > 64)
            return;
        _queuedTex.Add(rsiPath);
        _pendingTexLoad.Enqueue(rsiPath);
    }

    void PumpTextureLoads(string? contentRoot)
    {
        if (contentRoot is null || _pendingTexLoad.Count == 0)
            return;

        // Load a few per frame to avoid hitching the GL thread.
        for (var n = 0; n < 3 && _pendingTexLoad.Count > 0; n++)
        {
            var path = _pendingTexLoad.Dequeue();
            try
            {
                var dir = Port.Content.RsiMeta.FindRsiDirectory(contentRoot, path);
                if (dir is null)
                    continue;
                var frame = Port.Content.RsiMeta.TryGetPreviewFrame(dir);
                if (frame is null)
                    continue;
                var tex = LoadTexture(frame.Value.PngPath);
                if (tex != 0)
                    _texCache[path] = tex;
            }
            catch
            {
                /* skip bad RSI */
            }
        }
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
            void main() {
              vec4 c = texture2D(u_tex, v_uv);
              if (c.a < 0.05) discard;
              gl_FragColor = c;
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
