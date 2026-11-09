using System;

namespace Content.Android;

/// <summary>
/// Extension point for Mini Station auth: set the SS14 auth env vars before the engine starts.
/// The engine reads them in ProgramShared/ClientContentLoader (ROBUST_AUTH_*).
/// </summary>
public static class AuthBridge
{
    private static string? _userId;
    private static string? _token;
    private static string? _server;
    private static string? _publicKey;

    public static void SetToken(string userId, string token, string server, string publicKey)
    {
        _userId = userId;
        _token = token;
        _server = server;
        _publicKey = publicKey;
    }

    public static void ApplyEnvironment()
    {
        Set("ROBUST_AUTH_USERID", _userId);
        Set("ROBUST_AUTH_TOKEN", _token);
        Set("ROBUST_AUTH_PUBKEY", _publicKey);
        Set("ROBUST_AUTH_SERVER", _server ?? "https://auth.spacestation14.com/");
    }

    private static void Set(string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
            Environment.SetEnvironmentVariable(name, value);
    }
}
