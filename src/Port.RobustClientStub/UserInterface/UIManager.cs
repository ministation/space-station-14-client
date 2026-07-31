namespace Robust.Client.UserInterface;

/// <summary>
/// Minimal UIManager stub. Full Clyde-backed UI comes in later PRs;
/// this exists so Content.Client can resolve the type during assembly load.
/// </summary>
public partial class UIManager
{
    public Control? RootControl { get; set; }

    public void Initialize()
    {
    }

    public void FrameUpdate(float frameTime)
    {
        _ = frameTime;
    }

    public void DisposeAll()
    {
        RootControl?.Dispose();
        RootControl = null;
    }
}
