using System.Numerics;

namespace Port.Client.Rendering;

/// <summary>
/// Clyde-shaped world view used by the Android client loop.
/// Platform backends (GLES) implement this; MainActivity stops talking to GLES types directly over time.
/// </summary>
public interface IClydeWorldView
{
    Vector2 Camera { get; set; }
    float CameraRotation { get; set; }
    float Zoom { get; set; }
    Vector2 ScreenSize { get; }

    void SetContentRoot(string? root);
    void SetFullbright(bool enabled);
    void SetGhostMode(bool enabled);
    void FrameProcess(float dt);
}

/// <summary>No-op view for tests / headless bootstrap.</summary>
public sealed class NullClydeWorldView : IClydeWorldView
{
    public Vector2 Camera { get; set; }
    public float CameraRotation { get; set; }
    public float Zoom { get; set; } = 1f;
    public Vector2 ScreenSize { get; set; } = new(1, 1);

    public void SetContentRoot(string? root) { }
    public void SetFullbright(bool enabled) { }
    public void SetGhostMode(bool enabled) { }
    public void FrameProcess(float dt) { }
}
