namespace Robust.Client.UserInterface.CustomControls;

/// <summary>BaseWindow shell for Content.Client menus (WiresMenu, etc.).</summary>
public partial class BaseWindow : Control
{
    public bool IsOpen { get; private set; }

    public event Action? OnClose;

    public virtual void Open() => IsOpen = true;

    public virtual void Close()
    {
        IsOpen = false;
        OnClose?.Invoke();
    }

    public override void Dispose()
    {
        Close();
        base.Dispose();
    }
}
