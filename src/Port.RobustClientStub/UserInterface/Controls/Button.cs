namespace Robust.Client.UserInterface.Controls;

/// <summary>Button stub for Content.Client UI type resolution.</summary>
public class Button : Container
{
    public string? Text { get; set; }
    public new bool Disabled
    {
        get => base.Disabled;
        set => base.Disabled = value;
    }

    public event Action? OnPressed;

    public void Press()
    {
        if (!Disabled)
            OnPressed?.Invoke();
    }
}
