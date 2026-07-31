namespace Robust.Client;

/// <summary>
/// GameController shell so Content.Client can resolve the type.
/// Android drives ticks via Port.Client.ClientLoop until full Robust embed lands.
/// </summary>
public sealed partial class GameController
{
    public bool IsRunning { get; private set; }

    public void Startup() => IsRunning = true;

    public void Shutdown(string? reason = null)
    {
        _ = reason;
        IsRunning = false;
    }

    public void Tick(float frameTime) => _ = frameTime;
}
