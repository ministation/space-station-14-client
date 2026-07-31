namespace Robust.Client.UserInterface.Controls;

/// <summary>Text input stub matching Robust LineEdit for chat / forms.</summary>
public class LineEdit : Control
{
    public string Text { get; set; } = "";
    public string? PlaceHolder { get; set; }
    public bool Editable { get; set; } = true;
    public bool IsPlaceHolderVisible => string.IsNullOrEmpty(Text) && !string.IsNullOrEmpty(PlaceHolder);

    public event Action? OnTextEntered;
    public event Action<string>? OnTextChanged;

    public void SetText(string text, bool raiseEvent = true)
    {
        Text = text ?? "";
        if (raiseEvent)
            OnTextChanged?.Invoke(Text);
    }

    public void Submit()
    {
        if (!Disabled && Editable)
            OnTextEntered?.Invoke();
    }
}
