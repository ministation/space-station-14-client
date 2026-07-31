using Port.Content;
using Port.Net;

namespace Port.Client;

/// <summary>
/// Kill-switches while the port migrates from ghost-observe hacks to a full Robust-shaped client.
/// </summary>
public static class ClientFeatureFlags
{
    /// <summary>
    /// When true, IconSmooth / RSI state resolution uses prototype YAML + RSI meta only.
    /// </summary>
    public static bool AuthoritativeSprites
    {
        get => SpriteResolveOptions.AuthoritativeOnly;
        set => SpriteResolveOptions.AuthoritativeOnly = value;
    }

    /// <summary>
    /// When true, <see cref="Content.ContentAssemblyHost"/> loads Content.*.Client packs
    /// for type discovery. Does NOT put them into NetSerializer (see ReflectContentClientInSerializer).
    /// </summary>
    public static bool LoadContentClientAssemblies { get; set; } = true;

    /// <summary>
    /// When true, invoke Content.Client EntryPoint.Init via IoC after type-load.
    /// Default false — Init pulls Clyde/UI and can hard-crash the Android process.
    /// </summary>
    public static bool RunContentEntryPointBootstrap { get; set; }

    /// <summary>
    /// When true, construct parameterless Content.*.Client EntitySystem types after type-load.
    /// Default false — ctors often touch desktop-only natives.
    /// </summary>
    public static bool RunContentSystemHost { get; set; }

    /// <summary>
    /// When true, Content.*.Client packs enter the NetSerializer reflection set.
    /// Default false — historically polluted DESER fail lists.
    /// </summary>
    public static bool ReflectContentClientInSerializer
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
