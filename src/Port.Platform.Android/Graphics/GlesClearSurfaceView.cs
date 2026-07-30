using Android.Content;
using Android.Opengl;
using Android.Util;
using Android.Views;

namespace Port.Platform.Android.Graphics;

/// <summary>
/// GLSurfaceView host for Phase 4 clear-color probe.
/// Also forwards touch to the platform touch queue when attached.
/// </summary>
public sealed class GlesClearSurfaceView : GLSurfaceView
{
    public GlesClearRenderer Renderer { get; }

    public GlesClearSurfaceView(Context context) : base(context)
    {
        Holder?.SetFormat(global::Android.Graphics.Format.Opaque);
        SetEGLContextClientVersion(2);
        // Prefer keeping GL objects across pause; renderer still clears on OnSurfaceCreated.
        PreserveEGLContextOnPause = true;
        Renderer = new GlesClearRenderer();
        SetRenderer(Renderer);
        RenderMode = Rendermode.Continuously;
    }

    public GlesClearSurfaceView(Context context, IAttributeSet? attrs) : base(context, attrs)
    {
        Holder?.SetFormat(global::Android.Graphics.Format.Opaque);
        SetEGLContextClientVersion(2);
        PreserveEGLContextOnPause = true;
        Renderer = new GlesClearRenderer();
        SetRenderer(Renderer);
        RenderMode = Rendermode.Continuously;
    }
}
