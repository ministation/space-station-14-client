using System.Numerics;

namespace Robust.Client.Graphics;

/// <summary>Eye manager shell — camera still owned by GameSessionClient / GLES for now.</summary>
public interface IEyeManager
{
    IEye? CurrentEye { get; set; }
}

public interface IEye
{
    Vector2 Position { get; set; }
    float Zoom { get; set; }
}

public sealed class Eye : IEye
{
    public Vector2 Position { get; set; }
    public float Zoom { get; set; } = 1f;
}

public sealed class EyeManager : IEyeManager
{
    public IEye? CurrentEye { get; set; } = new Eye();
}
