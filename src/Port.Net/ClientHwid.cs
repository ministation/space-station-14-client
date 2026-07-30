using System.Security.Cryptography;

namespace Port.Net;

/// <summary>
/// Persistent device HWID for Android/non-Windows, matching Robust.Client BasicHWId file path.
/// Modern format is [version=0] + 32 random bytes; legacy is empty off Windows.
/// </summary>
public static class ClientHwid
{
    public const int Length = 32;

    /// <summary>Directory for <c>.hwid</c> (usually Android userdata).</summary>
    public static string? StorageDirectory { get; set; }

    public static byte[] GetLegacy() => Array.Empty<byte>();

    public static byte[] GetModern()
    {
        var raw = LoadOrCreate();
        var modern = new byte[1 + raw.Length];
        modern[0] = 0;
        Buffer.BlockCopy(raw, 0, modern, 1, raw.Length);
        return modern;
    }

    public static string? GetModernBase64() => Convert.ToBase64String(GetModern());

    static byte[] LoadOrCreate()
    {
        var dir = StorageDirectory;
        if (string.IsNullOrWhiteSpace(dir))
            dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ss14-hub");

        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, ".hwid");

        try
        {
            if (File.Exists(path))
            {
                var existing = File.ReadAllBytes(path);
                if (existing.Length == Length)
                    return existing;
            }
        }
        catch
        {
            /* recreate below */
        }

        var created = RandomNumberGenerator.GetBytes(Length);
        try
        {
            File.WriteAllBytes(path, created);
        }
        catch
        {
            /* still use in-memory id for this session */
        }

        return created;
    }
}
