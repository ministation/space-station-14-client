using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Port.Net;

public sealed record HubServerEntry(
    string Id,
    string Name,
    string Host,
    int Port,
    string? Region = null,
    bool Favorite = false,
    int Players = 0,
    int MaxPlayers = 0,
    string? Map = null,
    string? Preset = null,
    string? Description = null,
    IReadOnlyList<string>? Tags = null,
    string? RoundStart = null,
    int? RoundId = null,
    bool Online = true)
{
    public string ConnectUri => $"ss14://{Host}:{Port}";
    public string StatusUrl => $"http://{Host}:{Port}/status";
    public string HttpBaseUrl => $"http://{Host}:{Port}";
    public string PlayersLabel => MaxPlayers > 0 ? $"{Players}/{MaxPlayers}" : $"{Players}";
    public string SummaryLine
    {
        get
        {
            var parts = new List<string> { PlayersLabel };
            if (!string.IsNullOrWhiteSpace(Map)) parts.Add(Map!);
            if (!string.IsNullOrWhiteSpace(Preset)) parts.Add(Preset!);
            if (!string.IsNullOrWhiteSpace(Region)) parts.Add(Region!);
            return string.Join(" · ", parts);
        }
    }

    public GameEndpoint ToEndpoint() => new(Host, Port, StatusUrl: StatusUrl);

    public static HubServerEntry? TryParse(string raw, string? name = null)
    {
        raw = raw.Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        raw = Regex.Replace(raw, @"^(ss14s?|https?)://", "", RegexOptions.IgnoreCase);
        raw = raw.TrimEnd('/');
        var slash = raw.IndexOf('/');
        if (slash >= 0)
            raw = raw[..slash];

        string host;
        var port = 1212;
        var colon = raw.LastIndexOf(':');
        if (colon > 0 && int.TryParse(raw[(colon + 1)..], out var p) && p is > 0 and < 65536)
        {
            host = raw[..colon];
            port = p;
        }
        else
        {
            host = raw;
        }

        if (string.IsNullOrWhiteSpace(host))
            return null;

        var id = $"{host}:{port}".ToLowerInvariant();
        return new HubServerEntry(id, name ?? host, host, port, Favorite: true);
    }

    public static HubServerEntry? FromSs14Address(string address, string? name = null)
    {
        if (string.IsNullOrWhiteSpace(address))
            return null;
        return TryParse(address, name);
    }
}

/// <summary>
/// Full SS14 hub list (hub.spacestation14.com) + local favorites.
/// </summary>
public sealed class HubServerCatalog
{
    public const string DefaultHubUrl = "https://hub.spacestation14.com/api/servers";

    readonly string? _favoritesPath;
    readonly HashSet<string> _favoriteIds = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, HubServerEntry> _custom = new(StringComparer.OrdinalIgnoreCase);
    List<HubServerEntry> _hub = new();
    readonly object _gate = new();

    public string HubUrl { get; set; } = DefaultHubUrl;
    public string? LastError { get; private set; }
    public DateTime? LastRefreshUtc { get; private set; }

    public HubServerCatalog(string? favoritesPath = null)
    {
        _favoritesPath = favoritesPath;
        LoadFavorites();
    }

    public IReadOnlyList<HubServerEntry> All
    {
        get
        {
            lock (_gate)
            {
                var map = new Dictionary<string, HubServerEntry>(StringComparer.OrdinalIgnoreCase);

                foreach (var s in _hub)
                    map[s.Id] = ApplyFavoriteFlag(s);

                foreach (var s in _custom.Values)
                {
                    if (map.TryGetValue(s.Id, out var existing))
                        map[s.Id] = existing with { Favorite = true, Name = string.IsNullOrWhiteSpace(s.Name) ? existing.Name : s.Name };
                    else
                        map[s.Id] = ApplyFavoriteFlag(s with { Favorite = true });
                }

                // Favorites that disappeared from hub still show.
                foreach (var id in _favoriteIds)
                {
                    if (map.ContainsKey(id))
                        continue;
                    if (_custom.TryGetValue(id, out var c))
                        map[id] = c with { Favorite = true, Online = false };
                }

                return map.Values
                    .OrderByDescending(s => s.Favorite)
                    .ThenByDescending(s => s.Players)
                    .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }
    }

    public async Task RefreshFromHubAsync(CancellationToken ct = default)
    {
        LastError = null;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "SS14-MobileHub/0.2");
            await using var stream = await http.GetStreamAsync(HubUrl, ct);
            var rows = await JsonSerializer.DeserializeAsync<List<HubApiRow>>(stream, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            }, ct) ?? new List<HubApiRow>();

            var parsed = new List<HubServerEntry>(rows.Count);
            foreach (var row in rows)
            {
                var entry = ParseHubRow(row);
                if (entry != null)
                    parsed.Add(entry);
            }

