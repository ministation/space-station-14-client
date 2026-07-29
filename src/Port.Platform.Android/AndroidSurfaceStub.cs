namespace Port.Platform.Android;

/// <summary>
/// Tracks surface availability for status UI. Prefer GlesClearRenderer.Format() for GL details.
/// </summary>
public sealed class AndroidSurfaceStub
{
    public bool HasSurface { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public string Backend { get; private set; } = "none";

    public void OnSurfaceAvailable(int width, int height, string backend = "gles")
    {
        HasSurface = true;
        Width = width;
        Height = height;
        Backend = backend;
    }

    public void OnSurfaceDestroyed()
    {
        HasSurface = false;
        Width = 0;
        Height = 0;
        Backend = "none";
    }

    public string Format() => HasSurface
        ? $"surface: {Backend} {Width}x{Height}"
        : "surface: (no surface)";
}
