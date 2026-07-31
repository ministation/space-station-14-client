namespace Robust.Client;

/// <summary>GameController service shell for Content.Client EntryPoint deps.</summary>
public interface IGameController
{
    GameController.GameLaunchState LaunchState { get; }
    bool IsRunning { get; }
    void Shutdown(string? reason = null);
}

public sealed partial class GameController : IGameController
{
    public sealed class GameLaunchState
    {
        public bool FromLauncher { get; set; }
    }

    public GameLaunchState LaunchState { get; } = new();
    bool IGameController.IsRunning => IsRunning;
    void IGameController.Shutdown(string? reason) => Shutdown(reason);
}
