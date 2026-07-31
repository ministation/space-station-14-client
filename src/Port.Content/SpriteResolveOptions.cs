namespace Port.Content;

/// <summary>
/// Global sprite resolution policy. Authoritative mode = YAML + RSI meta only
/// (no Walls/Windows path invent when meta is missing).
/// </summary>
public static class SpriteResolveOptions
{
    /// <summary>
    /// When true (default), <see cref="IconSmoothInfer"/> does not invent IconSmooth
    /// from RSI path heuristics — only numbered states from meta/atlas count.
    /// </summary>
    public static bool AuthoritativeOnly { get; set; } = true;

    /// <summary>
    /// When true, refuse blind RSI state remaps unless the YAML base is absent from meta.
    /// </summary>
    public static bool StrictRsiStates { get; set; } = true;
}
