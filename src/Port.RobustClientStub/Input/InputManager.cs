namespace Robust.Client.Input;

/// <summary>Input manager shell for Content.Client type resolution.</summary>
public sealed class InputManager
{
    public bool IsKeyDown(Keyboard.Key key) => false;

    public void FrameUpdate()
    {
    }
}

public static class Keyboard
{
    public enum Key : ushort
    {
        Unknown = 0,
        Escape = 1,
        Space = 2,
        W = 3,
        A = 4,
        S = 5,
        D = 6,
        MouseLeft = 100,
        MouseRight = 101,
    }
}
