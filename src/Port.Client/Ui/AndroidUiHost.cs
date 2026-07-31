using Port.Client.Bootstrap;
using Robust.Client.UserInterface;

namespace Port.Client.Ui;

/// <summary>
/// Hosts Robust UIManager on Android until Clyde is ported.
/// Probe.AndroidHost XML HUD will gradually be replaced by this tree.
/// </summary>
public sealed class AndroidUiHost : IClientSystem
{
    public UIManager Ui { get; } = new();

    public void Initialize() => Ui.Initialize();

    public void FrameUpdate(float dt) => Ui.FrameUpdate(dt);

    public void Shutdown() => Ui.DisposeAll();
}
