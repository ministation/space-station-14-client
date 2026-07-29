using System.Text.Json;
using System.Text.Json.Serialization;

namespace Port.Content;

public sealed record ServerBuildInfo(
    string EngineVersion,
    string ForkId,
    string Version,
    string DownloadUrl,
    string ManifestUrl,
    string ManifestDownloadUrl,
    string Hash,
    string ManifestHash,
    bool Acz,
    string AuthMode,
    string PublicKey,
    string ConnectAddress,
    string? Description);

public sealed class ServerInfoClient
{
    readonly HttpClient _http;
    static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public ServerInfoClient(HttpClient? http = null)
    {
        _http = http ?? PortHttp.Create(TimeSpan.FromSeconds(30));
    }

    public async Task<ServerBuildInfo> FetchAsync(string statusBaseUrl, CancellationToken ct = default)
    {
        var baseUrl = statusBaseUrl.TrimEnd('/');
        var url = baseUrl.EndsWith("/info", StringComparison.OrdinalIgnoreCase)
            ? baseUrl
            : baseUrl + "/info";

        await using var stream = await _http.GetStreamAsync(url, ct);
        var dto = await JsonSerializer.DeserializeAsync<InfoDto>(stream, JsonOptions, ct)
                  ?? throw new InvalidOperationException("empty /info");

        var build = dto.Build ?? throw new InvalidOperationException("/info missing build");
        return new ServerBuildInfo(
            EngineVersion: build.EngineVersion ?? "",
            ForkId: build.ForkId ?? "custom",
            Version: build.Version ?? "",
            DownloadUrl: build.DownloadUrl ?? "",
            ManifestUrl: build.ManifestUrl ?? "",
            ManifestDownloadUrl: build.ManifestDownloadUrl ?? "",
            Hash: build.Hash ?? "",
            ManifestHash: build.ManifestHash ?? "",
            Acz: build.Acz,
            AuthMode: dto.Auth?.Mode ?? "",
            PublicKey: dto.Auth?.PublicKey ?? "",
            ConnectAddress: dto.ConnectAddress ?? "",
            Description: dto.Desc);
    }

    sealed class InfoDto
    {
        [JsonPropertyName("connect_address")] public string? ConnectAddress { get; set; }
        public AuthDto? Auth { get; set; }
        public BuildDto? Build { get; set; }
        public string? Desc { get; set; }
    }

    sealed class AuthDto
    {
        public string? Mode { get; set; }
        [JsonPropertyName("public_key")] public string? PublicKey { get; set; }
    }

    sealed class BuildDto
    {
        [JsonPropertyName("engine_version")] public string? EngineVersion { get; set; }
        [JsonPropertyName("fork_id")] public string? ForkId { get; set; }
        public string? Version { get; set; }
        [JsonPropertyName("download_url")] public string? DownloadUrl { get; set; }
        [JsonPropertyName("manifest_url")] public string? ManifestUrl { get; set; }
        [JsonPropertyName("manifest_download_url")] public string? ManifestDownloadUrl { get; set; }
        public string? Hash { get; set; }
        [JsonPropertyName("manifest_hash")] public string? ManifestHash { get; set; }
        public bool Acz { get; set; }
    }
}
