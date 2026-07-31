namespace Port.Net;

/// <summary>
/// Controls which content assemblies enter NetSerializer type maps.
/// Content.*.Client stays off until Robust.Client stubs cover Clyde/UI surface.
/// </summary>
public static class SerializerLoadPolicy
{
    /// <summary>
    /// When true, allow loading <c>*.Client.dll</c> packs into the serializer reflection set.
    /// Default false — client UI packs historically polluted DESER fail lists.
    /// </summary>
    public static bool LoadContentClientAssemblies { get; set; } = false;
}
