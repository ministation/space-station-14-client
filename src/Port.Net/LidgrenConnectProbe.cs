using System.Net;
using System.Net.Sockets;
using System.Text;
using Lidgren.Network;

namespace Port.Net;

public enum LidgrenProbePhase
{
    Idle,
    Resolving,
    Connecting,
    Connected,
    Failed,
    Cancelled,
}

public sealed record LidgrenProbeResult(
    LidgrenProbePhase Phase,
    string Detail,
    string? ResolvedIp = null,
    NetConnectionStatus? ConnectionStatus = null,
    TimeSpan Elapsed = default);

/// <summary>
/// Minimal Lidgren UDP connect probe (no Robust handshake/auth/content).
/// Tries public IPs first (DoH fallback) because VPN DNS often returns dead RFC1918 addresses.
/// </summary>
public sealed class LidgrenConnectProbe
{
    readonly object _gate = new();
    readonly List<string> _log = new();

    public LidgrenProbePhase Phase { get; private set; } = LidgrenProbePhase.Idle;
    public string Detail { get; private set; } = "idle";
    public string? ResolvedIp { get; private set; }
    public NetConnectionStatus? ConnectionStatus { get; private set; }

    public IReadOnlyList<string> SnapshotLog(int max = 12)
    {
        lock (_gate)
        {
            if (_log.Count <= max) return _log.ToArray();
            return _log.Skip(_log.Count - max).ToArray();
        }
    }

    public string Format()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"lidgren: {Phase}  {Detail}");
        if (!string.IsNullOrEmpty(ResolvedIp))
            sb.AppendLine($"resolved: {ResolvedIp}");
        if (ConnectionStatus is { } st)
            sb.AppendLine($"conn: {st}");
        foreach (var line in SnapshotLog())
            sb.AppendLine("  " + line);
        return sb.ToString().TrimEnd();
    }

    public async Task<LidgrenProbeResult> ConnectAsync(
        GameEndpoint endpoint,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Set(LidgrenProbePhase.Resolving, $"DNS {endpoint.Host}");

        IReadOnlyList<IPAddress> addrs;
        try
        {
            addrs = await HostResolver.ResolveAsync(endpoint.Host, ct);
            foreach (var a in addrs)
                Note($"candidate {a} ({(HostResolver.IsPrivate(a) ? "private" : "public")})");
        }
        catch (Exception ex)
        {
            Set(LidgrenProbePhase.Failed, $"DNS: {ex.Message}");
            return Result(sw.Elapsed);
        }

        Exception? last = null;
        foreach (var ip in addrs)
        {
            ct.ThrowIfCancellationRequested();
            ResolvedIp = ip.ToString();
            var perTry = TimeSpan.FromSeconds(Math.Max(4, timeout.TotalSeconds / Math.Max(1, addrs.Count)));
            Note($"try {ResolvedIp}:{endpoint.Port} timeout={perTry.TotalSeconds:0}s");
            try
            {
                var ok = await TryConnectOneAsync(endpoint, ip, perTry, ct);
                if (ok)
                    return Result(sw.Elapsed);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Set(LidgrenProbePhase.Cancelled, "cancelled");
                return Result(sw.Elapsed);
            }
            catch (Exception ex)
            {
                last = ex;
                Note($"fail {ip}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Set(LidgrenProbePhase.Failed,
            last is null
                ? "all candidates failed (no UDP response)"
                : $"all candidates failed: {last.GetType().Name}: {last.Message}");
        return Result(sw.Elapsed);
    }

    async Task<bool> TryConnectOneAsync(
        GameEndpoint endpoint,
        IPAddress ip,
        TimeSpan timeout,
        CancellationToken ct)
    {
        Set(LidgrenProbePhase.Connecting, $"{ip}:{endpoint.Port} app={endpoint.AppIdentifier}");
        NetPeer? peer = null;
        try
        {
            var config = LidgrenPeerFactory.Create(endpoint.AppIdentifier);
            peer = new NetPeer(config);
            peer.Start();
            Note($"NetPeer bound udp/{peer.Port}");

            var conn = peer.Connect(new IPEndPoint(ip, endpoint.Port));
            ConnectionStatus = conn.Status;
            Note($"Connect() issued, status={conn.Status}");

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(timeout);

            while (!linked.Token.IsCancellationRequested)
            {
                NetIncomingMessage? msg;
                while ((msg = peer.ReadMessage()) != null)
                {
                    try
                    {
                        HandleMessage(msg);
                    }
                    finally
                    {
                        peer.Recycle(msg);
                    }

                    if (Phase == LidgrenProbePhase.Connected)
                        return true;
                    if (Phase == LidgrenProbePhase.Failed)
                        return false;
                }

                if (conn.Status == NetConnectionStatus.Connected)
                {
                    ConnectionStatus = conn.Status;
                    Set(LidgrenProbePhase.Connected, "Lidgren Connected (pre-handshake)");
                    return true;
                }

                if (conn.Status is NetConnectionStatus.Disconnected or NetConnectionStatus.Disconnecting)
                {
                    ConnectionStatus = conn.Status;
                    Note($"disconnected early: {conn.Status}");
                    return false;
                }

                await Task.Delay(50, linked.Token);
            }

            Note($"timeout on {ip}");
            return false;
        }
        finally
        {
            try { peer?.Shutdown("probe try done"); } catch { /* ignore */ }
        }
    }

    void HandleMessage(NetIncomingMessage msg)
    {
        switch (msg.MessageType)
        {
            case NetIncomingMessageType.StatusChanged:
                var status = (NetConnectionStatus)msg.ReadByte();
                var reason = msg.ReadString();
                ConnectionStatus = status;
                Note($"status -> {status} ({reason})");
                if (status == NetConnectionStatus.Connected)
                    Set(LidgrenProbePhase.Connected, string.IsNullOrEmpty(reason) ? "connected" : reason);
                else if (status == NetConnectionStatus.Disconnected)
                    Note($"disconnected: {(string.IsNullOrEmpty(reason) ? "no reason" : reason)}");
                break;
            case NetIncomingMessageType.ErrorMessage:
            case NetIncomingMessageType.WarningMessage:
            case NetIncomingMessageType.DebugMessage:
            case NetIncomingMessageType.VerboseDebugMessage:
                Note($"{msg.MessageType}: {msg.ReadString()}");
                break;
            default:
                Note($"msg {msg.MessageType} bytes={msg.LengthBytes}");
                break;
        }
    }

    void Set(LidgrenProbePhase phase, string detail)
    {
        Phase = phase;
        Detail = detail;
        Note($"{phase}: {detail}");
    }

    void Note(string line)
    {
        lock (_gate)
        {
            _log.Add($"{DateTime.Now:HH:mm:ss.fff} {line}");
            if (_log.Count > 100)
                _log.RemoveRange(0, _log.Count - 80);
        }
    }

    LidgrenProbeResult Result(TimeSpan elapsed) =>
        new(Phase, Detail, ResolvedIp, ConnectionStatus, elapsed);
}
