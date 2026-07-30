namespace Robust.Client;

/// <summary>
/// Marker so the mobile port can resolve assembly name "Robust.Client"
/// when Content packs reference it. No Clyde / UI implementation.
/// </summary>
public static class MobileClientStub
{
    public const string Tag = "Port.RobustClientStub";
}
