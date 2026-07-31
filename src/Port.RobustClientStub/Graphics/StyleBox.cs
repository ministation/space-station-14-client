using System.Numerics;

namespace Robust.Client.Graphics;

public abstract class StyleBox
{
    public Vector2 Padding { get; set; }
}

public sealed class StyleBoxFlat : StyleBox
{
    public uint BackgroundColor { get; set; }
}

public sealed class StyleBoxTexture : StyleBox
{
    public object? Texture { get; set; }
    public uint Modulate { get; set; } = 0xFFFFFFFF;
}
