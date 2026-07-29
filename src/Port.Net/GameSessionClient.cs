using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lidgren.Network;
using Robust.Shared.Enums;
using Robust.Shared.Network;
using SpaceWizards.Sodium;

namespace Port.Net;

public enum GameSessionPhase
{
    Idle,
    Connecting,
    Authenticating,
    LoginSuccess,
    StringTable,
    MapStrings,
    Transfer,
    SyncingCVars,
    RequestingPlayers,
    InLobby,
    Observing,
    Failed,
    Skipped,
}

public sealed record LobbyPlayer(Guid UserId, string Name, SessionStatus Status);

public sealed record GameSessionResult(
    GameSessionPhase Phase,
    string Detail,
    string? UserName = null,
    Guid? UserId = null,
    LoginType? LoginType = null,
    IReadOnlyList<LobbyPlayer>? Players = null,
    TimeSpan Elapsed = default);

/// <summary>
/// Authenticated connect that continues past LoginSuccess into lobby:
/// string table → mapstr skip → transfer ack/WS → MsgConVars → MsgPlayerList.
/// </summary>
public sealed class GameSessionClient : IDisposable
{
    const int TransferKeyBytes = 32;
    const string TransferKeyHeader = "RT-Key";
    const string TransferUserHeader = "RT-UserId";

    readonly object _gate = new();
    readonly List<string> _log = new();
    readonly Dictionary<int, string> _msgNames = new();
    readonly Dictionary<string, int> _msgIds = new();

    NetPeer? _peer;
    NetConnection? _conn;
    NetPacketCrypto? _crypto;
    ClientWebSocket? _transferWs;
    CancellationTokenSource? _keepAliveCts;

    public GameSessionPhase Phase { get; private set; } = GameSessionPhase.Idle;
    public string Detail { get; private set; } = "idle";
    public string? UserName { get; private set; }
    public Guid? UserId { get; private set; }
    public LoginType? LoginType { get; private set; }
    public IReadOnlyList<LobbyPlayer> Players { get; private set; } = Array.Empty<LobbyPlayer>();
    public bool IsReady { get; private set; }
    public bool IsObserving { get; private set; }
    public int StatesReceived { get; private set; }
    public int LastStateBytes { get; private set; }
    public string LastCommandAck { get; private set; } = "";
    public string LastEyeHint { get; private set; } = "";
    public EyeSnapshot? LastEye { get; private set; }
    public SessionStatus LocalStatus { get; private set; } = SessionStatus.Connecting;
    public float CamX { get; private set; }
    public float CamY { get; private set; }
    public string? AssembliesDirectory { get; set; }
    public string SerializerStatus { get; private set; } = "serializer: not started";

    SerializerBootstrap? _serializer;
    byte[]? _mapStrHash;
    bool _mapStrRequested;

    public bool IsConnected =>
        (Phase is GameSessionPhase.InLobby or GameSessionPhase.Observing) &&
        _conn is { Status: NetConnectionStatus.Connected };

    public IReadOnlyList<string> SnapshotLogPublic(int max = 24) => SnapshotLog(max);

