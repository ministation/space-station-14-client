using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Port.Net;

public sealed record Ss14AuthResult(
    bool Ok,
    string? Token = null,
    string? UserName = null,
    Guid? UserId = null,
    DateTimeOffset? ExpireTime = null,
    string? Error = null,
    string? ErrorCode = null);

/// <summary>
/// Official launcher auth API (same endpoints as SS14.Launcher AuthApi).
/// </summary>
public sealed class Ss14AuthClient
{
    public const string DefaultAuthServer = "https://auth.spacestation14.com/";

    readonly HttpClient _http;
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string AuthServer { get; set; } = DefaultAuthServer;

    public Ss14AuthClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task<Ss14AuthResult> AuthenticateAsync(
        string username,
        string password,
        string? tfaCode = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return new Ss14AuthResult(false, Error: "username/password required", ErrorCode: "missing");

        try
        {
            var url = AuthServer.TrimEnd('/') + "/api/auth/authenticate";
            var body = new AuthenticateRequest(
                Username: username.Trim(),
                UserId: null,
                Password: password,
                TfaCode: string.IsNullOrWhiteSpace(tfaCode) ? null : tfaCode.Trim());

            using var resp = await _http.PostAsJsonAsync(url, body, JsonOptions, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);

            if (resp.IsSuccessStatusCode)
            {
                var ok = JsonSerializer.Deserialize<AuthenticateResponse>(json, JsonOptions)
                         ?? throw new InvalidOperationException("empty auth response");
                return new Ss14AuthResult(
                    true,
                    Token: ok.Token,
                    UserName: ok.Username,
                    UserId: ok.UserId,
                    ExpireTime: ok.ExpireTime);
            }

            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                var deny = JsonSerializer.Deserialize<AuthenticateDenyResponse>(json, JsonOptions);
                var msg = deny?.Errors is { Length: > 0 }
                    ? string.Join("; ", deny.Errors)
                    : "invalid credentials";
                return new Ss14AuthResult(false, Error: msg, ErrorCode: deny?.Code ?? "Unauthorized");
            }

            return new Ss14AuthResult(
                false,
                Error: $"HTTP {(int)resp.StatusCode}: {Truncate(json, 180)}",
                ErrorCode: resp.StatusCode.ToString());
        }
        catch (Exception ex)
        {
            return new Ss14AuthResult(false, Error: $"{ex.GetType().Name}: {ex.Message}", ErrorCode: "exception");
        }
    }

    public async Task<Ss14AuthResult> RefreshAsync(string token, CancellationToken ct = default)
    {
        try
        {
            var url = AuthServer.TrimEnd('/') + "/api/auth/refresh";
            using var resp = await _http.PostAsJsonAsync(url, new RefreshRequest(token), JsonOptions, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                return new Ss14AuthResult(
                    false,
                    Error: $"refresh HTTP {(int)resp.StatusCode}: {Truncate(json, 160)}",
                    ErrorCode: resp.StatusCode.ToString());
            }

            var ok = JsonSerializer.Deserialize<RefreshResponse>(json, JsonOptions)
                     ?? throw new InvalidOperationException("empty refresh response");
            return new Ss14AuthResult(true, Token: ok.NewToken, ExpireTime: ok.ExpireTime);
        }
        catch (Exception ex)
        {
            return new Ss14AuthResult(false, Error: $"{ex.GetType().Name}: {ex.Message}", ErrorCode: "exception");
        }
    }

    public async Task<bool> PingAsync(string token, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, AuthServer.TrimEnd('/') + "/api/auth/ping");
            req.Headers.Authorization = new AuthenticationHeaderValue("SS14Auth", token);
            using var resp = await _http.SendAsync(req, ct);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public AuthSessionConfig ToSession(Ss14AuthResult result, string? authServer = null)
    {
        if (!result.Ok || result.Token is null || result.UserId is null)
            throw new InvalidOperationException("auth result is not successful");

        return new AuthSessionConfig
        {
            AuthServer = authServer ?? AuthServer,
            UserId = result.UserId.Value.ToString(),
            Token = result.Token,
            UserName = result.UserName ?? "AndroidPort",
            ExpireTime = result.ExpireTime?.ToString("O"),
            AllowHwid = false,
            PublicKey = "",
        };
    }

    static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");

    sealed record AuthenticateRequest(
        string? Username,
        Guid? UserId,
        string Password,
        string? TfaCode);

    sealed record AuthenticateResponse(
        string Token,
        string Username,
        Guid UserId,
        DateTimeOffset ExpireTime);

    sealed record AuthenticateDenyResponse(
        string[]? Errors,
        string? Code);

    sealed record RefreshRequest(string Token);

    sealed record RefreshResponse(DateTimeOffset ExpireTime, string NewToken);
}
