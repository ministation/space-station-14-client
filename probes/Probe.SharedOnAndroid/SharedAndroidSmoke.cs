using Robust.Shared.Maths;

namespace Probe.SharedOnAndroid;

/// <summary>
/// Compile-time smoke check: can Robust.Shared be referenced from net10.0-android?
/// </summary>
public static class SharedAndroidSmoke
{
    public static string Ping() => $"Shared-on-Android compile OK; Angle.Zero={Angle.Zero}";
}
