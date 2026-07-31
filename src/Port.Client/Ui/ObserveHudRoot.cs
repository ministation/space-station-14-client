using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;

namespace Port.Client.Ui;

/// <summary>
/// PC-shaped observe HUD as a Robust control tree.
/// Android XML views bind to these labels until Clyde draws UI natively.
/// </summary>
public sealed class ObserveHudRoot : BoxContainer
{
    public Label StatusLabel { get; } = new() { Name = "ObserveStatus" };
    public Label FpsLabel { get; } = new() { Name = "ObserveFps" };
    public RichTextLabel ChatHistory { get; } = new() { Name = "ChatHistory" };
    public LineEdit ChatInput { get; } = new() { Name = "ChatInput", PlaceHolder = "Сообщение…" };
    public Button ChatChannelButton { get; } = new() { Name = "ChatChannel", Text = "Рядом" };
    public Button ChatSendButton { get; } = new() { Name = "ChatSend", Text = "➤" };
    public Label DiagLabel { get; } = new() { Name = "ObserveDiag", Visible = false };

    public int ChatChannelIndex { get; set; }
    public bool ChatExpanded { get; set; } = true;

    public ObserveHudRoot()
    {
        Name = "ObserveHudRoot";
        Orientation = LayoutOrientation.Vertical;

        var topBar = new BoxContainer
        {
            Name = "TopBar",
            Orientation = LayoutOrientation.Horizontal,
        };
        topBar.AddChild(StatusLabel);
        topBar.AddChild(FpsLabel);
        AddChild(topBar);

        var chat = new BoxContainer
        {
            Name = "ChatPanel",
            Orientation = LayoutOrientation.Vertical,
        };
        chat.AddChild(ChatHistory);
        var chatBar = new BoxContainer
        {
            Name = "ChatBar",
            Orientation = LayoutOrientation.Horizontal,
        };
        chatBar.AddChild(ChatChannelButton);
        chatBar.AddChild(ChatInput);
        chatBar.AddChild(ChatSendButton);
        chat.AddChild(chatBar);
        AddChild(chat);
        AddChild(DiagLabel);
    }

    public void SetStatus(string text) => StatusLabel.Text = text;
    public void SetFps(string text) => FpsLabel.Text = text;
    public void SetChatHistory(string text) => ChatHistory.Text = text;
    public void SetDiag(string text, bool visible)
    {
        DiagLabel.Text = text;
        DiagLabel.Visible = visible;
    }
}
