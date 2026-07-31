namespace Robust.Client.UserInterface.Controls;

/// <summary>Multiline / markup label stub for chat history.</summary>
public class RichTextLabel : Label
{
    public string? MarkupText
    {
        get => Text;
        set => Text = value;
    }
}
