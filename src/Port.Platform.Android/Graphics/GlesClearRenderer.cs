using Android.Opengl;
using Javax.Microedition.Khronos.Opengles;
using EGLConfig = Javax.Microedition.Khronos.Egl.EGLConfig;
using GLSurfaceView = Android.Opengl.GLSurfaceView;

namespace Port.Platform.Android.Graphics;

/// <summary>
/// GLES2 clear + ghost-observe atmosphere (parallax space field via clear tint).
/// Bridge toward Clyde; not a full sprite viewport yet.
/// </summary>
public sealed class GlesClearRenderer : Java.Lang.Object, GLSurfaceView.IRenderer
{
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

    public string Format()
    {
        lock (_gate)
        {
            if (!string.IsNullOrEmpty(_lastError))
                return $"gles: ERROR {_lastError}";
            if (!_ready)
                return "gles: waiting for surface";
            return _ghostMode
                ? $"gles ghost: {_width}x{_height} cam=({_camX:0},{_camY:0}) frames={_frames}"
                : $"gles: OK {_width}x{_height} frames={_frames} pulse={_pulse}";
        }
    }

    public void OnSurfaceCreated(IGL10? gl, EGLConfig? config)
    {
        try
        {
            GLES20.GlClearColor(0.02f, 0.03f, 0.06f, 1f);
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
        lock (_gate)
        {
            r = _r;
            g = _g;
            b = _b;
            pulse = _pulse;
            ghost = _ghostMode;
            camX = _camX;
            camY = _camY;
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
                // Space parallax tint from camera — proves touch → eye offset.
                var nx = MathF.Sin(camX * 0.004f) * 0.5f + 0.5f;
                var ny = MathF.Cos(camY * 0.004f) * 0.5f + 0.5f;
                var breath = (MathF.Sin(_frames * 0.02f) + 1f) * 0.5f;
                r = 0.02f + 0.04f * nx + 0.02f * breath;
                g = 0.03f + 0.05f * ny;
                b = 0.07f + 0.10f * (1f - nx) + 0.03f * breath;
            }
        }

        GLES20.GlClearColor(r, g, b, 1f);
        GLES20.GlClear(GLES20.GlColorBufferBit);
    }
}
