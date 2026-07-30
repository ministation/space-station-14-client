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
    readonly PortTransferReceiver _transfer = new();
    string _joinHost = "";
    int _ignoredRx;
    int _transferDataRx;
    bool _sawTransferTraffic;
    DateTime? _lastTransferDataAt;
    bool _transferHandshakeDone;

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
    public WorldSnapshot? LastWorld { get; private set; }
    public SessionStatus LocalStatus { get; private set; } = SessionStatus.Connecting;
    public float CamX { get; private set; }
    public float CamY { get; private set; }
    float _panOffX;
    float _panOffY;
    public string? AssembliesDirectory { get; set; }
    public string? ContentFilesRoot { get; set; }
    public string? ContentSearchRoot { get; set; }
    public string? StringsCacheDirectory { get; set; }
    public string SerializerStatus { get; private set; } = "serializer: not started";
    public bool HasMappedStrings => _serializer?.HasMappedStrings == true;

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

    public void EnsureSerializerPublic() => EnsureSerializer(forceRediscover: true);

    public bool SetReady(bool ready)
    {
        IsReady = ready;
        return SendConsole(ready ? "toggleready True" : "toggleready False");
    }

    public bool Observe()
    {
        EnsureSerializer(forceRediscover: true);
        if (_serializer is null)
        {
            Note("observe: serializer still unavailable — check Assemblies download");
            Detail = "observe blocked: no Assemblies for GameState";
            // Still send observe so server attaches ghost; viewport may catch up later.
        }
        else if (_serializer is { HasMappedStrings: false })
        {
            if (_mapStrHash is { Length: > 0 })
            {
                if (!_serializer.TryLoadCachedStrings(_mapStrHash, Note))
                    RequestMappedStrings();
            }
            else
                Note("observe: waiting MsgMapStrServerHandshake for string hash");
        }

        if (!SendConsole("observe"))
            return false;
        IsObserving = true;
        Set(GameSessionPhase.Observing,
            HasMappedStrings
                ? "sent observe — awaiting MsgState / ghost"
                : "sent observe — waiting serializer/strings for decode");
        return true;
    }

    public void RequestMappedStrings()
    {
        if (_mapStrHash is null || _mapStrHash.Length == 0)
        {
            Note("mapstr request skipped — no server hash yet");
            return;
        }

        if (_serializer?.HasMappedStrings == true)
            return;

        try
        {
            SendNamed("MsgMapStrClientHandshake", NetDeliveryMethod.ReliableOrdered, m =>
            {
                m.Write(true); // NeedsStrings
            });
            _mapStrRequested = true;
            Note("mapstr: requested NeedsStrings=true (observe/decode)");
        }
        catch (Exception ex)
        {
            Note($"mapstr request fail: {ex.Message}");
        }
    }

    public void PanCamera(float dx, float dy)
    {
        _panOffX += dx;
        _panOffY += dy;
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
                    _joinHost = endpoint.Host;

                    using var loginCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    loginCts.CancelAfter(TimeSpan.FromSeconds(40));
                    await ConnectAndLoginAsync(endpoint, ip, authMode, serverPublicKey, auth, loginCts.Token);

                    using var bootCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    bootCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(55, timeout.TotalSeconds)));
                    await BootstrapLobbyAsync(bootCts.Token);

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
                catch (OperationCanceledException ex)
                {
                    last = new TimeoutException(
                        $"lobby timeout (mapstr/transfer/playerlist) — {_ignoredRx} ignored msgs, transferData={_transferDataRx}",
                        ex);
                    Note($"fail {ip}: timeout — {last.Message}");
                    Set(GameSessionPhase.Failed, last.Message);
                    DisposeConnection();
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
                            string.IsNullOrWhiteSpace(reason)
                                ? "disconnected before login"
                                : ConnectFailureFormatter.ExtractReason(reason));
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

        byte[] legacyHwid = [];
        string? modernHwidB64 = null;
        // MiniStation denies with "отказался отправлять HWID" when join has hwid=null.
        var sendHwid = encReq.WantHwid && auth.AllowHwid;
        if (sendHwid)
        {
            legacyHwid = ClientHwid.GetLegacy();
            modernHwidB64 = ClientHwid.GetModernBase64();
            Note($"hwid: want={encReq.WantHwid} allow={auth.AllowHwid} modernB64Len={modernHwidB64?.Length ?? 0} legacyLen={legacyHwid.Length}");
        }
        else
        {
            Note($"hwid: skipped want={encReq.WantHwid} allow={auth.AllowHwid}");
        }

        await JoinAuthServerAsync(auth, authHash, modernHwidB64, ct);
        Note("api/session/join OK");

        var sealedPayload = new byte[sharedSecret.Length + encReq.VerifyToken.Length];
        sharedSecret.CopyTo(sealedPayload.AsSpan());
        encReq.VerifyToken.CopyTo(sealedPayload.AsSpan(sharedSecret.Length));
        var sealedData = CryptoBox.Seal(sealedPayload, publicKey);

        var outMsg = _peer!.CreateMessage();
        WriteEncryptionResponse(outMsg, Guid.Parse(auth.UserId!), sealedData, legacyHwid);
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
                            string.IsNullOrWhiteSpace(reason)
                                ? "disconnected during auth"
                                : ConnectFailureFormatter.ExtractReason(reason));
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
        var stringTableReady = false;
        DateTime? cvarsSentAt = null;
        var bootStarted = DateTime.UtcNow;
        _ignoredRx = 0;
        _transferDataRx = 0;
        _sawTransferTraffic = false;
        _lastTransferDataAt = null;
        _transferHandshakeDone = false;
        _transfer.Reset();

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
                            string.IsNullOrWhiteSpace(reason)
                                ? "disconnected during lobby bootstrap"
                                : ConnectFailureFormatter.ExtractReason(reason));
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
                        NoteIgnore($"unknown msg id={id} ({msg.LengthBytes}B)");
                        return false;
                    }
                }

                switch (name)
                {
                    case "MsgStringTableEntries":
                        ApplyStringTable(msg);
                        stringTableReady = true;
                        Set(GameSessionPhase.StringTable, $"string table: {_msgIds.Count} names");
                        break;

                    case "MsgMapStrServerHandshake":
                        Set(GameSessionPhase.MapStrings, "MsgMapStrServerHandshake");
                        _mapStrHash = ReadMapStrHash(msg);
                        EnsureSerializer();
                        if (_serializer is not null
                            && _serializer.TryLoadCachedStrings(_mapStrHash, Note))
                        {
                            SerializerStatus = _serializer.Status;
                            SendNamed("MsgMapStrClientHandshake", NetDeliveryMethod.ReliableOrdered, m =>
                            {
                                m.Write(false);
                            });
                            mapstrDone = true;
                            Note("mapstr: cache HIT — NeedsStrings=false");
                            break;
                        }

                        // Request package for GameState decode; force-complete later if slow.
                        SendNamed("MsgMapStrClientHandshake", NetDeliveryMethod.ReliableOrdered, m =>
                        {
                            m.Write(true); // NeedsStrings = true
                        });
                        _mapStrRequested = true;
                        Note($"mapstr: NeedsStrings=true hashLen={_mapStrHash.Length}");
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
                            {
                                SerializerStatus = _serializer.Status;
                                _serializer.TrySaveCachedStrings(_mapStrHash, package, Note);
                            }
                            else
                                SerializerStatus = "serializer: SetPackage failed";
                        }

                        SendNamed("MsgMapStrClientHandshake", NetDeliveryMethod.ReliableOrdered, m =>
                        {
                            m.Write(false);
                        });
                        mapstrDone = true;
                        Note("sent MsgMapStrClientHandshake NeedsStrings=false (after package)");
                        break;
                    }

                    case "MsgTransferInit":
                        Set(GameSessionPhase.Transfer, "MsgTransferInit");
                        await HandleTransferInitAsync(msg, ct);
                        _transferHandshakeDone = true;
                        Note("transfer handshake complete");
                        break;

                    case "MsgTransferData":
                        _transferDataRx++;
                        _sawTransferTraffic = true;
                        _lastTransferDataAt = DateTime.UtcNow;
                        Set(GameSessionPhase.Transfer, $"MsgTransferData #{_transferDataRx}");
                        _transfer.ReadMsgTransferData(msg, Note);
                        if (TrySendNetworkResourceAck())
                            transferDone = true;
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

                    case "MsgEntity":
                        NoteIgnore("MsgEntity");
                        break;

                    default:
                        NoteIgnore($"{name} id={id}");
                        break;
                }

                return false;
            }, ct);

            var bootElapsed = DateTime.UtcNow - bootStarted;
            // Don't stall forever on mapstr — force lobby path after a few seconds.
            if (!mapstrDone && stringTableReady && bootElapsed > TimeSpan.FromSeconds(12))
            {
                try
                {
                    SendNamed("MsgMapStrClientHandshake", NetDeliveryMethod.ReliableOrdered, m =>
                    {
                        m.Write(false);
                    });
                    Note("mapstr: force NeedsStrings=false after 12s wait");
                }
                catch (Exception ex)
                {
                    Note($"mapstr force-complete fail: {ex.Message}");
                }

                mapstrDone = true;
            }

            // Resource ACK unblocks MsgPlayerList on the server.
            if (!transferDone && TrySendNetworkResourceAck())
                transferDone = true;

            // Transfer traffic went quiet → force-finish + ACK (Finish frame may have been missed).
            if (!transferDone && _sawTransferTraffic && _lastTransferDataAt is { } lastXfer
                && DateTime.UtcNow - lastXfer > TimeSpan.FromSeconds(2))
            {
                _transfer.ForceFinishForAck(Note);
                if (TrySendNetworkResourceAck())
                    transferDone = true;
            }

            // No upload payload expected: handshake done (or never came) and no MsgTransferData.
            if (!transferDone && stringTableReady && !_sawTransferTraffic
                && bootElapsed > TimeSpan.FromSeconds(4)
                && (_transferHandshakeDone || bootElapsed > TimeSpan.FromSeconds(10)))
            {
                transferDone = true;
                Note(_transferHandshakeDone
                    ? "transfer: done (handshake, no download traffic)"
                    : "transfer: done (no MsgTransferInit / no download)");
            }

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

            // Re-request player list once if silent (resources may have become ready late).
            if (playerListReqSent && !gotPlayerList && cvarsSentAt is { } sent
                && DateTime.UtcNow - sent > TimeSpan.FromSeconds(8))
            {
                cvarsSentAt = DateTime.UtcNow;
                // Re-send resource ACK then player list — covers race where list req arrived first.
                TrySendNetworkResourceAck(forceResend: true);
                SendNamed("MsgPlayerListReq", NetDeliveryMethod.ReliableUnordered, _ => { });
                Note("re-sent NetworkResourceAck + MsgPlayerListReq");
            }

            await Task.Delay(40, ct);
        }

        if (!gotPlayerList)
            throw new TimeoutException(
                $"lobby bootstrap timeout (mapstr={mapstrDone} transfer={transferDone} ack={_transfer.AckSent} cvarsRx={cvarsReceived} ignore={_ignoredRx} xfer={_transferDataRx})");
    }

    bool TrySendNetworkResourceAck(bool forceResend = false)
    {
        if (!_transfer.DownloadFinished && !forceResend)
            return false;
        if (_transfer.AckSent && !forceResend)
            return true;
        if (!_msgIds.ContainsKey("NetworkResourceAckMessage"))
        {
            if (_transfer.DownloadFinished)
                Note("transfer: NetworkResourceAckMessage not in string table yet");
            return false;
        }

        var key = _transfer.DownloadFinished ? _transfer.LastAckKey : PortTransferReceiver.AckInitial;
        try
        {
            SendNamed("NetworkResourceAckMessage", NetDeliveryMethod.ReliableOrdered, m => m.Write(key));
            _transfer.MarkAckSent();
            Note($"sent NetworkResourceAckMessage key={key}");
            return true;
        }
        catch (Exception ex)
        {
            Note($"NetworkResourceAckMessage fail: {ex.Message}");
            return false;
        }
    }

    void NoteIgnore(string label)
    {
        _ignoredRx++;
        if (_ignoredRx <= 3 || _ignoredRx % 100 == 0)
            Note($"rx {label} — ignore (#{_ignoredRx})");
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
        endpointUrl = RewriteTransferEndpoint(endpointUrl);
        Note($"transfer WS → {endpointUrl}");

        try
        {
            using var wsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wsCts.CancelAfter(TimeSpan.FromSeconds(8));

            _transferWs = new ClientWebSocket();
            _transferWs.Options.SetRequestHeader(TransferKeyHeader, Convert.ToBase64String(key));
            _transferWs.Options.SetRequestHeader(TransferUserHeader, UserId!.Value.ToString());
            await _transferWs.ConnectAsync(new Uri(endpointUrl), wsCts.Token);
            Note($"transfer WS connected ({_transferWs.State})");
            _ = Task.Run(() => DrainTransferWsAsync(CancellationToken.None), CancellationToken.None);
        }
        catch (Exception ex)
        {
            Note($"transfer WS fail: {ex.GetType().Name}: {ex.Message} — continuing lobby without WS");
            try { _transferWs?.Dispose(); } catch { /* ignore */ }
            _transferWs = null;
            // Best-effort: some forks accept lidgren ack after WS fail.
            try
            {
                SendNamed("MsgTransferAckInit", NetDeliveryMethod.ReliableOrdered, _ => { });
                Note("sent MsgTransferAckInit (fallback after WS fail)");
            }
            catch (Exception ackEx)
            {
                Note($"transfer ack fallback fail: {ackEx.Message}");
            }
        }
    }

    string RewriteTransferEndpoint(string endpointUrl)
    {
        if (!Uri.TryCreate(endpointUrl, UriKind.Absolute, out var uri))
            return endpointUrl;

        var host = uri.Host;
        var rewrite = host is "localhost" or "127.0.0.1" or "::1" or "0.0.0.0";
        if (!rewrite && IPAddress.TryParse(host, out var ip) && HostResolver.IsPrivate(ip))
            rewrite = true;

        if (!rewrite || string.IsNullOrWhiteSpace(_joinHost))
            return endpointUrl;

        var rebuilt = new UriBuilder(uri) { Host = _joinHost }.Uri.ToString();
        Note($"transfer WS host rewrite {host} → {_joinHost}");
        return rebuilt;
    }

    async Task DrainTransferWsAsync(CancellationToken ct)
    {
        if (_transferWs is null) return;
        var buf = new byte[16384 + 256];
        var pending = new MemoryStream();
        try
        {
            while (!ct.IsCancellationRequested && _transferWs.State == WebSocketState.Open)
            {
                pending.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await _transferWs.ReceiveAsync(buf, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return;
                    if (result.Count > 0)
                        pending.Write(buf, 0, result.Count);
                } while (!result.EndOfMessage);

                _sawTransferTraffic = true;
                _lastTransferDataAt = DateTime.UtcNow;
                _transferDataRx++;
                var payload = pending.ToArray();
                _transfer.OnWebSocketMessage(payload, Note);
                TrySendNetworkResourceAck();
            }
        }
        catch (Exception ex)
        {
            Note($"transfer WS drain end: {ex.GetType().Name}: {ex.Message}");
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
                            string.IsNullOrWhiteSpace(reason)
                                ? "disconnected in lobby"
                                : ConnectFailureFormatter.ExtractReason(reason));
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

            case "MsgMapStrStrings":
            {
                var size = msg.ReadVariableInt32();
                var package = msg.ReadBytes(size);
                Note($"post-lobby MsgMapStrStrings {package.Length:N0} B");
                EnsureSerializer();
                if (_serializer is not null && _mapStrHash is not null)
                {
                    if (_serializer.TrySetMappedPackage(_mapStrHash, package, Note))
                    {
                        SerializerStatus = _serializer.Status;
                        _serializer.TrySaveCachedStrings(_mapStrHash, package, Note);
                    }
                }

                try
                {
                    SendNamed("MsgMapStrClientHandshake", NetDeliveryMethod.ReliableOrdered, m => m.Write(false));
                }
                catch { /* ignore */ }
                break;
            }

            case "MsgMapStrServerHandshake":
                _mapStrHash = ReadMapStrHash(msg);
                EnsureSerializer();
                if (_serializer is not null && !_serializer.HasMappedStrings)
                {
                    if (!_serializer.TryLoadCachedStrings(_mapStrHash, Note))
                        RequestMappedStrings();
                    else
                    {
                        SerializerStatus = _serializer.Status;
                        SendNamed("MsgMapStrClientHandshake", NetDeliveryMethod.ReliableOrdered, m => m.Write(false));
                    }
                }
                break;

            case "MsgTransferData":
                _transferDataRx++;
                _sawTransferTraffic = true;
                _lastTransferDataAt = DateTime.UtcNow;
                _transfer.ReadMsgTransferData(msg, Note);
                TrySendNetworkResourceAck();
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

            EnsureSerializer(forceRediscover: StatesReceived % 40 == 1);
            if (_serializer is { HasMappedStrings: true } boot && UserId is { } localId)
            {
                if (GameStateDecoder.TryDecodeWorld(
                        boot.Serializer, payload, localId,
                        out var eye, out var world, out var tick, out var err))
                {
                    LastEye = eye;
                    LastWorld = world;
                    LastEyeHint = eye!.Detail;
                    // Follow controlled entity; touch pan is an extra offset.
                    CamX = eye.LocalPosition.X * 32f + eye.EyeOffset.X * 32f + _panOffX;
                    CamY = eye.LocalPosition.Y * 32f + eye.EyeOffset.Y * 32f + _panOffY;

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
                    LastWorld = world;
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
            else if (IsObserving && StatesReceived <= 3)
            {
                Note($"GameState decode deferred — HasMappedStrings={_serializer?.HasMappedStrings == true}");
                if (_mapStrHash is { Length: > 0 })
                    RequestMappedStrings();
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

    void EnsureSerializer(bool forceRediscover = false)
    {
        var resolved = ContentAssemblyLocator.Resolve(
            AssembliesDirectory,
            ContentFilesRoot is null ? null : Path.Combine(ContentFilesRoot, "Assemblies"),
            ContentFilesRoot,
            ContentSearchRoot);
        if (resolved != null)
            AssembliesDirectory = resolved;

        if (_serializer is not null)
        {
            if (!string.IsNullOrWhiteSpace(StringsCacheDirectory))
                _serializer.StringsCacheDirectory = StringsCacheDirectory;
            SerializerStatus = _serializer.Status;
            return;
        }

        if (!ContentAssemblyLocator.HasDlls(AssembliesDirectory))
        {
            SerializerStatus = $"serializer: waiting Assemblies ({AssembliesDirectory ?? "null"})";
            if (forceRediscover || StatesReceived % 30 == 1)
                Note(SerializerStatus);
            return;
        }

        _serializer = SerializerBootstrap.TryCreate(AssembliesDirectory, Note);
        if (_serializer is not null && !string.IsNullOrWhiteSpace(StringsCacheDirectory))
            _serializer.StringsCacheDirectory = StringsCacheDirectory;
        SerializerStatus = _serializer?.Status
                           ?? $"serializer: bootstrap failed — {SerializerBootstrap.LastError ?? "unknown"}";
        if (_serializer is not null
            && _mapStrHash is { Length: > 0 }
            && !_serializer.HasMappedStrings)
        {
            if (!_serializer.TryLoadCachedStrings(_mapStrHash, Note))
                RequestMappedStrings();
        }
    }

    /// <summary>Call when content download finishes so Assemblies become visible.</summary>
    public void NotifyContentReady(string? filesRoot)
    {
        if (!string.IsNullOrWhiteSpace(filesRoot))
        {
            ContentFilesRoot = filesRoot;
            AssembliesDirectory = Path.Combine(filesRoot, "Assemblies");
        }

        EnsureSerializer(forceRediscover: true);
        Note($"content ready → serializer={SerializerStatus}");
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

    static void WriteEncryptionResponse(NetOutgoingMessage msg, Guid userId, byte[] sealedData, byte[] legacyHwid)
    {
        msg.Write(userId);
        msg.WriteVariableInt32(sealedData.Length);
        msg.Write(sealedData);
        msg.WriteVariableInt32(legacyHwid.Length);
        msg.Write(legacyHwid);
    }

    static byte[] MakeAuthHash(byte[] sharedSecret, byte[] publicKey)
    {
        var incHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        incHash.AppendData(sharedSecret);
        incHash.AppendData(publicKey);
        return incHash.GetHashAndReset();
    }

    static async Task JoinAuthServerAsync(AuthSessionConfig auth, string authHash, string? modernHwidBase64, CancellationToken ct)
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
            new JoinRequest(authHash, modernHwidBase64),
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
        _transfer.Reset();
        _sawTransferTraffic = false;
        _transferDataRx = 0;
        _lastTransferDataAt = null;
        _transferHandshakeDone = false;
        // Keep AssembliesDirectory / ContentFilesRoot across reconnects in same process.
        IsReady = false;
        IsObserving = false;
        StatesReceived = 0;
        LastStateBytes = 0;
        LastEye = null;
        LastWorld = null;
        LastEyeHint = "";
        SerializerStatus = "serializer: not started";
        CamX = 0;
        CamY = 0;
        _panOffX = 0;
        _panOffY = 0;
        LocalStatus = SessionStatus.Disconnected;
    }

    public void Dispose() => Disconnect("dispose");

    sealed record JoinRequest(string Hash, string? Hwid);
}
