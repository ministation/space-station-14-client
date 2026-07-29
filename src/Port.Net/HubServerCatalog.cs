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
    bool Favorite = false)
{
    public string ConnectUri => $"ss14://{Host}:{Port}";
    public string StatusUrl => $"http://{Host}:{Port}/status";
    public string HttpBaseUrl => $"http://{Host}:{Port}";

    public GameEndpoint ToEndpoint() => new(Host, Port, StatusUrl: StatusUrl);

    public static HubServerEntry? TryParse(string raw, string? name = null)
    {
        raw = raw.Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        // ss14://host:port or host:port or http://host:port
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
        return new HubServerEntry(id, name ?? host, host, port);
    }
}

/// <summary>
/// Built-in + user favorites for the public SS14 mobile hub.
/// </summary>
public sealed class HubServerCatalog
{
    static readonly HubServerEntry[] BuiltIn =
    [
        new("ss14.ministation.ru:1214", "Mini Station", "ss14.ministation.ru", 1214, "RU", Favorite: true),
        new("lizard.spacestation14.com:1212", "Wizard's Den — Lizard", "lizard.spacestation14.com", 1212, "US"),
        new("leviathan.spacestation14.com:1212", "Wizard's Den — Leviathan", "leviathan.spacestation14.com", 1212, "US"),
        new("frontier.spacestation14.com:1212", "Frontier Station", "frontier.spacestation14.com", 1212, "US"),
    ];

    readonly string? _favoritesPath;
    readonly List<HubServerEntry> _custom = new();

    public HubServerCatalog(string? favoritesPath = null)
    {
        _favoritesPath = favoritesPath;
        Load();
    }

    public IReadOnlyList<HubServerEntry> All
    {
        get
        {
            var map = new Dictionary<string, HubServerEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in BuiltIn)
                map[s.Id] = s;
            foreach (var s in _custom)
                map[s.Id] = s;
            return map.Values
                .OrderByDescending(s => s.Favorite)
                .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public bool AddCustom(HubServerEntry entry)
    {
        var existing = _custom.FindIndex(s => s.Id.Equals(entry.Id, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
            _custom[existing] = entry with { Favorite = true };
        else
            _custom.Add(entry with { Favorite = true });
        Save();
        return true;
    }

    void Load()
    {
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
                if (e != null)
                    _custom.Add(e with { Favorite = true });
            }
        }
        catch
        {
            /* ignore corrupt favorites */
        }
    }

    void Save()
    {
        if (string.IsNullOrWhiteSpace(_favoritesPath))
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_favoritesPath)!);
            var list = _custom.Select(s => new Stored(s.Name, s.Host, s.Port)).ToList();
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
}
