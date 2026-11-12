using System.Numerics;

namespace LightmapBench;

// Minimal stand-ins for the engine types the lightmap core touches. Field layout and
// arithmetic match Robust.Shared.Maths / SixLabors so the float results are identical.

public struct Color
{
    public float R;
    public float G;
    public float B;
    public float A;

    public Color(float r, float g, float b, float a = 1f)
    {
        R = r;
        G = g;
        B = b;
        A = a;
    }
}

public struct Rgba32 : System.IEquatable<Rgba32>
{
    public byte R;
    public byte G;
    public byte B;
    public byte A;

    public Rgba32(byte r, byte g, byte b, byte a)
    {
        R = r;
        G = g;
        B = b;
        A = a;
    }

    public bool Equals(Rgba32 other) => R == other.R && G == other.G && B == other.B && A == other.A;
}

public readonly struct Box2
{
    public readonly float Left;
    public readonly float Bottom;
    public readonly float Right;
    public readonly float Top;

    public Box2(float left, float bottom, float right, float top)
    {
        Left = left;
        Bottom = bottom;
        Right = right;
        Top = top;
    }

    public float Width => System.MathF.Abs(Right - Left);
    public float Height => System.MathF.Abs(Top - Bottom);
    public Vector2 BottomLeft => new(Left, Bottom);

    public bool Intersects(in Box2 other) =>
        other.Bottom <= Top && other.Top >= Bottom && other.Right >= Left && other.Left <= Right;

    public Box2 Union(in Box2 other) => new(
        System.MathF.Min(Left, other.Left), System.MathF.Min(Bottom, other.Bottom),
        System.MathF.Max(Right, other.Right), System.MathF.Max(Top, other.Top));

    public Box2 Enlarged(float size) => new(Left - size, Bottom - size, Right + size, Top + size);
}

/// <summary>Stand-in for PointLightComponent: only the members the lightmap reads.</summary>
public sealed class PointLight
{
    public float Radius = 5f;
    public float Energy = 1f;
    public float Falloff = 6.8f;
    public float CurveFactor;
    public Color Color = new(1f, 1f, 1f);
    public bool CastShadows = true;
}

public struct LightRenderData
{
    public PointLight Light;
    public Vector2 Position;
}