            lock (_gate)
            {
                _hub = parsed;
                LastRefreshUtc = DateTime.UtcNow;
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            throw;
        }
    }

    public bool AddCustom(HubServerEntry entry)
    {
        lock (_gate)
        {
            _favoriteIds.Add(entry.Id);
            _custom[entry.Id] = entry with { Favorite = true };
            SaveFavorites();
        }
        return true;
    }

    public bool ToggleFavorite(string id)
    {
        lock (_gate)
        {
            if (_favoriteIds.Contains(id))
            {
                _favoriteIds.Remove(id);
                // Keep custom entries but unfavorite if they came only from hub.
                if (_custom.TryGetValue(id, out var c) && !_hub.Any(h => h.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                {
                    // custom-only: removing favorite removes from list
                    _custom.Remove(id);
                }
            }
            else
            {
                _favoriteIds.Add(id);
                var fromHub = _hub.FirstOrDefault(h => h.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                if (fromHub != null)
                    _custom[id] = fromHub with { Favorite = true };
            }

            SaveFavorites();
            return _favoriteIds.Contains(id);
        }
    }

    public bool IsFavorite(string id)
    {
        lock (_gate)
            return _favoriteIds.Contains(id);
    }

    public void SetDescription(string id, string? description)
    {
        lock (_gate)
        {
            for (var i = 0; i < _hub.Count; i++)
            {
                if (!_hub[i].Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                    continue;
                _hub[i] = _hub[i] with { Description = description };
                break;
            }

            if (_custom.TryGetValue(id, out var c))
                _custom[id] = c with { Description = description };
        }
    }

    HubServerEntry ApplyFavoriteFlag(HubServerEntry s) =>
        s with { Favorite = _favoriteIds.Contains(s.Id) };

    static HubServerEntry? ParseHubRow(HubApiRow row)
    {
        if (string.IsNullOrWhiteSpace(row.Address))
            return null;
        var baseEntry = HubServerEntry.FromSs14Address(row.Address);
        if (baseEntry is null)
            return null;

        var st = row.StatusData;
        var name = st?.Name;
        if (string.IsNullOrWhiteSpace(name))
            name = baseEntry.Name;

        string? region = null;
        var tags = st?.Tags ?? Array.Empty<string>();
        foreach (var t in tags)
        {
            if (t.StartsWith("region:", StringComparison.OrdinalIgnoreCase))
            {
                region = t["region:".Length..];
                break;
            }
        }

        return baseEntry with
        {
            Name = name!,
            Region = region,
            Players = st?.Players ?? 0,
            MaxPlayers = st?.SoftMaxPlayers ?? 0,
            Map = st?.Map,
            Preset = st?.Preset,
            Tags = tags,
            RoundStart = st?.RoundStartTime,
            RoundId = st?.RoundId,
            Online = true,
        };
    }

    void LoadFavorites()
    {
        _favoriteIds.Clear();
        _custom.Clear();
        if (string.IsNullOrWhiteSpace(_favoritesPath) || !File.Exists(_favoritesPath))
            return;
        try
        {
            var json = File.ReadAllText(_favoritesPath);
            var list = JsonSerializer.Deserialize<List<Stored>>(json);
            if (list is null) return;
            foreach (var s in list)
            {
                var e = HubServerEntry.TryParse($"{s.Host}:{s.Port}", s.Name);
                if (e is null) continue;
                _favoriteIds.Add(e.Id);
                _custom[e.Id] = e with { Favorite = true };
            }
        }
        catch
        {
            /* ignore corrupt favorites */
        }
    }

    void SaveFavorites()
    {
        if (string.IsNullOrWhiteSpace(_favoritesPath))
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_favoritesPath)!);
            var list = new List<Stored>();
            foreach (var id in _favoriteIds)
            {
                HubServerEntry? e = null;
                if (_custom.TryGetValue(id, out var c))
                    e = c;
                else
                    e = _hub.FirstOrDefault(h => h.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                if (e is null)
                {
                    var parsed = HubServerEntry.TryParse(id);
                    if (parsed != null)
                        e = parsed;
                }

                if (e != null)
                    list.Add(new Stored(e.Name, e.Host, e.Port));
            }

            File.WriteAllText(_favoritesPath,
                JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            /* ignore */
        }
    }

    sealed record Stored(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("host")] string Host,
        [property: JsonPropertyName("port")] int Port);

    sealed class HubApiRow
    {
        public string? Address { get; set; }
        public HubStatusData? StatusData { get; set; }
    }

    sealed class HubStatusData
    {
        public string? Name { get; set; }
        public string? Map { get; set; }
        public string? Preset { get; set; }
        public int Players { get; set; }
        [JsonPropertyName("soft_max_players")] public int SoftMaxPlayers { get; set; }
        public string[]? Tags { get; set; }
        [JsonPropertyName("round_id")] public int? RoundId { get; set; }
        [JsonPropertyName("round_start_time")] public string? RoundStartTime { get; set; }
    }
}
