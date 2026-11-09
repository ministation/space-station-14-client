using System;
using System.Text.Json.Serialization;

namespace Content.Android.Launcher.Models;

public sealed class AccountInfo
{
    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    [JsonPropertyName("expires")]
    public DateTimeOffset? Expires { get; set; }

    [JsonPropertyName("created")]
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record AuthAuthenticateRequest(
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("password")] string Password,
    [property: JsonPropertyName("tfaCode")] string? TfaCode = null
);

public sealed record AuthAuthenticateResponse(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("userId")] Guid UserId,
    [property: JsonPropertyName("expireTime")] DateTimeOffset ExpireTime
);

public sealed record AuthErrorResponse(
    [property: JsonPropertyName("errors")] string[]? Errors,
    [property: JsonPropertyName("code")] string? Code
);

public readonly struct AuthResult
{
    public bool Success { get; }
    public bool TfaRequired { get; }
    public AccountInfo? Account { get; }
    public string? ErrorMessage { get; }

    private AuthResult(bool success, bool tfaRequired, AccountInfo? account, string? errorMessage)
    {
        Success = success;
        TfaRequired = tfaRequired;
        Account = account;
        ErrorMessage = errorMessage;
    }

    public static AuthResult Ok(AccountInfo account) => new(true, false, account, null);
    public static AuthResult NeedsTfa() => new(false, true, null, "Two factor authentication required.");
    public static AuthResult Fail(string error) => new(false, false, null, error);
}
