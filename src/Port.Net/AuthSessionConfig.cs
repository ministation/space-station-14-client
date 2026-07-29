using System.Text.Json;

namespace Port.Net;

public sealed class AuthSessionConfig
{
    public string AuthServer { get; set; } = Ss14AuthClient.DefaultAuthServer;
    public string UserId { get; set; } = "";
    public string Token { get; set; } = "";
    public string PublicKey { get; set; } = "";
    public string UserName { get; set; } = "AndroidPort";
    public string? ExpireTime { get; set; }
    public bool AllowHwid { get; set; }

    public bool HasRequiredFields =>
        Guid.TryParse(UserId, out var guid) &&
        guid != Guid.Empty &&
        !string.IsNullOrWhiteSpace(Token) &&
        !Token.Contains("paste-launcher-token-here", StringComparison.OrdinalIgnoreCase);

    public string StatusLine()
    {
        if (!HasRequiredFields)
            return "auth: not logged in — enter SS14 username/password";
        var exp = "";
        if (DateTimeOffset.TryParse(ExpireTime, out var when))
            exp = $"  expires {when:yyyy-MM-dd HH:mm} UTC";
        return $"auth: logged in as {UserName} ({UserId}){exp}";
    }

    public static AuthSessionConfig FromEnvironment()
    {
        return new AuthSessionConfig
        {
            AuthServer = Environment.GetEnvironmentVariable("ROBUST_AUTH_SERVER")?.Trim() ?? Ss14AuthClient.DefaultAuthServer,
            UserId = Environment.GetEnvironmentVariable("ROBUST_AUTH_USERID")?.Trim() ?? "",
            Token = Environment.GetEnvironmentVariable("ROBUST_AUTH_TOKEN")?.Trim() ?? "",
            PublicKey = Environment.GetEnvironmentVariable("ROBUST_AUTH_PUBKEY")?.Trim() ?? "",
            AllowHwid = (Environment.GetEnvironmentVariable("ROBUST_AUTH_ALLOW_HWID")?.Trim() ?? "") == "1",
            UserName = Environment.GetEnvironmentVariable("ROBUST_AUTH_USERNAME")?.Trim() ?? "AndroidPort",
        };
    }

    public static AuthSessionConfig? TryLoad(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            var json = File.ReadAllText(path);
            var cfg = JsonSerializer.Deserialize<AuthSessionConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
            return cfg;
        }

        var env = FromEnvironment();
        return env.HasRequiredFields || !string.IsNullOrWhiteSpace(env.PublicKey) ? env : null;
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true,
        }));
    }

    public static void Clear(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    public static string ExampleJson() =>
        JsonSerializer.Serialize(new AuthSessionConfig
        {
            AuthServer = Ss14AuthClient.DefaultAuthServer,
            UserId = "00000000-0000-0000-0000-000000000000",
            Token = "paste-launcher-token-here",
            PublicKey = "",
            UserName = "AndroidPort",
            AllowHwid = false,
        }, new JsonSerializerOptions { WriteIndented = true });
}
