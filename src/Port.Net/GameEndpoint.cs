namespace Port.Net;

public sealed record GameEndpoint(
    string Host,
    int Port,
    string AppIdentifier = "RobustToolbox",
    string? StatusUrl = null)
{
    public static GameEndpoint MiniStation { get; } = new(
        Host: "ss14.ministation.ru",
        Port: 1214,
        AppIdentifier: "RobustToolbox",
        StatusUrl: "http://ss14.ministation.ru:1214/status");

    public string ConnectUri => $"ss14://{Host}:{Port}";
    public string HttpBaseUrl => $"http://{Host}:{Port}";
}

public sealed record GameStatusInfo(
    bool Online,
    string Name,
    int Players,
    int MaxPlayers,
    string Map,
    string Preset,
    string? Error = null);
