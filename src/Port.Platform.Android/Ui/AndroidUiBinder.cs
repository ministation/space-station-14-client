using Android.Views;
using Android.Widget;
using Port.Client.Ui;

namespace Port.Platform.Android.Ui;

/// <summary>
/// One-way binder: Robust observe HUD → Android XML TextViews/EditTexts.
/// Lets us own HUD state in Robust controls while XML is still the draw surface.
/// </summary>
public sealed class AndroidUiBinder
{
    public TextView? StatusView { get; set; }
    public TextView? FpsView { get; set; }
    public TextView? ChatHistoryView { get; set; }
    public EditText? ChatInputView { get; set; }
    public global::Android.Widget.Button? ChatChannelView { get; set; }
    public TextView? DiagView { get; set; }

    public void BindViews(
        TextView? status,
        TextView? fps,
        TextView? chatHistory,
        EditText? chatInput,
        global::Android.Widget.Button? chatChannel,
        TextView? diag)
    {
        StatusView = status;
        FpsView = fps;
        ChatHistoryView = chatHistory;
        ChatInputView = chatInput;
        ChatChannelView = chatChannel;
        DiagView = diag;
    }

    public void PushFromHud(ObserveHudRoot hud)
    {
        ArgumentNullException.ThrowIfNull(hud);
        if (StatusView != null && StatusView.Text != hud.StatusLabel.Text)
            StatusView.Text = hud.StatusLabel.Text;
        if (FpsView != null && FpsView.Text != hud.FpsLabel.Text)
            FpsView.Text = hud.FpsLabel.Text;
        if (ChatHistoryView != null && ChatHistoryView.Text != hud.ChatHistory.Text)
            ChatHistoryView.Text = hud.ChatHistory.Text;
        if (ChatChannelView != null && ChatChannelView.Text != hud.ChatChannelButton.Text)
            ChatChannelView.Text = hud.ChatChannelButton.Text;
        if (DiagView != null)
        {
            DiagView.Visibility = hud.DiagLabel.Visible ? ViewStates.Visible : ViewStates.Gone;
            if (hud.DiagLabel.Visible && DiagView.Text != hud.DiagLabel.Text)
                DiagView.Text = hud.DiagLabel.Text;
        }
    }

    /// <summary>Pull Android EditText into Robust LineEdit before send.</summary>
    public void PullChatInput(ObserveHudRoot hud)
    {
        if (ChatInputView is null)
            return;
        hud.ChatInput.SetText(ChatInputView.Text ?? "", raiseEvent: false);
    }

    public void ClearChatInput(ObserveHudRoot hud)
    {
        hud.ChatInput.SetText("", raiseEvent: false);
        if (ChatInputView != null)
            ChatInputView.Text = "";
    }
}
