namespace Robust.Client.UserInterface.Controllers;

/// <summary>UIController shell for Content.Client UI controllers.</summary>
public abstract class UIController
{
    public virtual void Initialize()
    {
    }

    public virtual void FrameUpdate(float frameTime) => _ = frameTime;

    public virtual void Shutdown()
    {
    }
}
