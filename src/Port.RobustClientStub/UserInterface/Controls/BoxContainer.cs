namespace Robust.Client.UserInterface.Controls;

/// <summary>Layout container stub matching Robust.Client BoxContainer API surface.</summary>
public partial class BoxContainer : Container
{
    public enum LayoutOrientation : byte
    {
        Horizontal,
        Vertical
    }

    public LayoutOrientation Orientation { get; set; } = LayoutOrientation.Vertical;
    public int SeparationOverride { get; set; }
}
