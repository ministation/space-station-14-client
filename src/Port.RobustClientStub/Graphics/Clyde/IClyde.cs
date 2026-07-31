using Robust.Shared.Maths;

namespace Robust.Client.Graphics;

/// <summary>
/// Clyde interface shell. Android implements via <c>GlesClydeBackend</c>;
/// Content.Client resolves this type without SDL.
/// </summary>
public interface IClyde
{
    Vector2i ScreenSize { get; }
    void FrameProcess(float frameTime);
}

/// <summary>Viewport / main-window handle shell.</summary>
public interface IClydeWindow
{
    Vector2i Size { get; }
    bool IsFocused { get; }
}

public sealed class NullClyde : IClyde
{
    public Vector2i ScreenSize { get; set; } = new(1, 1);

    public void FrameProcess(float frameTime) => _ = frameTime;
}

public sealed class NullClydeWindow : IClydeWindow
{
    public Vector2i Size { get; set; } = new(1, 1);
    public bool IsFocused { get; set; } = true;
}
