using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Port.Content;

public sealed record EnginePlatformBuild(string Rid, string Url, string? Sha256, string? Signature);

public sealed record EngineVersionInfo(
    string RequestedVersion,
    string ResolvedVersion,
    bool Insecure,
    IReadOnlyList<EnginePlatformBuild> Platforms);

/// <summary>
/// Downloads Robust engine zips from the official builds CDN (same as SS14.Launcher).
/// There is currently no android RID — on phones we fetch linux-arm64 for assemblies/probe only.
/// </summary>
public sealed class EngineBuildClient
{
    public const string ManifestUrl = "https://robust-builds.cdn.spacestation14.com/manifest.json";
    public const string ManifestFallbackUrl = "https://robust-builds.fallback.cdn.spacestation14.com/manifest.json";

    readonly HttpClient _http;
    static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public EngineBuildClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    }

    public async Task<EngineVersionInfo> GetVersionAsync(string engineVersion, CancellationToken ct = default)
    {
        using var doc = await FetchManifestAsync(ct);
        if (!doc.RootElement.TryGetProperty(engineVersion, out var verEl))
            throw new InvalidOperationException($"engine version {engineVersion} not in CDN manifest");

        var insecure = verEl.TryGetProperty("insecure", out var ins) && ins.GetBoolean();
        var platforms = new List<EnginePlatformBuild>();
        if (verEl.TryGetProperty("platforms", out var plats))
        {
            foreach (var p in plats.EnumerateObject())
            {
                var url = p.Value.TryGetProperty("url", out var u) ? u.GetString() : null;
                if (string.IsNullOrWhiteSpace(url))
                    continue;
                platforms.Add(new EnginePlatformBuild(
                    p.Name,
                    url,
                    p.Value.TryGetProperty("sha256", out var h) ? h.GetString() : null,
                    p.Value.TryGetProperty("sig", out var s) ? s.GetString() : null));
            }
        }

        if (platforms.Count == 0)
            throw new InvalidOperationException($"engine {engineVersion} has no platforms");

        return new EngineVersionInfo(engineVersion, engineVersion, insecure, platforms);
    }

    public EnginePlatformBuild PickBestPlatform(EngineVersionInfo info, bool preferArm64)
    {
        // Official builds have no android RID yet.
        string[] preference = preferArm64
            ?
            [
                "linux-arm64",
                "linux-x64",
                "win-arm64",
                "win-x64",
            ]
            :
            [
                "linux-x64",
                "linux-arm64",
                "win-x64",
                "win-arm64",
            ];

        foreach (var rid in preference)
        {
            var hit = info.Platforms.FirstOrDefault(p =>
                p.Rid.Equals(rid, StringComparison.OrdinalIgnoreCase));
            if (hit is not null)
                return hit;
        }

        return info.Platforms[0];
    }

    public async Task<string> DownloadAsync(
        EnginePlatformBuild build,
        string destZipPath,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destZipPath)!);
        if (File.Exists(destZipPath) && new FileInfo(destZipPath).Length < 1024)
            File.Delete(destZipPath);

        Exception? last = null;
        foreach (var url in ExpandDownloadUrls(build.Url))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"GET {url}");
            try
            {
                using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    last = new HttpRequestException(
                        $"engine download HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {Truncate(body, 200)}");
                    progress?.Report(last.Message);
                    continue;
                }

                var total = resp.Content.Headers.ContentLength;
                var tempPath = destZipPath + ".partial";
                await using (var input = await resp.Content.ReadAsStreamAsync(ct))
                await using (var output = File.Create(tempPath))
                {
                    var buffer = new byte[128 * 1024];
                    long copied = 0;
                    int n;
                    while ((n = await input.ReadAsync(buffer, ct)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, n), ct);
                        copied += n;
                        if (total is > 0)
                            progress?.Report($"{build.Rid}: {copied * 100 / total.Value}% ({copied:N0}/{total:N0})");
                        else
                            progress?.Report($"{build.Rid}: {copied:N0} bytes");
                    }

                    await output.FlushAsync(ct);
                }

                if (File.Exists(destZipPath))
                    File.Delete(destZipPath);
                File.Move(tempPath, destZipPath);

                if (!string.IsNullOrWhiteSpace(build.Sha256))
                {
                    progress?.Report("verifying sha256…");
                    await using var verify = File.OpenRead(destZipPath);
                    var hash = Convert.ToHexString(await SHA256.HashDataAsync(verify, ct));
                    if (!hash.Equals(build.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"sha256 mismatch: got {hash}, expected {build.Sha256}");
                    progress?.Report("sha256 OK");
                }

                return destZipPath;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                progress?.Report($"fail: {ex.GetType().Name}: {ex.Message}");
                try
                {
                    var partial = destZipPath + ".partial";
                    if (File.Exists(partial)) File.Delete(partial);
                }
                catch { /* ignore */ }
            }
        }

        throw last ?? new InvalidOperationException("engine download failed");
    }

    static IEnumerable<string> ExpandDownloadUrls(string primary)
    {
        yield return primary;
        if (!Uri.TryCreate(primary, UriKind.Absolute, out var uri))
            yield break;

        // Official launcher uses UrlFallbackSet for CDN hosts (DPI / regional blocks).
        // Only swap host — keep the path from the manifest URL.
        string[] altHosts =
        [
            "robust-builds.cdn.spacestation14.com",
            "robust-builds.fallback.cdn.spacestation14.com",
        ];

        if (uri.Host.Contains("playss14", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var host in altHosts)
            {
                var builder = new UriBuilder(uri) { Host = host };
                yield return builder.Uri.ToString();
            }
        }
    }

    async Task<JsonDocument> FetchManifestAsync(CancellationToken ct)
    {
        Exception? last = null;
        foreach (var url in new[] { ManifestUrl, ManifestFallbackUrl })
        {
            try
            {
                using var resp = await _http.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    last = new HttpRequestException(
                        $"manifest HTTP {(int)resp.StatusCode}: {Truncate(body, 160)}");
                    continue;
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        throw last ?? new InvalidOperationException("engine manifest fetch failed");
    }

    static string Truncate(string s, int n) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…");
}
