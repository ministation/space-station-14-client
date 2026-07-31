namespace Robust.Client.UserInterface
{
    public partial class Control
    {
        public enum CursorShape : byte { Arrow = 0, Hand, IBeam, VResize, HResize, Crosshair }
        public enum HAlignment : byte { Left = 0, Center, Right, Stretch }
        public enum VAlignment : byte { Top = 0, Center, Bottom, Stretch }
        public enum MouseFilterMode : byte { Ignore = 0, Pass, Stop }
        public sealed class OrderedChildCollection
        {
        }
    }

    public partial class FileDialogFilters
    {
        public sealed class Group
        {
            public string? Name { get; set; }
        }
    }
}

namespace Robust.Client.UserInterface.CustomControls
{
    public partial class BaseWindow
    {
        [Flags]
        public enum DragMode : byte
        {
            None = 0,
            Move = 1,
            Top = 2,
            Bottom = 4,
            Left = 8,
            Right = 16,
        }
    }
}

namespace Robust.Client.UserInterface.Controls
{
    public partial class BaseButton
    {
        public enum ActionMode : byte { Press = 0, Release }
        public enum DrawModeEnum : byte { Normal = 0, Pressed, Hover, Disabled }
        public class ButtonEventArgs : EventArgs { }
        public class ButtonToggledEventArgs : ButtonEventArgs { public bool Pressed { get; set; } }
    }

    public partial class BoxContainer
    {
        public enum AlignMode : byte { Begin = 0, Center, End }
    }

    public partial class Label
    {
        public enum AlignMode : byte { Left = 0, Center, Right }
        public enum VAlignMode : byte { Top = 0, Center, Bottom }
    }

    public partial class LayoutContainer
    {
        public enum GrowDirection : byte { Begin = 0, End, Both }
        public enum LayoutPresetMode : byte { KeepSize = 0, KeepWidth, KeepHeight, KeepRatio }
    }

    public partial class LineEdit
    {
        public class LineEditEventArgs : EventArgs { }
    }

    public partial class OptionButton
    {
        public class ItemSelectedEventArgs : EventArgs { public int SelectedId { get; set; } }
    }

    public partial class SplitContainer
    {
        public enum SplitOrientation : byte { Horizontal = 0, Vertical }
        public enum SplitResizeMode : byte { Stretch = 0, Fixed }
        public enum SplitState : byte { Auto = 0, Manual }
        public enum SplitStretchDirection : byte { Both = 0, Begin, End }
    }

    public partial class TextureRect
    {
        public enum StretchMode : byte { Scale = 0, Keep, KeepCentered, KeepAspect, KeepAspectCentered, KeepAspectCovered, Tile }
    }

    public partial class SpriteView
    {
        public enum StretchMode : byte { Scale = 0, Keep, KeepCentered, KeepAspect }
    }

    public partial class ItemList
    {
        public enum ItemListSelectMode : byte { None = 0, Single, Multiple }
        public class Item { public string? Text { get; set; } }
        public class ItemListEventArgs : EventArgs { }
        public class ItemListSelectedEventArgs : ItemListEventArgs { }
        public class ItemListDeselectedEventArgs : ItemListEventArgs { }
    }

    public partial class TextEdit
    {
        public enum LineBreakBias : byte { None = 0 }
        public struct CursorPos { public int Line; public int Column; }
        public class TextEditEventArgs : EventArgs { }
    }

    public partial class FloatSpinBox
    {
        public class FloatSpinBoxEventArgs : EventArgs { }
    }

    public partial class MultiselectOptionButton<T>
    {
        public class ItemPressedEventArgs : EventArgs { }
    }
}

namespace Robust.Client.Graphics
{
    public sealed partial class StyleBoxTexture
    {
        public enum StretchMode : byte { Stretch = 0, Tile, Keep, KeepCentered, KeepAspect, KeepAspectCentered, KeepAspectCovered }
    }
}

namespace Robust.Client.Replays.Playback
{
    public partial interface IReplayPlaybackManager
    {
        public delegate void HandleReplayMessageDelegate(object message, bool skipEffects);
    }
}
