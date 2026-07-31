using Robust.Shared.Maths;

namespace Robust.Client.Graphics;

/// <summary>
/// Clyde interface shell. Real GLES rendering stays in Port.Platform.Android until
/// Content.Client systems are wired to this surface.
/// </summary>
public interface IClyde
{
    Vector2i ScreenSize { get; }
    void FrameProcess(float frameTime);
}

public sealed class NullClyde : IClyde
{
    public Vector2i ScreenSize { get; set; } = new(1, 1);

    public void FrameProcess(float frameTime) => _ = frameTime;
}