    public string Format()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"session: {Phase}  {Detail}");
        if (!string.IsNullOrWhiteSpace(UserName))
            sb.AppendLine($"user: {UserName}  id={UserId}  login={LoginType}");
        if (Players.Count > 0)
        {
            sb.AppendLine($"lobby players ({Players.Count}):");
            foreach (var p in Players.Take(24))
                sb.AppendLine($"  [{p.Status}] {p.Name}");
            if (Players.Count > 24)
                sb.AppendLine($"  … +{Players.Count - 24} more");
        }

        sb.AppendLine($"ready={IsReady} observe={IsObserving} local={LocalStatus} states={StatesReceived}");
        sb.AppendLine(SerializerStatus);
        if (LastEye is { } eye)
            sb.AppendLine($"eye: {eye.Detail}");
        if (!string.IsNullOrWhiteSpace(LastCommandAck))
            sb.AppendLine($"cmd: {LastCommandAck}");
        if (!string.IsNullOrWhiteSpace(LastEyeHint))
            sb.AppendLine($"hint: {LastEyeHint}");

        foreach (var line in SnapshotLog(24))
            sb.AppendLine("  " + line);
        return sb.ToString().TrimEnd();
    }

    public bool SendConsole(string command)
    {
        if (!IsConnected)
            return false;
        try
        {
            SendNamed("MsgConCmd", NetDeliveryMethod.ReliableUnordered, m => m.Write(command));
            Note($">> {command}");
            return true;
        }
        catch (Exception ex)
        {
            Note($"cmd fail: {ex.Message}");
            return false;
        }
    }

    public bool SetReady(bool ready)
    {
        IsReady = ready;
        return SendConsole(ready ? "toggleready True" : "toggleready False");
    }

    public bool Observe()
    {
        if (!SendConsole("observe"))
            return false;
        IsObserving = true;
        Set(GameSessionPhase.Observing, "sent observe — awaiting MsgState / ghost attach");
        return true;
    }

    public void PanCamera(float dx, float dy)
    {
        CamX += dx;
        CamY += dy;
    }

    public async Task<GameSessionResult> JoinLobbyAsync(
        GameEndpoint endpoint,
        string authMode,
        string serverPublicKey,
        AuthSessionConfig auth,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (auth.HasRequiredFields != true)
            {
                Set(GameSessionPhase.Failed, "auth-session.json missing token/userId — Login SS14 first");
                return Result(sw.Elapsed);
            }

            Set(GameSessionPhase.Connecting, $"{endpoint.Host}:{endpoint.Port}");
            Note($"resolve {endpoint.Host}…");
            var addrs = await HostResolver.ResolveAsync(endpoint.Host, ct);
            Note($"dns candidates: {addrs.Count}");
            foreach (var a in addrs)
                Note($"  dns {a} ({(HostResolver.IsPrivate(a) ? "PRIVATE-SKIP?" : "public")})");

            Exception? last = null;
            foreach (var ip in addrs)
            {
                ct.ThrowIfCancellationRequested();
                DisposeConnection();
                Note($"try {ip}:{endpoint.Port}");
                try
                {
                    var perTry = TimeSpan.FromSeconds(Math.Max(35, timeout.TotalSeconds));
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    linked.CancelAfter(perTry);

                    await ConnectAndLoginAsync(endpoint, ip, authMode, serverPublicKey, auth, linked.Token);
                    await BootstrapLobbyAsync(linked.Token);

                    Set(GameSessionPhase.InLobby,
                        $"in lobby as {UserName} — {Players.Count} player(s)");
                    LocalStatus = Players.FirstOrDefault(p => p.UserId == UserId)?.Status
                                  ?? SessionStatus.Connected;

                    // Stay connected for the lobby UI; cancel only stops bootstrap timeout.
                    _keepAliveCts?.Cancel();
                    _keepAliveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    _ = Task.Run(() => KeepAliveLoopAsync(_keepAliveCts.Token), CancellationToken.None);

                    return Result(sw.Elapsed);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    Set(GameSessionPhase.Skipped, "cancelled");
                    DisposeConnection();
                    return Result(sw.Elapsed);
                }
                catch (Exception ex)
                {
                    last = ex;
                    Note($"fail {ip}: {ex.GetType().Name}: {ex.Message}");
                    Set(GameSessionPhase.Failed, $"{ex.GetType().Name}: {ex.Message}");
                    DisposeConnection();
                }
            }

            if (Phase != GameSessionPhase.InLobby)
            {
                Set(GameSessionPhase.Failed,
                    last is null
                        ? "all candidates failed"
                        : $"all candidates failed: {last.GetType().Name}: {last.Message}");
                DisposeConnection();
            }

            return Result(sw.Elapsed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Set(GameSessionPhase.Skipped, "cancelled");
            DisposeConnection();
            return Result(sw.Elapsed);
        }
        catch (Exception ex)
        {
            Set(GameSessionPhase.Failed, $"{ex.GetType().Name}: {ex.Message}");
            DisposeConnection();
            return Result(sw.Elapsed);
        }
        // Intentionally keep the UDP channel open on InLobby for the character menu UI.
    }

    public void Disconnect(string reason = "client disconnect")
    {
        Note($"disconnect: {reason}");
        if (Phase is GameSessionPhase.InLobby or GameSessionPhase.LoginSuccess
            or GameSessionPhase.SyncingCVars or GameSessionPhase.RequestingPlayers)
            Set(GameSessionPhase.Idle, reason);
        DisposeConnection();
    }

    async Task KeepAliveLoopAsync(CancellationToken ct)
    {
        try
        {
            await DrainKeepAliveAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // expected on Disconnect
        }
        catch (Exception ex)
        {
            Note($"keepalive end: {ex.GetType().Name}: {ex.Message}");
            if (Phase == GameSessionPhase.InLobby)
                Set(GameSessionPhase.Failed, ex.Message);
            DisposeConnection();
        }
    }

    async Task ConnectAndLoginAsync(
        GameEndpoint endpoint,
        IPAddress ip,
        string authMode,
        string serverPublicKey,
        AuthSessionConfig auth,
        CancellationToken ct)
    {
        _peer = LidgrenPeerFactory.Start(endpoint.AppIdentifier, Note);
        _conn = _peer.Connect(new IPEndPoint(ip, endpoint.Port));
        Note($"connecting → {ip}:{endpoint.Port} app={endpoint.AppIdentifier}");

        var loginSent = false;
        while (!ct.IsCancellationRequested)
        {
            await PumpAsync(async msg =>
            {
                if (msg.MessageType == NetIncomingMessageType.StatusChanged)
                {
                    var status = (NetConnectionStatus)msg.ReadByte();
                    var reason = msg.ReadString();
                    Note($"status -> {status} ({reason})");
                    if (status == NetConnectionStatus.Disconnected)
                        throw new InvalidOperationException(
                            string.IsNullOrWhiteSpace(reason) ? "disconnected before login" : reason);
                }
                else if (msg.MessageType == NetIncomingMessageType.Data)
                {
                    await HandlePreLoginDataAsync(msg, authMode, serverPublicKey, auth, ct);
                    return true;
                }
                else if (msg.MessageType is NetIncomingMessageType.ErrorMessage
                         or NetIncomingMessageType.WarningMessage
                         or NetIncomingMessageType.DebugMessage)
                {
                    Note($"{msg.MessageType}: {msg.ReadString()}");
                }

                return false;
            }, ct);

            if (!loginSent && _conn.Status == NetConnectionStatus.Connected)
            {
                Set(GameSessionPhase.Authenticating, "sending MsgLoginStart");
                SendLoginStart(auth, needPubKey: string.IsNullOrWhiteSpace(auth.PublicKey));
                loginSent = true;
            }

            if (Phase == GameSessionPhase.LoginSuccess)
                return;

            await Task.Delay(40, ct);
        }

        throw new TimeoutException("timeout waiting for LoginSuccess");
    }

    async Task HandlePreLoginDataAsync(
        NetIncomingMessage msg,
        string authMode,
        string serverPublicKey,
        AuthSessionConfig auth,
        CancellationToken ct)
    {
        // Encrypted LoginSuccess has no leading bool; unencrypted guest path does.
        // Auth servers always encrypt after MsgEncryptionResponse.
        if (_crypto != null)
        {
            if (!_crypto.TryDecrypt(msg))
                throw new InvalidOperationException("failed to decrypt login success");
            ParseLoginSuccess(msg);
            Set(GameSessionPhase.LoginSuccess, "authenticated LoginSuccess");
            return;
        }

        var loginOk = msg.ReadBoolean();
        msg.ReadPadBits();
        if (loginOk)
        {
            ParseLoginSuccess(msg);
            Set(GameSessionPhase.LoginSuccess, "guest LoginSuccess (no encryption)");
            return;
        }

        if (authMode.Equals("Required", StringComparison.OrdinalIgnoreCase) && auth.HasRequiredFields != true)
            throw new InvalidOperationException("server requires auth");

        Set(GameSessionPhase.Authenticating, "MsgEncryptionRequest");
        var encReq = ReadEncryptionRequest(msg);

        byte[] publicKey;
        if (encReq.PublicKey is { Length: > 0 })
        {
            publicKey = encReq.PublicKey;
            Note($"pubkey from MsgEncryptionRequest ({publicKey.Length}B)");
        }
        else if (!string.IsNullOrWhiteSpace(auth.PublicKey))
        {
            publicKey = Convert.FromBase64String(auth.PublicKey);
            Note("pubkey from auth-session.json");
        }
        else if (!string.IsNullOrWhiteSpace(serverPublicKey))
        {
            publicKey = Convert.FromBase64String(serverPublicKey);
            Note("pubkey from /info");
        }
        else
            throw new InvalidOperationException("server public key unavailable");

        if (publicKey.Length != CryptoBox.PublicKeyBytes)
            throw new InvalidOperationException($"bad pubkey length {publicKey.Length}");

        var sharedSecret = new byte[CryptoAeadXChaCha20Poly1305Ietf.KeyBytes];
        RandomNumberGenerator.Fill(sharedSecret);
        var authHash = Convert.ToBase64String(MakeAuthHash(sharedSecret, publicKey));

        await JoinAuthServerAsync(auth, authHash, ct);
        Note("api/session/join OK");

        var sealedPayload = new byte[sharedSecret.Length + encReq.VerifyToken.Length];
        sharedSecret.CopyTo(sealedPayload.AsSpan());
        encReq.VerifyToken.CopyTo(sealedPayload.AsSpan(sharedSecret.Length));
        var sealedData = CryptoBox.Seal(sealedPayload, publicKey);

        var outMsg = _peer!.CreateMessage();
        WriteEncryptionResponse(outMsg, Guid.Parse(auth.UserId!), sealedData);
        _peer.SendMessage(outMsg, _conn, NetDeliveryMethod.ReliableOrdered);
        Note("sent MsgEncryptionResponse");

        _crypto = new NetPacketCrypto(sharedSecret, isServer: false);

        // Wait for encrypted LoginSuccess (next Data packet).
        while (!ct.IsCancellationRequested)
        {
            var done = false;
            await PumpAsync(m =>
            {
                if (m.MessageType == NetIncomingMessageType.StatusChanged)
                {
                    var status = (NetConnectionStatus)m.ReadByte();
                    var reason = m.ReadString();
                    Note($"status -> {status} ({reason})");
                    if (status == NetConnectionStatus.Disconnected)
                        throw new InvalidOperationException(
                            string.IsNullOrWhiteSpace(reason) ? "disconnected during auth" : reason);
                }
                else if (m.MessageType == NetIncomingMessageType.Data)
                {
                    if (!_crypto.TryDecrypt(m))
                        throw new InvalidOperationException("failed to decrypt login success");
                    ParseLoginSuccess(m);
                    Set(GameSessionPhase.LoginSuccess, "authenticated LoginSuccess");
                    done = true;
                    return true;
                }

                return false;
            }, ct);

            if (done)
                return;
            await Task.Delay(40, ct);
        }

        throw new TimeoutException("timeout waiting for encrypted LoginSuccess");
    }

    async Task BootstrapLobbyAsync(CancellationToken ct)
    {
        var mapstrDone = false;
        var transferDone = false;
        var cvarsSent = false;
        var cvarsReceived = false;
        var playerListReqSent = false;
        var gotPlayerList = false;
        DateTime? cvarsSentAt = null;

        Set(GameSessionPhase.StringTable, "awaiting post-login bootstrap");

        while (!ct.IsCancellationRequested && !gotPlayerList)
        {
            await PumpAsync(async msg =>
            {
                if (msg.MessageType == NetIncomingMessageType.StatusChanged)
                {
                    var status = (NetConnectionStatus)msg.ReadByte();
                    var reason = msg.ReadString();
                    Note($"status -> {status} ({reason})");
                    if (status == NetConnectionStatus.Disconnected)
                        throw new InvalidOperationException(
                            string.IsNullOrWhiteSpace(reason) ? "disconnected during lobby bootstrap" : reason);
                    return false;
                }

                if (msg.MessageType != NetIncomingMessageType.Data)
                    return false;

                if (_crypto != null && !_crypto.TryDecrypt(msg))
                    throw new InvalidOperationException("failed to decrypt post-login packet");

                if (msg.LengthBytes < 1)
                    return false;

                var id = msg.ReadByte();
                if (!_msgNames.TryGetValue(id, out var name))
                {
                    // String table itself is always id 0 before/while table arrives.
                    if (id == 0)
                        name = "MsgStringTableEntries";
                    else
                    {
                        Note($"unknown msg id={id} ({msg.LengthBytes}B) — skip");
                        return false;
                    }
                }

                switch (name)
                {
                    case "MsgStringTableEntries":
                        ApplyStringTable(msg);
                        Set(GameSessionPhase.StringTable, $"string table: {_msgIds.Count} names");
                        break;

                    case "MsgMapStrServerHandshake":
                        Set(GameSessionPhase.MapStrings, "MsgMapStrServerHandshake — request package");
                        _mapStrHash = ReadMapStrHash(msg);
                        EnsureSerializer();
                        SendNamed("MsgMapStrClientHandshake", NetDeliveryMethod.ReliableOrdered, m =>
                        {
                            m.Write(true); // NeedsStrings = true
                        });
                        _mapStrRequested = true;
                        Note($"sent MsgMapStrClientHandshake NeedsStrings=true hashLen={_mapStrHash.Length}");
                        break;

                    case "MsgMapStrStrings":
                    {
                        Set(GameSessionPhase.MapStrings, "MsgMapStrStrings");
                        var size = msg.ReadVariableInt32();
                        var package = msg.ReadBytes(size);
                        Note($"MsgMapStrStrings {package.Length:N0} B");
                        EnsureSerializer();
                        if (_serializer is not null && _mapStrHash is not null)
                        {
                            if (_serializer.TrySetMappedPackage(_mapStrHash, package, Note))
                                SerializerStatus = _serializer.Status;
                            else
                                SerializerStatus = "serializer: SetPackage failed";
                        }
                        else
                        {
                            SerializerStatus = "serializer: missing bootstrap/hash for strings";
                            Note(SerializerStatus);
                        }

                        SendNamed("MsgMapStrClientHandshake", NetDeliveryMethod.ReliableOrdered, m =>
                        {
                            m.Write(false); // complete
                        });
                        mapstrDone = true;
                        Note("sent MsgMapStrClientHandshake NeedsStrings=false (complete)");
                        break;
                    }

                    case "MsgTransferInit":
                        Set(GameSessionPhase.Transfer, "MsgTransferInit");
                        await HandleTransferInitAsync(msg, ct);
                        transferDone = true;
                        Note("transfer handshake complete");
                        break;

                    case "MsgConVars":
                        cvarsReceived = true;
                        Note($"MsgConVars received ({msg.LengthBytes}B)");
                        break;

                    case "MsgPlayerList":
                        Players = ReadPlayerList(msg);
                        gotPlayerList = true;
                        var local = Players.FirstOrDefault(p => p.UserId == UserId);
                        Note($"MsgPlayerList: {Players.Count} players; local={local?.Status}");
                        Set(GameSessionPhase.InLobby,
                            local is null
                                ? $"got player list ({Players.Count})"
                                : $"lobby status={local.Status} ({Players.Count} players)");
                        break;

                    default:
                        Note($"rx {name} id={id} — ignore");
                        break;
                }

                return false;
            }, ct);

            if (mapstrDone && transferDone && !cvarsSent)
            {
                Set(GameSessionPhase.SyncingCVars, "sending MsgConVars");
                SendNamed("MsgConVars", NetDeliveryMethod.ReliableOrdered, m =>
                {
                    m.WriteVariableUInt32(0); // tick
                    m.Write((short)0); // no replicated vars
                });
                cvarsSent = true;
                cvarsSentAt = DateTime.UtcNow;
                Note("sent empty MsgConVars");
            }

            var cvarsTimedOut = cvarsSentAt is { } at && DateTime.UtcNow - at > TimeSpan.FromSeconds(4);
            if (cvarsSent && !playerListReqSent && (cvarsReceived || cvarsTimedOut))
            {
                if (!cvarsReceived)
                    Note("MsgConVars not received in 4s — requesting player list anyway");
                Set(GameSessionPhase.RequestingPlayers, "MsgPlayerListReq");
                SendNamed("MsgPlayerListReq", NetDeliveryMethod.ReliableUnordered, _ => { });
                playerListReqSent = true;
                Note("sent MsgPlayerListReq");
            }

            await Task.Delay(40, ct);
        }

        if (!gotPlayerList)
            throw new TimeoutException(
                $"lobby bootstrap timeout (mapstr={mapstrDone} transfer={transferDone} cvarsRx={cvarsReceived})");
    }

    async Task HandleTransferInitAsync(NetIncomingMessage msg, CancellationToken ct)
    {
        var httpAvailable = msg.ReadBoolean();
        if (!httpAvailable)
        {
            SendNamed("MsgTransferAckInit", NetDeliveryMethod.ReliableOrdered, _ => { });
            Note("sent MsgTransferAckInit (lidgren)");
            return;
        }

        msg.SkipPadBits();
        var endpointUrl = msg.ReadString();
        var key = msg.ReadBytes(TransferKeyBytes);
        Note($"transfer WS → {endpointUrl}");

        _transferWs = new ClientWebSocket();
        _transferWs.Options.SetRequestHeader(TransferKeyHeader, Convert.ToBase64String(key));
        _transferWs.Options.SetRequestHeader(TransferUserHeader, UserId!.Value.ToString());
        await _transferWs.ConnectAsync(new Uri(endpointUrl), ct);
        Note($"transfer WS connected ({_transferWs.State})");
        // Keep socket open; server completes handshake on AcceptWebSocket.
        _ = Task.Run(() => DrainTransferWsAsync(ct), CancellationToken.None);
    }

    async Task DrainTransferWsAsync(CancellationToken ct)
    {
        if (_transferWs is null) return;
        var buf = new byte[4096];
        try
        {
            while (!ct.IsCancellationRequested && _transferWs.State == WebSocketState.Open)
            {
                var result = await _transferWs.ReceiveAsync(buf, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
            }
        }
        catch
        {
            // hold-open best effort
        }
    }

    async Task DrainKeepAliveAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await PumpAsync(msg =>
            {
                if (msg.MessageType == NetIncomingMessageType.StatusChanged)
                {
                    var status = (NetConnectionStatus)msg.ReadByte();
                    var reason = msg.ReadString();
                    Note($"status -> {status} ({reason})");
                    if (status == NetConnectionStatus.Disconnected)
                        throw new InvalidOperationException(
                            string.IsNullOrWhiteSpace(reason) ? "disconnected in lobby" : reason);
                }
                else if (msg.MessageType == NetIncomingMessageType.Data)
                {
                    HandlePostLobbyData(msg);
                }

                return false;
            }, ct);
            await Task.Delay(50, ct);
        }
    }

    void HandlePostLobbyData(NetIncomingMessage msg)
    {
        if (_crypto != null && !_crypto.TryDecrypt(msg))
            return;
        if (msg.LengthBytes < 1)
            return;

        var id = msg.ReadByte();
        if (!_msgNames.TryGetValue(id, out var name))
        {
            Note($"post-lobby unknown id={id}");
            return;
        }

        switch (name)
        {
            case "MsgPlayerList":
                Players = ReadPlayerList(msg);
                var local = Players.FirstOrDefault(p => p.UserId == UserId);
                if (local is not null)
                {
                    LocalStatus = local.Status;
                    if (local.Status == SessionStatus.InGame && IsObserving)
                        Set(GameSessionPhase.Observing, $"ghost InGame — states={StatesReceived}");
                    else if (local.Status == SessionStatus.Connected && Phase == GameSessionPhase.Observing)
                        Detail = "observe rejected / still in lobby (round may not have started)";
                }

                Note($"player list update: {Players.Count} local={LocalStatus}");
                break;

            case "MsgState":
                HandleMsgState(msg);
                break;

            case "MsgStateLeavePvs":
                Note($"MsgStateLeavePvs ({msg.LengthBytes}B)");
                break;

            case "MsgEntity":
                // Content ticker events — full deserialize needs content serializer.
                Note($"MsgEntity ({msg.LengthBytes}B)");
                break;

            case "MsgConCmdAck":
                // FormattedMessage needs serializer; skim remaining as opaque.
                LastCommandAck = $"ack {msg.LengthBytes}B";
                Note($"MsgConCmdAck ({msg.LengthBytes}B)");
                break;

            case "MsgStringTableEntries":
                ApplyStringTable(msg);
                break;

                    default:
                        Note($"rx {name} id={id} ({msg.LengthBytes}B)");
                        break;
        }
    }

    void HandleMsgState(NetIncomingMessage msg)
    {
        try
        {
            var uncompressed = msg.ReadVariableInt32();
            var compressed = msg.ReadVariableInt32();
            LastStateBytes = uncompressed;
            StatesReceived++;

            byte[] payload;
            if (compressed > 0)
            {
                var zstdBytes = msg.ReadBytes(compressed);
                try
                {
                    using var input = new MemoryStream(zstdBytes);
                    using var z = new Robust.Shared.Utility.ZStdDecompressStream(input);
                    payload = new byte[uncompressed];
                    var read = 0;
                    while (read < uncompressed)
                    {
                        var n = z.Read(payload, read, uncompressed - read);
                        if (n <= 0) break;
                        read += n;
                    }

                    if (read != uncompressed)
                        Note($"MsgState zstd short read {read}/{uncompressed}");
                }
                catch (Exception zex)
                {
                    Note($"MsgState zstd FAIL: {zex.Message}");
                    payload = zstdBytes;
                }
            }
            else
            {
                payload = uncompressed > 0 ? msg.ReadBytes(uncompressed) : Array.Empty<byte>();
            }

            if (UserId is { } uid && payload.Length >= 16)
                TryScanControlledEntity(payload, uid);

            EnsureSerializer();
            if (_serializer is { HasMappedStrings: true } boot && UserId is { } localId)
            {
                if (GameStateDecoder.TryDecode(boot.Serializer, payload, localId, out var eye, out var tick, out var err))
                {
                    LastEye = eye;
                    LastEyeHint = eye!.Detail;
                    // Keep manual pan as offset on top of server eye.
                    if (!IsObserving || (Math.Abs(CamX) < 0.01f && Math.Abs(CamY) < 0.01f))
                    {
                        CamX = eye.LocalPosition.X * 32f;
                        CamY = eye.LocalPosition.Y * 32f;
                    }

                    try
                    {
                        SendNamed("MsgStateAck", NetDeliveryMethod.Unreliable, m => m.Write(tick.Value));
                    }
                    catch (Exception ackEx)
                    {
                        Note($"MsgStateAck fail: {ackEx.Message}");
                    }
                }
                else
                {
                    LastEye = eye;
                    LastEyeHint = err;
                    if (StatesReceived <= 8)
                        Note($"GameState decode: {err}");
                    if (eye is not null)
                    {
                        try
                        {
                            SendNamed("MsgStateAck", NetDeliveryMethod.Unreliable, m => m.Write(eye.ToSequence.Value));
                        }
                        catch { /* ignore */ }
                    }
                }
            }

            if (StatesReceived <= 5 || StatesReceived % 20 == 0)
                Note($"MsgState #{StatesReceived} raw={uncompressed}B z={compressed} {LastEyeHint}");

            if (IsObserving)
                Detail = $"observing · MsgState x{StatesReceived} ({uncompressed}B) {LastEyeHint}";
        }
        catch (Exception ex)
        {
            Note($"MsgState parse: {ex.Message}");
        }
    }

    void EnsureSerializer()
    {
        if (_serializer is not null)
            return;
        
        _serializer = SerializerBootstrap.TryCreate(AssembliesDirectory, Note);
        SerializerStatus = _serializer?.Status ?? "serializer: unavailable (no Assemblies?)";
    }

    void TryScanControlledEntity(byte[] payload, Guid userId)
    {
        var idBytes = userId.ToByteArray();
        for (var i = 0; i <= payload.Length - 16; i++)
        {
            var match = true;
            for (var b = 0; b < 16; b++)
            {
                if (payload[i + b] != idBytes[b])
                {
                    match = false;
                    break;
                }
            }

            if (!match) continue;
            if (string.IsNullOrEmpty(LastEyeHint))
                LastEyeHint = $"uid@{i}";
            return;
        }
    }

    void ApplyStringTable(NetIncomingMessage msg)
    {
        var count = msg.ReadUInt32();
        for (var i = 0; i < count; i++)
        {
            var id = msg.ReadVariableInt32();
            var name = msg.ReadString();
            if (id is < 0 or > byte.MaxValue)
                continue;
            _msgNames[id] = name;
            _msgIds[name] = id;
        }

        // Bootstrap id 0 is always MsgStringTableEntries.
        _msgNames.TryAdd(0, "MsgStringTableEntries");
        _msgIds.TryAdd("MsgStringTableEntries", 0);
        Note($"string table +{count} (known={_msgIds.Count})");
    }

    static byte[] ReadMapStrHash(NetIncomingMessage msg)
    {
        var len = msg.ReadVariableInt32();
        if (len is < 0 or > 64)
            throw new InvalidOperationException($"bad mapstr hash len {len}");
        return msg.ReadBytes(len);
    }

    static List<LobbyPlayer> ReadPlayerList(NetIncomingMessage msg)
    {
        var n = msg.ReadInt32();
        var list = new List<LobbyPlayer>(n);
        for (var i = 0; i < n; i++)
        {
            var id = msg.ReadGuid();
            var name = msg.ReadString();
            var status = (SessionStatus)msg.ReadByte();
            list.Add(new LobbyPlayer(id, name, status));
        }

        return list;
    }

    void SendNamed(string msgName, NetDeliveryMethod delivery, Action<NetOutgoingMessage> writeBody)
    {
        if (_peer is null || _conn is null)
            throw new InvalidOperationException("not connected");
        if (!_msgIds.TryGetValue(msgName, out var id))
            throw new InvalidOperationException($"message '{msgName}' not in string table yet");

        var packet = _peer.CreateMessage();
        packet.Write((byte)id);
        writeBody(packet);
        _crypto?.Encrypt(packet);
        _peer.SendMessage(packet, _conn, delivery);
    }

    void SendLoginStart(AuthSessionConfig auth, bool needPubKey)
    {
        var msg = _peer!.CreateMessage();
        msg.Write(auth.UserName ?? "AndroidGuest");
        msg.Write(true); // CanAuth
        msg.Write(needPubKey);
        msg.Write(true); // Encrypt
        _peer.SendMessage(msg, _conn, NetDeliveryMethod.ReliableOrdered);
    }

    void ParseLoginSuccess(NetIncomingMessage msg)
    {
        UserName = msg.ReadString();
        UserId = msg.ReadGuid();
        _ = msg.ReadString(); // patron
        LoginType = (LoginType)msg.ReadByte();
        Note($"login success: {UserName} ({UserId}) type={LoginType}");
    }

    static (byte[] VerifyToken, byte[] PublicKey, bool WantHwid) ReadEncryptionRequest(NetIncomingMessage msg)
    {
        var tokenLength = msg.ReadVariableInt32();
        var verifyToken = msg.ReadBytes(tokenLength);
        var keyLength = msg.ReadVariableInt32();
        var publicKey = msg.ReadBytes(keyLength);
        var wantHwid = msg.ReadBoolean();
        return (verifyToken, publicKey, wantHwid);
    }

    static void WriteEncryptionResponse(NetOutgoingMessage msg, Guid userId, byte[] sealedData)
    {
        msg.Write(userId);
        msg.WriteVariableInt32(sealedData.Length);
        msg.Write(sealedData);
        msg.WriteVariableInt32(0);
        msg.Write(Array.Empty<byte>());
    }

    static byte[] MakeAuthHash(byte[] sharedSecret, byte[] publicKey)
    {
        var incHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        incHash.AppendData(sharedSecret);
        incHash.AppendData(publicKey);
        return incHash.GetHashAndReset();
    }

    static async Task JoinAuthServerAsync(AuthSessionConfig auth, string authHash, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var authServer = string.IsNullOrWhiteSpace(auth.AuthServer)
            ? Ss14AuthClient.DefaultAuthServer
            : auth.AuthServer!;
        if (!authServer.EndsWith('/'))
            authServer += "/";

        using var req = new HttpRequestMessage(HttpMethod.Post, authServer + "api/session/join");
        req.Headers.Authorization = new AuthenticationHeaderValue("SS14Auth", auth.Token);
        req.Content = JsonContent.Create(
            new JoinRequest(authHash, null),
            options: new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var resp = await http.SendAsync(req, ct);
        if (resp.IsSuccessStatusCode)
            return;

        var body = "";
        try { body = await resp.Content.ReadAsStringAsync(ct); } catch { /* ignore */ }
        if (body.Length > 220)
            body = body[..220] + "…";
        throw new HttpRequestException(
            $"api/session/join HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}" +
            (string.IsNullOrWhiteSpace(body) ? "" : $": {body}"));
    }

    async Task PumpAsync(Func<NetIncomingMessage, Task<bool>> handler, CancellationToken ct)
    {
        if (_peer is null) return;
        NetIncomingMessage? msg;
        while ((msg = _peer.ReadMessage()) != null)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await handler(msg))
                    return;
            }
            finally
            {
                _peer.Recycle(msg);
            }
        }
    }

    async Task PumpAsync(Func<NetIncomingMessage, bool> handler, CancellationToken ct)
        => await PumpAsync(m => Task.FromResult(handler(m)), ct);

    void Set(GameSessionPhase phase, string detail)
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

    IReadOnlyList<string> SnapshotLog(int max = 16)
    {
        lock (_gate)
        {
            if (_log.Count <= max) return _log.ToArray();
            return _log.Skip(_log.Count - max).ToArray();
        }
    }

    GameSessionResult Result(TimeSpan elapsed) =>
        new(Phase, Detail, UserName, UserId, LoginType, Players, elapsed);

    void DisposeConnection()
    {
        try { _keepAliveCts?.Cancel(); } catch { /* ignore */ }
        _keepAliveCts?.Dispose();
        _keepAliveCts = null;
        try { _transferWs?.Dispose(); } catch { /* ignore */ }
        _transferWs = null;
        try { _peer?.Shutdown("session end"); } catch { /* ignore */ }
        _peer = null;
        _conn = null;
        _crypto = null;
        _msgNames.Clear();
        _msgIds.Clear();
        try { _serializer?.Dispose(); } catch { /* ignore */ }
        _serializer = null;
        _mapStrHash = null;
        _mapStrRequested = false;
        IsReady = false;
        IsObserving = false;
        StatesReceived = 0;
        LastStateBytes = 0;
        LastEye = null;
        LastEyeHint = "";
        SerializerStatus = "serializer: not started";
        CamX = 0;
        CamY = 0;
        LocalStatus = SessionStatus.Disconnected;
    }

    public void Dispose() => Disconnect("dispose");

    sealed record JoinRequest(string Hash, string? Hwid);
}
