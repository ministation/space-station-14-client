namespace Robust.Client.UserInterface.Controls;

/// <summary>Absolute / anchored layout host stub.</summary>
public class LayoutContainer : Container
{
    public enum LayoutPreset : byte
    {
        TopLeft,
        TopWide,
        CenterTop,
        TopRight,
        LeftWide,
        Center,
        RightWide,
        BottomLeft,
        BottomWide,
        CenterBottom,
        BottomRight,
        Wide,
    }

    public static void SetAnchorPreset(Control control, LayoutPreset preset)
    {
        _ = (control, preset);
    }
}
