using Port.Client.Bootstrap;
using Robust.Client.Input;
using Robust.Client.UserInterface;

namespace Port.Client.Ui;

/// <summary>
/// Hosts Robust UIManager + observe HUD + InputManager on Android.
/// XML HUD binds to <see cref="ObserveHud"/>; input injects into <see cref="Input"/>.
/// </summary>
public sealed class AndroidUiHost : IClientSystem
{
    public UIManager Ui { get; } = new();
    public InputManager Input { get; } = new();
    public ObserveHudRoot ObserveHud { get; } = new();

    public void Initialize()
    {
        Ui.Initialize();
        Ui.RootControl = ObserveHud;
    }

    public void FrameUpdate(float dt)
    {
        Input.FrameUpdate();
        Ui.FrameUpdate(dt);
    }

    public void Shutdown()
    {
        Ui.DisposeAll();
    }
}
