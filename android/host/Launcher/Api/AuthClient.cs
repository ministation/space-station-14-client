using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Content.Android.Launcher.Models;

namespace Content.Android.Launcher.Api;

public sealed class AuthClient
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly string[] AuthBases =
    {
        "https://auth.spacestation14.com/",
        "https://auth.fallback.spacestation14.com/"
    };

    private static void Log(string message)
    {
        try
        {
            global::Android.Util.Log.Info("SS14Auth", message);
        }
        catch
        {
        }
    }

    public async Task<AuthResult> AuthenticateAsync(string username, string password, string? tfaCode = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return AuthResult.Fail("Enter username and password.");

        var code = string.IsNullOrWhiteSpace(tfaCode) ? null : tfaCode.Trim();
        var payload = JsonSerializer.Serialize(new AuthAuthenticateRequest(username.Trim(), password, code), JsonOptions);
        string? lastError = null;

        foreach (var baseUrl in AuthBases)
        {
            var endpoint = baseUrl + "api/auth/authenticate";
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(12));

                using var resp = await Http.SendAsync(request, cts.Token);
                var body = await resp.Content.ReadAsStringAsync(cts.Token);
                Log($"authenticate {endpoint} tfa={(code != null ? "yes" : "no")} status={(int)resp.StatusCode} body={Truncate(body, 400)}");

                if (resp.IsSuccessStatusCode)
                {
                    try
                    {
                        var data = JsonSerializer.Deserialize<AuthAuthenticateResponse>(body);
                        if (data != null && !string.IsNullOrEmpty(data.Token))
                        {
                            var account = new AccountInfo
                            {
                                UserId = data.UserId.ToString(),
                                Username = data.Username,
                                Token = data.Token,
                                Expires = data.ExpireTime
                            };
                            return AuthResult.Ok(account);
                        }

                        lastError = "Authentication server returned an empty token.";
                    }
                    catch (JsonException ex)
                    {
                        lastError = "Authentication server sent an invalid response.";
                        Log("invalid json: " + ex.Message);
                    }

                    continue;
                }

                if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest
                    or HttpStatusCode.UnprocessableEntity)
                {
                    try
                    {
                        var err = JsonSerializer.Deserialize<AuthErrorResponse>(body);
                        if (err?.Code == "TfaRequired")
                            return AuthResult.NeedsTfa();

                        if (err?.Code == "TfaInvalid")
                            return AuthResult.Fail("Invalid two factor code.");

                        if (err?.Errors is { Length: > 0 })
                            return AuthResult.Fail(string.Join("\n", err.Errors));

                        return AuthResult.Fail(DescribeDeny(err?.Code));
                    }
                    catch (JsonException)
                    {
                        return AuthResult.Fail("Invalid login or password.");
                    }
                }

                lastError = $"Authentication server returned HTTP {(int)resp.StatusCode}.";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = "Connection error: " + ex.Message;
                Log($"authenticate {endpoint} failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return AuthResult.Fail(lastError ?? "Could not connect to authentication servers.");
    }

    private static string DescribeDeny(string? code)
    {
        return code switch
        {
            "InvalidCredentials" => "Invalid username or password.",
            "AccountUnconfirmed" => "Account is not confirmed. Check your email.",
            "TfaRequired" => "Two factor authentication required.",
            "TfaInvalid" => "Invalid two factor code.",
            "AccountLocked" => "Account is locked.",
            null or "" => "Invalid login or password.",
            _ => "Login denied: " + code
        };
    }

    private static string Truncate(string value, int max)
    {
        return value.Length <= max ? value : value.Substring(0, max) + "...";
    }

    public async Task<bool> ValidateTokenAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;

        foreach (var baseUrl in AuthBases)
        {
            try
            {
                var endpoint = baseUrl + "api/auth/ping";
                using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(8));

                using var resp = await Http.SendAsync(request, cts.Token);
                if (resp.IsSuccessStatusCode) return true;
            }
            catch
            {
            }
        }

        return false;
    }

    public async Task LogoutAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return;

        foreach (var baseUrl in AuthBases)
        {
            try
            {
                var endpoint = baseUrl + "api/auth/logout";
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                request.Content = JsonContent.Create(new { token });
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await Http.SendAsync(request, cts.Token);
                return;
            }
            catch
            {
            }
        }
    }
}
