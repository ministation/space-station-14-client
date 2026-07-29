using Port.Content;

namespace Port.Net;

/// <summary>
/// Orchestrates HTTP status + Lidgren connect for the Android host UI.
/// </summary>
public sealed class NetworkProbeSession
{
    readonly GameStatusClient _status = new();
    public GameEndpoint Endpoint { get; set; } = GameEndpoint.MiniStation;
    public string? AuthConfigPath { get; set; }
    public GameStatusInfo? LastStatus { get; private set; }
    public LidgrenConnectProbe Lidgren { get; } = new();
    public RobustHandshakeProbe Handshake { get; } = new();
    public bool Busy { get; private set; }
    public string Summary { get; private set; } = "net: idle — press Probe network";

    public async Task RunAsync(CancellationToken ct = default)
    {
        if (Busy) return;
        Busy = true;
        try
        {
            Summary = "net: fetching HTTP status…";
            LastStatus = await _status.FetchAsync(Endpoint, ct);
            Summary = LastStatus.Online
                ? $"net: HTTP OK — {LastStatus.Players}/{LastStatus.MaxPlayers} on {LastStatus.Map}"
                : $"net: HTTP FAIL — {LastStatus.Error}";

            Summary += "\nnet: Lidgren connecting…";
            var result = await Lidgren.ConnectAsync(Endpoint, TimeSpan.FromSeconds(12), ct);
            Summary = LastStatus.Online
                ? $"net: HTTP OK — {LastStatus.Players}/{LastStatus.MaxPlayers}\n"
                : $"net: HTTP FAIL — {LastStatus.Error}\n";
            Summary += $"net: Lidgren {result.Phase} ({result.Elapsed.TotalSeconds:0.0}s) — {result.Detail}";

            if (LastStatus.Online)
            {
                var auth = AuthSessionConfig.TryLoad(AuthConfigPath);
                var serverInfo = await new ServerInfoClient().FetchAsync(
                    Endpoint.HttpBaseUrl,
                    ct);

                Summary += "\nnet: handshake…";
                var hs = await Handshake.RunAsync(
                    Endpoint,
                    serverInfo.AuthMode,
                    serverInfo.PublicKey,
                    auth,
                    TimeSpan.FromSeconds(20),
                    ct);

                Summary += $"\nnet: handshake {hs.Phase} ({hs.Elapsed.TotalSeconds:0.0}s) — {hs.Detail}";
            }
        }
        catch (Exception ex)
        {
            Summary = $"net: ERROR {ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            Busy = false;
        }
    }

    public string Format()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(Summary);
        sb.AppendLine($"target: {Endpoint.ConnectUri}");
        var auth = AuthSessionConfig.TryLoad(AuthConfigPath);
        sb.AppendLine(auth?.StatusLine() ?? "auth: not logged in");
        if (LastStatus is { } s)
        {
            sb.AppendLine(s.Online
                ? $"status: {s.Name} | {s.Players}/{s.MaxPlayers} | {s.Map} | {s.Preset}"
                : $"status: offline ({s.Error})");
        }
        sb.AppendLine(Lidgren.Format());
        sb.AppendLine(Handshake.Format());
        if (!string.IsNullOrWhiteSpace(AuthConfigPath))
            sb.AppendLine($"auth config: {AuthConfigPath}");
        return sb.ToString().TrimEnd();
    }
}
