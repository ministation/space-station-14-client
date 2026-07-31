using Android.Views;
using Robust.Client.Input;

namespace Port.Platform.Android.Input;

/// <summary>
/// Maps Android MotionEvent / KeyEvent into Robust <see cref="InputManager"/>.
/// Camera / flight logic stays in MainActivity; this is the shared input source of truth.
/// </summary>
public sealed class AndroidInputBridge
{
    readonly InputManager _input;

    public AndroidInputBridge(InputManager input)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
    }

    public InputManager Input => _input;

    public bool HandleMotion(MotionEvent? ev)
    {
        if (ev is null)
            return false;

        var x = ev.GetX();
        var y = ev.GetY();
        switch (ev.ActionMasked)
        {
            case MotionEventActions.Down:
                _input.SetKey(Keyboard.Key.MouseLeft, true);
                _input.SetPointer(x, y, down: true, button: 0);
                return true;
            case MotionEventActions.Move:
                _input.SetPointer(x, y, down: _input.PointerDown, button: 0);
                return true;
            case MotionEventActions.Up:
            case MotionEventActions.Cancel:
                _input.SetKey(Keyboard.Key.MouseLeft, false);
                _input.SetPointer(x, y, down: false, button: 0);
                return true;
            default:
                return false;
        }
    }

    public bool HandleKey(Keycode keyCode, bool down)
    {
        var key = MapKey(keyCode);
        if (key == Keyboard.Key.Unknown)
            return false;
        _input.SetKey(key, down);
        return true;
    }

    public static Keyboard.Key MapKey(Keycode keyCode) => keyCode switch
    {
        Keycode.Escape or Keycode.Back => Keyboard.Key.Escape,
        Keycode.Space => Keyboard.Key.Space,
        Keycode.W => Keyboard.Key.W,
        Keycode.A => Keyboard.Key.A,
        Keycode.S => Keyboard.Key.S,
        Keycode.D => Keyboard.Key.D,
        Keycode.Enter or Keycode.NumpadEnter => Keyboard.Key.Enter,
        Keycode.Tab => Keyboard.Key.Tab,
        _ => Keyboard.Key.Unknown,
    };
}
