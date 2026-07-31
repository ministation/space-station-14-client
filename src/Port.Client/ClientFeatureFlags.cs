using Port.Content;
using Port.Net;

namespace Port.Client;

/// <summary>
/// Kill-switches while the port migrates from ghost-observe hacks to a full Robust-shaped client.
/// Defaults prefer correctness (YAML+meta) over heuristic fill-ins.
/// </summary>
public static class ClientFeatureFlags
{
    /// <summary>
    /// When true, IconSmooth / RSI state resolution uses prototype YAML + RSI meta only.
    /// Path heuristics (Walls/Windows invent) are disabled.
    /// </summary>
    public static bool AuthoritativeSprites
    {
        get => SpriteResolveOptions.AuthoritativeOnly;
        set => SpriteResolveOptions.AuthoritativeOnly = value;
    }

    /// <summary>
    /// When true, attempt to load Content.*.Client assemblies (requires expanded Robust.Client stubs).
    /// Off by default until UI/Clyde surface is large enough.
    /// </summary>
    public static bool LoadContentClientAssemblies
    {
        get => SerializerLoadPolicy.LoadContentClientAssemblies;
        set => SerializerLoadPolicy.LoadContentClientAssemblies = value;
    }

    /// <summary>
    /// When true, refuse RSI state remaps that would override a YAML base present in meta.
    /// </summary>
    public static bool StrictRsiStates
    {
        get => SpriteResolveOptions.StrictRsiStates;
        set => SpriteResolveOptions.StrictRsiStates = value;
    }
}
