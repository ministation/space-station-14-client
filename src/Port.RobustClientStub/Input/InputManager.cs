namespace Robust.Client.Input;

/// <summary>
/// Input manager with injectable key/pointer state for Android bridging.
/// Content.Client resolves this type; full bind maps arrive later.
/// </summary>
public sealed class InputManager
{
    readonly HashSet<Keyboard.Key> _down = new();
    readonly Dictionary<Keyboard.Key, bool> _pressedThisFrame = new();

    public float PointerX { get; private set; }
    public float PointerY { get; private set; }
    public bool PointerDown { get; private set; }
    public int PointerButton { get; private set; }

    public event Action<Keyboard.Key, bool>? KeyChanged;
    public event Action<float, float, bool, int>? PointerChanged;

    public bool IsKeyDown(Keyboard.Key key) => _down.Contains(key);

    public bool WasKeyPressed(Keyboard.Key key) =>
        _pressedThisFrame.TryGetValue(key, out var v) && v;

    public void SetKey(Keyboard.Key key, bool down)
    {
        if (down)
        {
            if (_down.Add(key))
            {
                _pressedThisFrame[key] = true;
                KeyChanged?.Invoke(key, true);
            }
        }
        else if (_down.Remove(key))
        {
            KeyChanged?.Invoke(key, false);
        }
    }

    public void SetPointer(float x, float y, bool down, int button = 0)
    {
        PointerX = x;
        PointerY = y;
        PointerDown = down;
        PointerButton = button;
        PointerChanged?.Invoke(x, y, down, button);
    }

    public void FrameUpdate()
    {
        _pressedThisFrame.Clear();
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
        Enter = 7,
        Tab = 8,
        MouseLeft = 100,
        MouseRight = 101,
        MouseMiddle = 102,
    }
}
