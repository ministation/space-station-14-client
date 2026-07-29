using System.Net.Http.Json;
using System.Text.Json;

namespace Port.Net;

public sealed class GameStatusClient
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    readonly HttpClient _http;

    public GameStatusClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
    }

    public async Task<GameStatusInfo> FetchAsync(GameEndpoint endpoint, CancellationToken ct = default)
    {
        var url = endpoint.StatusUrl;
        if (string.IsNullOrWhiteSpace(url))
            return new GameStatusInfo(false, endpoint.Host, 0, 0, "", "", "no status url");

        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
                return new GameStatusInfo(false, endpoint.Host, 0, 0, "", "", $"HTTP {(int)resp.StatusCode}");

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var dto = await JsonSerializer.DeserializeAsync<StatusDto>(stream, JsonOptions, ct);
            if (dto is null)
                return new GameStatusInfo(false, endpoint.Host, 0, 0, "", "", "empty json");

            return new GameStatusInfo(
                Online: true,
                Name: dto.Name ?? endpoint.Host,
                Players: dto.Players,
                MaxPlayers: dto.SoftMaxPlayers > 0 ? dto.SoftMaxPlayers : 100,
                Map: dto.Map ?? "",
                Preset: dto.Preset ?? "");
        }
        catch (Exception ex)
        {
            return new GameStatusInfo(false, endpoint.Host, 0, 0, "", "", ex.GetType().Name + ": " + ex.Message);
        }
    }

    sealed class StatusDto
    {
        public string? Name { get; set; }
        public int Players { get; set; }
        public int SoftMaxPlayers { get; set; }
        public string? Map { get; set; }
        public string? Preset { get; set; }
    }
}
