namespace Port.Content;

/// <summary>Parsed IconSmooth component from entity prototypes (PC Content.Client.IconSmoothing).</summary>
public readonly record struct IconSmoothData(
    string Key,
    string StateBase,
    IconSmoothMode Mode);

public enum IconSmoothMode : byte
{
    Corners = 0,
    CardinalFlags = 1,
    Diagonal = 2,
    NoSprite = 3,
}
