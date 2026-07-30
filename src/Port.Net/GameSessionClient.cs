using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lidgren.Network;
using Robust.Shared.Enums;
using Robust.Shared.GameObjects;
using Robust.Shared.Input;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Network;
using Robust.Shared.Timing;
using SpaceWizards.Sodium;
using System.Numerics;

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

public sealed record ChatLine(string Channel, string Text, DateTime Utc, int Argb = unchecked((int)0xFFD3D3D3));

public sealed record SpeechBubbleDraw(
    float X,
    float Y,
    string Text,
    int Argb,
    float Alpha,
    float StackOffset);

public sealed record GhostWarpEntry(
    NetEntity Entity,
    string DisplayName,
    bool IsWarpPoint,
    /// <summary>place | player | antag — Mini GhostTargetWindow categories.</summary>
    string Category = "place",
    string? Subtitle = null);

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
    public bool IsObserving { get; set; }
    /// <summary>Fired on UI thread-unsafe path after a successful cycle warp (incl. auto after load).</summary>
    public event Action<string>? WarpCycled;
    public event Action? GhostUiChanged;

    public int GhostRoleCount { get; private set; }
    public bool CanReturnToBody { get; private set; } = true;
    public bool CanTakeGhostRoles { get; private set; } = true;

    /// <summary>Ghost eye is forced fullbright/no-FoV on Android for now.</summary>
    public bool DrawFov { get; private set; } = false;
    public bool DrawLighting { get; private set; } = false;
    public bool ShowOtherGhosts { get; private set; } = true;

    int _lightingMode = 1; // 0 normal, 1 fullbright

    public string LightingModeLabel => _lightingMode switch
    {
        1 => "Ярко",
        _ => "Свет",
    };
    public int StatesReceived { get; private set; }
    public int LastStateBytes { get; private set; }
    public string LastCommandAck { get; private set; } = "";
    public string LastEyeHint { get; private set; } = "";
    public EyeSnapshot? LastEye { get; private set; }
    public WorldSnapshot? LastWorld { get; private set; }
    public SessionStatus LocalStatus { get; private set; } = SessionStatus.Connecting;
    public float CamX { get; private set; }
    public float CamY { get; private set; }
    /// <summary>
    /// Rendered camera rotation — follows server eye/grid (PC Clyde eye alignment).
    /// Stick input is transformed by this so screen-up stays MoveUp on a rotated grid.
    /// </summary>
    public float CamRotation { get; private set; }
    /// <summary>Server eye / grid world rotation (radians).</summary>
    public float EyeWorldRotation { get; private set; }
    public float Zoom { get; private set; } = 1f;
    float _panOffX;
    float _panOffY;
    float _flightX;
    float _flightY;
    float _flightSpeed = 980f;
    BoundKeyMap? _keyMap;
    uint _inputSequence;
    bool _keyUp, _keyDown, _keyLeft, _keyRight;
    public string? AssembliesDirectory { get; set; }
    public string? ContentFilesRoot { get; set; }
    public string? ContentSearchRoot { get; set; }
    public string? StringsCacheDirectory { get; set; }
    public string SerializerStatus { get; private set; } = "serializer: not started";
    public bool HasMappedStrings => _serializer?.HasMappedStrings == true;
    public int WorldXformCount => _worldCache.XformCount;
    public int WorldSpriteCount => _worldCache.SpriteCount;
    public int PrototypeSpriteCount => _protoSprites.Count;
    public int WorldTileChunks => _worldCache.TileChunkCount;

    readonly Port.Content.PrototypeSpriteIndex _protoSprites = new();
    readonly Port.Content.TilePrototypeIndex _tileProtos = new();
    public Port.Content.AczOnDemandFetcher TextureFetcher { get; } = new();

    public void ConfigureTextureFetcher(string statusBaseUrl, Port.Content.ContentManifest manifest, string filesRoot)
    {
        try
        {
            TextureFetcher.Configure(statusBaseUrl, manifest, filesRoot);
            Note($"texture fetcher ready — rsicIndex={TextureFetcher.IndexedRsicCount}");
        }
        catch (Exception ex)
        {
            Note($"texture fetcher FAIL: {ex.Message}");
        }
    }

    public void ConfigureTextureFetcher(string statusBaseUrl, IReadOnlyDictionary<string, int> rsicByPath, string filesRoot)
    {
        try
        {
            TextureFetcher.Configure(statusBaseUrl, rsicByPath, filesRoot);
            Note($"texture fetcher ready — rsicIndex={TextureFetcher.IndexedRsicCount}");
        }
        catch (Exception ex)
        {
            Note($"texture fetcher FAIL: {ex.Message}");
        }
    }
    SerializerBootstrap? _serializer;
    byte[]? _mapStrHash;
    byte[]? _pendingMapStrPackage;
    bool _mapStrRequested;
    /// <summary>None → got ServerHandshake → awaiting package → complete (do NOT send ClientHandshake again).</summary>
    enum MapStrPhase { None, AwaitingResponse, AwaitingPackage, Complete }
    MapStrPhase _mapStrPhase = MapStrPhase.None;
    readonly WorldStateCache _worldCache = new();

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
        // Send command first — serializer bootstrap must not block / risk UI drop.
        if (!IsConnected)
            return false;

        try
        {
            // Common SS14 lobby observer entry points across forks.
            SendNamed("MsgConCmd", NetDeliveryMethod.ReliableUnordered, m => m.Write("observe"));
            Note(">> observe");
            try
            {
                SendNamed("MsgConCmd", NetDeliveryMethod.ReliableUnordered, m => m.Write("observer"));
                Note(">> observer");
            }
            catch { /* ignore */ }
        }
        catch (Exception ex)
        {
            Note($"observe cmd fail: {ex.Message}");
            return false;
        }

        IsObserving = true;
        _spawnWarpPending = true;
        _panOffX = 0;
        _panOffY = 0;
        Set(GameSessionPhase.Observing, "ghost flight — awaiting MsgState / spawn warp");

        // Ask server for warp targets; first response auto-warps to observer spawn.
        try { RequestGhostWarps(); } catch { /* serializer may not be ready yet */ }

        _ = Task.Run(() =>
        {
            try
            {
                EnsureSerializer(forceRediscover: true);
                TryApplyPendingMapStrings();
                if (!string.IsNullOrWhiteSpace(ContentFilesRoot))
                {
                    _protoSprites.EnsureLoaded(ContentFilesRoot, Note);
                    _tileProtos.EnsureLoaded(ContentFilesRoot, Note);
                    _worldCache.SetPrototypeIndex(_protoSprites);
                    _worldCache.SetTileIndex(_tileProtos);
                }

                Note($"observe serializer → {SerializerStatus} strings={HasMappedStrings} xfStore={_worldCache.XformCount} protos={_protoSprites.Count} tiles={_tileProtos.Count}");
                try
                {
                    // Prefetch MobObserver RSI so ghost is drawable immediately.
                    TextureFetcher.EnsureFile("Textures/Mobs/Ghosts/ghost_human.rsic", Note);
                    TextureFetcher.EnsureFile("Textures/Mobs/Ghosts/ghost_human.rsi/meta.json", Note);
                }
                catch { /* on-demand later */ }
                try { EnsureKeyMap(); } catch { /* ignore */ }
                try { RequestGhostWarps(); } catch { /* ignore */ }
                // Retry warps a few times until spawn completes (serializer/timing).
                for (var i = 0; i < 8 && _spawnWarpPending && IsObserving; i++)
                {
                    Thread.Sleep(500);
                    try { RequestGhostWarps(); } catch { /* ignore */ }
                }

                if (_serializer is { HasMappedStrings: false } && _mapStrHash is { Length: > 0 })
                {
                    if (_serializer.TryLoadCachedStrings(_mapStrHash, Note))
                    {
                        SerializerStatus = _serializer.Status;
                        Note("observe: mapped strings from cache");
                    }
                    else
                    {
                        Note(_mapStrPhase == MapStrPhase.Complete
                            ? "observe: no string cache — GameState decode needs MsgMapStrStrings (reconnect)"
                            : "observe: waiting for in-progress mapstr package");
                    }
                }

                // Fallback command used by some forks (safe — not mapstr).
                try
                {
                    if (IsConnected && LocalStatus != SessionStatus.InGame)
                    {
                        SendNamed("MsgConCmd", NetDeliveryMethod.ReliableUnordered, m => m.Write("ghost"));
                        Note(">> ghost (fallback)");
                    }
                }
                catch { /* ignore */ }

                Detail = HasMappedStrings
                    ? $"ghost — strings ready, xfStore={_worldCache.XformCount}"
                    : $"ghost — NEED strings ({SerializerStatus}); phase={_mapStrPhase}";
            }
            catch (Exception ex)
            {
                Note($"observe background: {ex.Message}");
            }
        });

        return true;
    }

    public void SetFlightInput(float x, float y)
    {
        _flightX = Math.Clamp(x, -1f, 1f);
        _flightY = Math.Clamp(y, -1f, 1f);
        // Joystick steers the actual ghost entity (PC SharedMoverController), not free-cam.
        if (Math.Abs(_flightX) > 0.2f || Math.Abs(_flightY) > 0.2f)
        {
            _panOffX = 0;
            _panOffY = 0;
        }

        SyncMoveKeysFromStick();
    }

    public void TickFlight(float dt)
    {
        if (!IsObserving)
            return;
        // Predictive local pan only when stick is held AND server input unavailable.
        if (_keyMap is null && (Math.Abs(_flightX) > 0.01f || Math.Abs(_flightY) > 0.01f))
        {
            // Screen-relative pan: stick up → toward top of view (inverse of draw rotation).
            var speed = _flightSpeed / Math.Max(0.35f, Zoom);
            var c = MathF.Cos(CamRotation);
            var s = MathF.Sin(CamRotation);
            var sx = _flightX * speed * dt;
            var sy = _flightY * speed * dt;
            var dx = sx * c - sy * s;
            var dy = sx * s + sy * c;
            _panOffX += dx;
            _panOffY += dy;
            CamX += dx;
            CamY += dy;
        }
        // Otherwise camera follows ControlledEntity via MsgState; stick already sent as Move* keys.
    }

    void SyncMoveKeysFromStick()
    {
        // PC relative movement: screen-space input is rotated by the parent-grid camera basis.
        const float dead = 0.28f;
        var (gx, gy) = GridCameraMath.RotateScreenInput(_flightX, _flightY, CamRotation);
        SetMoveKey(ref _keyRight, EngineKeyFunctions.MoveRight, gx > dead);
        SetMoveKey(ref _keyLeft, EngineKeyFunctions.MoveLeft, gx < -dead);
        SetMoveKey(ref _keyUp, EngineKeyFunctions.MoveUp, gy > dead);
        SetMoveKey(ref _keyDown, EngineKeyFunctions.MoveDown, gy < -dead);
    }

    void SetMoveKey(ref bool held, BoundKeyFunction function, bool wantDown)
    {
        if (held == wantDown)
            return;
        held = wantDown;
        try
        {
            SendKeyState(function, wantDown ? BoundKeyState.Down : BoundKeyState.Up);
        }
        catch (Exception ex)
        {
            Note($"move input FAIL: {ex.Message}");
        }
    }

    void EnsureKeyMap()
    {
        if (_keyMap is not null || _serializer is null)
            return;
        try
        {
            var map = new BoundKeyMap(_serializer.Reflection);
            map.PopulateKeyFunctionsMap();
            _keyMap = map;
            Note("key map ready (Move* → FullInputCmdMessage)");
        }
        catch (Exception ex)
        {
            Note($"key map FAIL: {ex.Message}");
        }
    }

    void SendKeyState(BoundKeyFunction function, BoundKeyState state)
    {
        if (!IsConnected || !IsObserving)
            return;
        EnsureKeyMap();
        if (_keyMap is null)
            return;

        KeyFunctionId funcId;
        try
        {
            funcId = _keyMap.KeyFunctionID(function);
        }
        catch
        {
            return;
        }

        var tick = LastEye?.ToSequence ?? new GameTick(1);
        var msg = new FullInputCmdMessage(
            tick,
            0,
            funcId,
            state,
            NetCoordinates.Invalid,
            ScreenCoordinates.Invalid,
            NetEntity.Invalid)
        {
            InputSequence = ++_inputSequence,
        };
        SendEntitySystemMessage(msg);
    }

    /// <summary>Ghost chat — <c>say</c> (deadchat when observer) / <c>looc</c> / <c>ooc</c>.</summary>
    public bool SendChat(string text, string channel = "say")
    {
        if (!IsConnected || string.IsNullOrWhiteSpace(text))
            return false;
        text = text.Trim();
        if (text.Length > 256)
            text = text[..256];
        var cmd = channel.ToLowerInvariant() switch
        {
            "looc" => "looc",
            "ooc" => "ooc",
            "me" => "me",
            "whisper" => "whisper",
            _ => "say",
        };
        // Escape quotes for console.
        var escaped = text.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return SendConsole($"{cmd} \"{escaped}\"");
    }

    public void AdjustZoom(float factor)
    {
        Zoom = Math.Clamp(Zoom * factor, 0.35f, 3.5f);
    }

    public void PanCamera(float dx, float dy)
    {
        // dx/dy are view-space pixel deltas (already sens-scaled).
        var c = MathF.Cos(CamRotation);
        var s = MathF.Sin(CamRotation);
        var wx = dx * c - dy * s;
        var wy = dx * s + dy * c;
        _panOffX += wx;
        _panOffY += wy;
        CamX += wx;
        CamY += wy;
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

        // Server disconnects on a second NeedsStrings=true ("Cannot request strings twice").
        if (_mapStrRequested || _mapStrPhase is MapStrPhase.AwaitingPackage or MapStrPhase.Complete)
        {
            Note($"mapstr request skipped — already requested (phase={_mapStrPhase})");
            return;
        }

        // Only legal while server opened a handshake (AwaitingResponse).
        if (_mapStrPhase is not MapStrPhase.AwaitingResponse)
        {
            Note($"mapstr request skipped — phase={_mapStrPhase} (ClientHandshake would kick)");
            return;
        }

        try
        {
            _mapStrRequested = true; // set before send so races cannot double-fire
            SendNamed("MsgMapStrClientHandshake", NetDeliveryMethod.ReliableOrdered, m =>
            {
                m.Write(true); // NeedsStrings
            });
            _mapStrPhase = MapStrPhase.AwaitingPackage;
            Note("mapstr: requested NeedsStrings=true");
        }
        catch (Exception ex)
        {
            _mapStrRequested = false;
            Note($"mapstr request fail: {ex.Message}");
        }
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
        if (Phase is GameSessionPhase.InLobby or GameSessionPhase.Observing
            or GameSessionPhase.LoginSuccess or GameSessionPhase.SyncingCVars
            or GameSessionPhase.RequestingPlayers)
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
            if (Phase is GameSessionPhase.InLobby or GameSessionPhase.Observing)
                Set(GameSessionPhase.Failed, ex.Message);
            // Keep IsObserving until Dispose so UI can show the error briefly.
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
                        // Do not downgrade AwaitingPackage/Complete — a second ServerHandshake
                        // must not reset phase and trigger another NeedsStrings=true.
                        if (_mapStrPhase is MapStrPhase.None or MapStrPhase.AwaitingResponse)
                            _mapStrPhase = MapStrPhase.AwaitingResponse;
                        EnsureSerializer();
                        if (_serializer is not null
                            && _serializer.TryLoadCachedStrings(_mapStrHash, Note))
                        {
                            SerializerStatus = _serializer.Status;
                            SendNamed("MsgMapStrClientHandshake", NetDeliveryMethod.ReliableOrdered, m =>
                            {
                                m.Write(false);
                            });
                            _mapStrPhase = MapStrPhase.Complete;
                            mapstrDone = true;
                            Note("mapstr: cache HIT — NeedsStrings=false");
                            break;
                        }

                        // Single NeedsStrings=true for this handshake (via RequestMappedStrings).
                        if (_mapStrPhase == MapStrPhase.AwaitingResponse && !_mapStrRequested)
                            RequestMappedStrings();
                        else
                            Note($"mapstr: handshake seen — phase={_mapStrPhase} requested={_mapStrRequested}");
                        break;

                    case "MsgMapStrStrings":
                    {
                        Set(GameSessionPhase.MapStrings, "MsgMapStrStrings");
                        var size = msg.ReadVariableInt32();
                        var package = msg.ReadBytes(size);
                        Note($"MsgMapStrStrings {package.Length:N0} B");
                        _pendingMapStrPackage = package;
                        EnsureSerializer();
                        var applied = TryApplyPendingMapStrings();
                        if (_mapStrPhase is MapStrPhase.AwaitingPackage or MapStrPhase.AwaitingResponse)
                        {
                            // Ack only after we buffered/applied — never drop package on late Assemblies.
                            SendNamed("MsgMapStrClientHandshake", NetDeliveryMethod.ReliableOrdered, m =>
                            {
                                m.Write(false);
                            });
                            Note(applied
                                ? "sent MsgMapStrClientHandshake NeedsStrings=false (after package)"
                                : "sent NeedsStrings=false — package buffered until serializer ready");
                        }

                        _mapStrPhase = MapStrPhase.Complete;
                        mapstrDone = true;
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
            // Don't stall forever on mapstr — but NEVER send a second ClientHandshake
            // (server disconnects: "without in-progress handshake").
            if (!mapstrDone && stringTableReady && bootElapsed > TimeSpan.FromSeconds(20))
            {
                if (_mapStrPhase == MapStrPhase.None)
                {
                    Note("mapstr: no ServerHandshake yet — continue without strings");
                    _mapStrPhase = MapStrPhase.Complete;
                    mapstrDone = true;
                }
                else if (_mapStrPhase == MapStrPhase.AwaitingPackage
                         && _serializer?.HasMappedStrings == true)
                {
                    _mapStrPhase = MapStrPhase.Complete;
                    mapstrDone = true;
                    Note("mapstr: package already loaded — mark complete");
                }
                else if (_mapStrPhase == MapStrPhase.AwaitingPackage && bootElapsed > TimeSpan.FromSeconds(45))
                {
                    // Give up waiting for package; do not send another handshake.
                    Note("mapstr: package timeout — lobby without strings (GameState decode limited)");
                    _mapStrPhase = MapStrPhase.Complete;
                    mapstrDone = true;
                }
                else if (_mapStrPhase == MapStrPhase.Complete)
                {
                    mapstrDone = true;
                }
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
                _pendingMapStrPackage = package;
                EnsureSerializer();
                TryApplyPendingMapStrings();

                if (_mapStrPhase is MapStrPhase.AwaitingPackage or MapStrPhase.AwaitingResponse)
                {
                    try
                    {
                        SendNamed("MsgMapStrClientHandshake", NetDeliveryMethod.ReliableOrdered, m => m.Write(false));
                    }
                    catch { /* ignore */ }
                }

                _mapStrPhase = MapStrPhase.Complete;
                break;
            }

            case "MsgMapStrServerHandshake":
                _mapStrHash = ReadMapStrHash(msg);
                if (_mapStrPhase is MapStrPhase.None or MapStrPhase.AwaitingResponse)
                    _mapStrPhase = MapStrPhase.AwaitingResponse;
                EnsureSerializer();
                if (_serializer is not null && !_serializer.HasMappedStrings)
                {
                    if (_serializer.TryLoadCachedStrings(_mapStrHash, Note))
                    {
                        SerializerStatus = _serializer.Status;
                        SendNamed("MsgMapStrClientHandshake", NetDeliveryMethod.ReliableOrdered, m => m.Write(false));
                        _mapStrPhase = MapStrPhase.Complete;
                    }
                    else if (_mapStrPhase == MapStrPhase.AwaitingResponse && !_mapStrRequested)
                        RequestMappedStrings();
                }
                else if (_mapStrPhase is not MapStrPhase.AwaitingPackage)
                {
                    // Already have strings (or no serializer yet) — ack without download.
                    try
                    {
                        SendNamed("MsgMapStrClientHandshake", NetDeliveryMethod.ReliableOrdered, m => m.Write(false));
                        _mapStrPhase = MapStrPhase.Complete;
                    }
                    catch { /* ignore */ }
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
                HandleMsgStateLeavePvs(msg);
                break;

            case "MsgChatMessage":
                HandleMsgChatMessage(msg);
                break;

            case "MsgEntity":
                HandleMsgEntity(msg);
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

    void HandleMsgStateLeavePvs(NetIncomingMessage msg)
    {
        try
        {
            var tick = msg.ReadUInt32();
            var length = msg.ReadInt32();
            if (length < 0 || length > 50_000)
            {
                Note($"MsgStateLeavePvs bad length={length}");
                return;
            }

            var leaving = new List<NetEntity>(length);
            for (var i = 0; i < length; i++)
                leaving.Add(new NetEntity(msg.ReadInt32()));

            // Free-cam pan alone does not move the eye — but LeavePvs MUST apply so maps/areas
            // unload when the ghost warps / PVS moves. Otherwise stations stack forever.
            _worldCache.RemoveEntities(leaving);
            if (LastWorld is { } world && leaving.Count > 0)
            {
                var drop = new HashSet<NetEntity>(leaving);
                var kept = world.Entities.Where(e => !drop.Contains(e.Entity)).ToList();
                var audioKept = world.Audio?.Where(a => !drop.Contains(a.Entity)).ToList()
                                ?? (IReadOnlyList<WorldAudioCue>)Array.Empty<WorldAudioCue>();
                // Rebuild floors from remaining grids — old area tiles must not linger after warp.
                var tiles = _worldCache.RebuildTilesNearEye(40f);
                LastWorld = world with
                {
                    Entities = kept,
                    Tiles = tiles,
                    Audio = audioKept,
                    Detail = world.Detail + $" leavePvs={leaving.Count}",
                };
            }

            // Reset free-cam pan after large leave so camera recenters on new PVS bubble.
            if (leaving.Count > 64)
            {
                _panOffX = 0;
                _panOffY = 0;
            }

            Note($"MsgStateLeavePvs tick={tick} left={leaving.Count} store={_worldCache.XformCount}");
        }
        catch (Exception ex)
        {
            Note($"MsgStateLeavePvs FAIL: {ex.Message}");
        }
    }

    readonly List<ChatLine> _chatLines = new();
    readonly object _chatGate = new();
    readonly object _chatAudioGate = new();
    readonly List<(string Path, float VolumeDb)> _chatAudioPending = new();

    public IReadOnlyList<(string Path, float VolumeDb)> DrainChatAudio()
    {
        lock (_chatAudioGate)
        {
            if (_chatAudioPending.Count == 0)
                return Array.Empty<(string, float)>();
            var copy = _chatAudioPending.ToArray();
            _chatAudioPending.Clear();
            return copy;
        }
    }
    int _chatVersion;
    readonly List<GhostWarpEntry> _ghostWarps = new();
    readonly object _warpGate = new();
    int _warpVersion;
    uint _entityMsgSequence;

    public IReadOnlyList<ChatLine> ChatLines
    {
        get { lock (_chatGate) return _chatLines.ToList(); }
    }

    public int ChatVersion
    {
        get { lock (_chatGate) return _chatVersion; }
    }

    public IReadOnlyList<GhostWarpEntry> GhostWarps
    {
        get { lock (_warpGate) return _ghostWarps.ToList(); }
    }

    public int WarpVersion
    {
        get { lock (_warpGate) return _warpVersion; }
    }

    public bool CycleWarp(out string? destinationName)
    {
        destinationName = null;
        if (!IsConnected)
            return false;

        // Prefer location warp points (maps/rooms); fall back to players.
        List<GhostWarpEntry> list;
        lock (_warpGate)
        {
            list = _ghostWarps.Where(w => w.IsWarpPoint).ToList();
            if (list.Count == 0)
                list = _ghostWarps.ToList();
        }

        if (list.Count == 0)
        {
            _warpCyclePending = true;
            try { RequestGhostWarps(); } catch { /* ignore */ }
            return false;
        }

        _warpCyclePending = false;
        _warpCycleIndex = (_warpCycleIndex + 1) % list.Count;
        var target = list[_warpCycleIndex];
        destinationName = target.DisplayName;
        var ok = WarpTo(target.Entity);
        if (ok)
        {
            try { WarpCycled?.Invoke(destinationName); } catch { /* UI */ }
        }

        return ok;
    }

    bool _warpCyclePending;
    int _warpCycleIndex = -1;

    public void RequestGhostWarps()
    {
        if (!IsConnected || _serializer is not { HasMappedStrings: true } boot)
        {
            Note("RequestGhostWarps deferred — serializer not ready");
            return;
        }

        if (!boot.Reflection.TryLooseGetType("Content.Shared.Ghost.GhostWarpsRequestEvent", out var type)
            && !boot.Reflection.TryLooseGetType("GhostWarpsRequestEvent", out type))
        {
            Note("GhostWarpsRequestEvent type missing");
            return;
        }

        var evt = Activator.CreateInstance(type);
        if (evt is null) return;
        SendEntitySystemMessage(evt);
        Note(">> GhostWarpsRequestEvent");
    }

    public bool WarpTo(NetEntity target)
    {
        if (!IsConnected || !target.IsValid() || _serializer is not { HasMappedStrings: true } boot)
            return false;

        if (!boot.Reflection.TryLooseGetType("Content.Shared.Ghost.GhostWarpToTargetRequestEvent", out var type)
            && !boot.Reflection.TryLooseGetType("GhostWarpToTargetRequestEvent", out type))
        {
            Note("GhostWarpToTargetRequestEvent type missing");
            return false;
        }

        object? evt = null;
        try
        {
            evt = Activator.CreateInstance(type, target);
        }
        catch
        {
            try
            {
                evt = Activator.CreateInstance(type);
                type.GetProperty("Target")?.SetValue(evt, target);
                type.GetField("Target")?.SetValue(evt, target);
            }
            catch (Exception ex)
            {
                Note($"WarpTo create FAIL: {ex.Message}");
                return false;
            }
        }

        if (evt is null) return false;
        // Reset pan so camera follows the new eye position after warp.
        _panOffX = 0;
        _panOffY = 0;
        SendEntitySystemMessage(evt);
        Note($">> GhostWarpToTargetRequestEvent → {target}");
        return true;
    }

    /// <summary>PC GhostnadoRequestEvent — warp to the most-followed ghost target.</summary>
    public bool Ghostnado()
    {
        if (!IsConnected || _serializer is not { HasMappedStrings: true } boot)
            return false;

        if (!boot.Reflection.TryLooseGetType("Content.Shared.Ghost.GhostnadoRequestEvent", out var type)
            && !boot.Reflection.TryLooseGetType("GhostnadoRequestEvent", out type))
        {
            Note("GhostnadoRequestEvent type missing");
            return false;
        }

        var evt = Activator.CreateInstance(type);
        if (evt is null) return false;
        _panOffX = 0;
        _panOffY = 0;
        SendEntitySystemMessage(evt);
        Note(">> GhostnadoRequestEvent");
        return true;
    }

    /// <summary>PC GhostSystem.ReturnToBody → GhostReturnToBodyRequest.</summary>
    public bool ReturnToBody()
    {
        if (!IsConnected || _serializer is not { HasMappedStrings: true } boot)
            return false;
        if (!CanReturnToBody)
        {
            Note("ReturnToBody blocked — CanReturnToBody=false");
            return false;
        }

        if (!boot.Reflection.TryLooseGetType("Content.Shared.Ghost.GhostReturnToBodyRequest", out var type)
            && !boot.Reflection.TryLooseGetType("GhostReturnToBodyRequest", out type)
            && !boot.Reflection.TryLooseGetType("Content.Shared.Ghost.SharedGhostSystem+GhostReturnToBodyRequest", out type))
        {
            Note("GhostReturnToBodyRequest type missing");
            return false;
        }

        var evt = Activator.CreateInstance(type);
        if (evt is null) return false;
        _panOffX = 0;
        _panOffY = 0;
        SendEntitySystemMessage(evt);
        Note(">> GhostReturnToBodyRequest");
        return true;
    }

    /// <summary>Cycle follow among player warps (non-warp-point entries).</summary>
    public bool CycleFollowPlayer(out string? name)
    {
        name = null;
        if (!IsConnected)
            return false;

        List<GhostWarpEntry> players;
        lock (_warpGate)
            players = _ghostWarps.Where(w => !w.IsWarpPoint).ToList();

        if (players.Count == 0)
        {
            try { RequestGhostWarps(); } catch { /* ignore */ }
            return false;
        }

        _followCycleIndex = (_followCycleIndex + 1) % players.Count;
        var target = players[_followCycleIndex];
        name = target.DisplayName;
        return WarpTo(target.Entity);
    }

    int _followCycleIndex = -1;

    /// <summary>PC GhostSystem.OpenGhostRoles → remote console ghostroles.</summary>
    public bool OpenGhostRoles()
    {
        if (!IsConnected)
            return false;
        if (!CanTakeGhostRoles)
        {
            Note("ghostroles blocked — CanTakeGhostRoles=false");
            return false;
        }
        try
        {
            SendNamed("MsgConCmd", NetDeliveryMethod.ReliableUnordered, m => m.Write("ghostroles"));
            Note(">> ghostroles");
            return true;
        }
        catch (Exception ex)
        {
            Note($"ghostroles FAIL: {ex.Message}");
            return false;
        }
    }

    /// <summary>PC GhostSystem.OnToggleFoV — local eye DrawFov.</summary>
    public void ToggleFov()
    {
        DrawFov = false;
        Note("ToggleFoV ignored: forced off");
        try { GhostUiChanged?.Invoke(); } catch { /* UI */ }
    }

    /// <summary>PC GhostSystem.OnToggleLighting — normal ↔ fullbright.</summary>
    public void ToggleLighting()
    {
        _lightingMode = 1;
        DrawLighting = false;
        Note("ToggleLighting ignored: forced fullbright");
        try { GhostUiChanged?.Invoke(); } catch { /* UI */ }
    }

    /// <summary>PC GhostSystem.ToggleGhostVisibility.</summary>
    public void ToggleOtherGhosts()
    {
        ShowOtherGhosts = !ShowOtherGhosts;
        Note($"ToggleGhosts → {ShowOtherGhosts}");
        try { GhostUiChanged?.Invoke(); } catch { /* UI */ }
    }

    public void SetCanReturnToBody(bool value)
    {
        if (CanReturnToBody == value) return;
        CanReturnToBody = value;
        try { GhostUiChanged?.Invoke(); } catch { /* UI */ }
    }

    void SyncGhostFlagsFromWorld()
    {
        var changed = false;
        if (_worldCache.TryGetControlledGhostFlags(out var canReturn, out var canRoles))
        {
            if (CanReturnToBody != canReturn)
            {
                CanReturnToBody = canReturn;
                changed = true;
            }
            if (CanTakeGhostRoles != canRoles)
            {
                CanTakeGhostRoles = canRoles;
                changed = true;
            }
        }

        if (changed)
            try { GhostUiChanged?.Invoke(); } catch { /* UI */ }
    }

    void SendEntitySystemMessage(object systemMessage)
    {
        if (_serializer is null)
            throw new InvalidOperationException("serializer missing");

        var tick = LastEye?.ToSequence ?? new GameTick(1);
        var seq = ++_entityMsgSequence;
        SendNamed("MsgEntity", NetDeliveryMethod.ReliableOrdered, m =>
        {
            m.Write((byte)1); // EntityMessageType.SystemMessage
            m.Write(tick);
            m.Write(seq);
            using var stream = new MemoryStream();
            _serializer.Serializer.Serialize(stream, systemMessage);
            var bytes = stream.ToArray();
            m.WriteVariableInt32(bytes.Length);
            m.Write(bytes);
        });
    }

    void HandleMsgEntity(NetIncomingMessage msg)
    {
        try
        {
            if (_serializer is not { HasMappedStrings: true } boot)
            {
                Note($"MsgEntity skipped ({msg.LengthBytes}B) — no serializer");
                return;
            }

            var typeByte = msg.ReadByte();
            var sourceTick = msg.ReadGameTick();
            var sequence = msg.ReadUInt32();
            if (typeByte != 1) // SystemMessage
            {
                Note($"MsgEntity type={typeByte} tick={sourceTick} seq={sequence}");
                return;
            }

            var length = msg.ReadVariableInt32();
            if (length <= 0 || length > 4_000_000)
            {
                Note($"MsgEntity bad length={length}");
                return;
            }

            using var stream = new MemoryStream(length);
            msg.ReadAlignedMemory(stream, length);
            object? evt;
            try
            {
                evt = boot.Serializer.Deserialize(stream);
            }
            catch (Exception ex)
            {
                Note($"MsgEntity deserialize FAIL: {ex.Message}");
                return;
            }

            if (evt is null) return;
            var tn = evt.GetType().Name;
            if (tn.Contains("GhostWarpsResponse", StringComparison.Ordinal))
            {
                ParseGhostWarpsResponse(evt);
                return;
            }

            if (tn.Contains("GhostUpdateGhostRoleCount", StringComparison.Ordinal)
                || tn.Contains("GhostRoleCount", StringComparison.Ordinal))
            {
                ParseGhostRoleCount(evt);
                return;
            }

            if (StatesReceived <= 3 || tn.Contains("Ghost", StringComparison.Ordinal))
                Note($"MsgEntity {tn} tick={sourceTick}");
        }
        catch (Exception ex)
        {
            Note($"MsgEntity FAIL: {ex.Message}");
        }
    }

    void ParseGhostRoleCount(object evt)
    {
        try
        {
            var t = evt.GetType();
            var n = t.GetProperty("AvailableGhostRoles")?.GetValue(evt)
                    ?? t.GetField("AvailableGhostRoles")?.GetValue(evt)
                    ?? t.GetProperty("AvailableGhostRoleCount")?.GetValue(evt)
                    ?? t.GetField("AvailableGhostRoleCount")?.GetValue(evt)
                    ?? t.GetProperty("Count")?.GetValue(evt);
            if (n is int i)
                GhostRoleCount = i;
            else if (n is not null && int.TryParse(n.ToString(), out var p))
                GhostRoleCount = p;
            Note($"GhostRoleCount={GhostRoleCount}");
            try { GhostUiChanged?.Invoke(); } catch { /* UI */ }
        }
        catch (Exception ex)
        {
            Note($"GhostRoleCount FAIL: {ex.Message}");
        }
    }

    void ParseGhostWarpsResponse(object evt)
    {
        try
        {
            var list = new List<GhostWarpEntry>();
            var t = evt.GetType();

            // Vanilla: Warps: List<GhostWarp>
            var warpsObj = t.GetProperty("Warps")?.GetValue(evt) ?? t.GetField("Warps")?.GetValue(evt);
            if (warpsObj is System.Collections.IEnumerable enWarps)
            {
                foreach (var item in enWarps)
                    TryAddWarpItem(item, list, defaultCategory: null);
            }

            // Mini / enriched panel: Players + Places + Antagonists
            AppendNamedWarps(t.GetProperty("Places")?.GetValue(evt) ?? t.GetField("Places")?.GetValue(evt),
                list, isWarpPoint: true, category: "place");
            AppendNamedWarps(t.GetProperty("Players")?.GetValue(evt) ?? t.GetField("Players")?.GetValue(evt),
                list, isWarpPoint: false, category: "player");
            AppendNamedWarps(t.GetProperty("Antagonists")?.GetValue(evt) ?? t.GetField("Antagonists")?.GetValue(evt),
                list, isWarpPoint: false, category: "antag");

            if (list.Count == 0)
            {
                Note("GhostWarpsResponse: empty (no Warps/Players/Places)");
                return;
            }

            // Dedupe by entity id (players may also appear as antags).
            list = list
                .GroupBy(w => w.Entity.Id)
                .Select(g => g.FirstOrDefault(x => x.Category == "antag") ?? g.First())
                .OrderBy(w => w.Category switch { "place" => 0, "antag" => 1, _ => 2 })
                .ThenBy(w => w.DisplayName)
                .ToList();

            lock (_warpGate)
            {
                _ghostWarps.Clear();
                _ghostWarps.AddRange(list);
                _warpVersion++;
            }

            Note($"GhostWarpsResponse: {list.Count} targets " +
                 $"(places={list.Count(w => w.IsWarpPoint)} players={list.Count(w => !w.IsWarpPoint)})");
            try { GhostUiChanged?.Invoke(); } catch { /* UI */ }

            if (_spawnWarpPending && list.Count > 0)
            {
                _spawnWarpPending = false;
                var spawn = PickObserverSpawn(list);
                if (WarpTo(spawn.Entity))
                {
                    Note($"spawn warp → {spawn.DisplayName}");
                    try { WarpCycled?.Invoke(spawn.DisplayName); } catch { /* UI */ }
                }
                else
                {
                    _spawnWarpPending = true; // retry
                    Note("spawn warp FAIL — will retry");
                }
            }
            else if (_warpCyclePending && list.Count > 0)
            {
                if (CycleWarp(out var name) && name is not null)
                    Note($"auto-cycle warp → {name}");
            }
        }
        catch (Exception ex)
        {
            Note($"ParseGhostWarps FAIL: {ex.Message}");
        }
    }

    static void AppendNamedWarps(object? collection, List<GhostWarpEntry> list, bool isWarpPoint, string category)
    {
        if (collection is not System.Collections.IEnumerable en)
            return;
        foreach (var item in en)
            TryAddWarpItem(item, list, defaultCategory: category, forceWarpPoint: isWarpPoint);
    }

    static void TryAddWarpItem(object? item, List<GhostWarpEntry> list, string? defaultCategory, bool? forceWarpPoint = null)
    {
        if (item is null) return;
        var it = item.GetType();
        var ent = it.GetProperty("Entity")?.GetValue(item) as NetEntity?
                  ?? it.GetField("Entity")?.GetValue(item) as NetEntity?
                  ?? default;
        if (!ent.IsValid())
            return;

        var name = it.GetProperty("DisplayName")?.GetValue(item) as string
                   ?? it.GetField("DisplayName")?.GetValue(item) as string
                   ?? it.GetProperty("Name")?.GetValue(item) as string
                   ?? it.GetField("Name")?.GetValue(item) as string
                   ?? "?";

        var isWp = forceWarpPoint
                   ?? it.GetProperty("IsWarpPoint")?.GetValue(item) as bool?
                   ?? it.GetField("IsWarpPoint")?.GetValue(item) as bool?
                   ?? (defaultCategory == "place");

        string? sub = it.GetProperty("Description")?.GetValue(item) as string
                      ?? it.GetField("Description")?.GetValue(item) as string
                      ?? it.GetProperty("AntagonistName")?.GetValue(item) as string
                      ?? it.GetField("AntagonistName")?.GetValue(item) as string
                      ?? it.GetProperty("JobId")?.GetValue(item)?.ToString();

        var cat = defaultCategory
                  ?? (isWp ? "place" : "player");
        if (defaultCategory == "antag")
            cat = "antag";
        else if (cat == "player" && !string.IsNullOrEmpty(sub)
                 && it.Name.Contains("Antagonist", StringComparison.OrdinalIgnoreCase))
            cat = "antag";

        list.Add(new GhostWarpEntry(ent, name, isWp, cat, sub));
    }

    bool _spawnWarpPending;

    static GhostWarpEntry PickObserverSpawn(IReadOnlyList<GhostWarpEntry> list)
    {
        // Mirror GameTicker.GetObserverSpawnPoint: prefer Observer spawn warp points.
        static int Score(GhostWarpEntry w)
        {
            var n = w.DisplayName ?? "";
            var score = w.IsWarpPoint ? 100 : 0;
            if (n.Contains("Observer", StringComparison.OrdinalIgnoreCase)) score += 50;
            if (n.Contains("наблюд", StringComparison.OrdinalIgnoreCase)) score += 50;
            if (n.Contains("Spawn", StringComparison.OrdinalIgnoreCase)) score += 30;
            if (n.Contains("спавн", StringComparison.OrdinalIgnoreCase)) score += 30;
            if (n.Contains("Arrive", StringComparison.OrdinalIgnoreCase)) score += 10;
            if (n.Contains("Late", StringComparison.OrdinalIgnoreCase)) score += 10;
            return score;
        }

        return list.OrderByDescending(Score).ThenBy(w => w.DisplayName).First();
    }

    const int MaxChatLines = 200;

    void HandleMsgChatMessage(NetIncomingMessage msg)
    {
        try
        {
            if (_serializer is not { HasMappedStrings: true } boot)
            {
                // Consume payload so the stream stays aligned.
                var skipLen = msg.ReadVariableInt32();
                if (skipLen > 0 && skipLen < 2_000_000)
                    msg.ReadBytes(skipLen);
                Note("MsgChatMessage deferred — serializer not ready");
                return;
            }

            var length = msg.ReadVariableInt32();
            if (length <= 0 || length > 2_000_000)
            {
                Note($"MsgChatMessage bad length={length}");
                return;
            }

            using var stream = new MemoryStream(length);
            msg.ReadAlignedMemory(stream, length);

            if (!boot.Reflection.TryLooseGetType("Content.Shared.Chat.ChatMessage", out var chatType)
                && !boot.Reflection.TryLooseGetType("ChatMessage", out chatType))
            {
                Note("MsgChatMessage: ChatMessage type missing");
                return;
            }

            var method = typeof(Robust.Shared.Serialization.IRobustSerializer)
                .GetMethod(nameof(Robust.Shared.Serialization.IRobustSerializer.DeserializeDirect))!
                .MakeGenericMethod(chatType);
            var args = new object?[] { stream, null };
            method.Invoke(boot.Serializer, args);
            var obj = args[1];
            if (obj is null)
                return;

            var t = obj.GetType();
            var hide = t.GetField("HideChat")?.GetValue(obj) as bool? ?? false;
            if (hide)
                return;

            var message = t.GetField("Message")?.GetValue(obj) as string
                          ?? t.GetProperty("Message")?.GetValue(obj) as string
                          ?? "";
            var wrapped = t.GetField("WrappedMessage")?.GetValue(obj) as string
                          ?? t.GetProperty("WrappedMessage")?.GetValue(obj) as string
                          ?? message;
            var channelObj = t.GetField("Channel")?.GetValue(obj)
                             ?? t.GetProperty("Channel")?.GetValue(obj);
            var channel = channelObj?.ToString() ?? "Chat";
            ushort channelFlags = 0;
            if (channelObj is Enum en)
            {
                channel = en.ToString();
                try
                {
                    var raw = Convert.ToUInt32(en);
                    channelFlags = (ushort)(raw & 0xFFFF);
                }
                catch { /* ignore */ }
            }
            else if (channelObj is not null && uint.TryParse(channelObj.ToString(), out var flags))
            {
                channelFlags = (ushort)(flags & 0xFFFF);
                channel = ChatChannelLabel(channelFlags);
            }

            var overrideColor = t.GetField("MessageColorOverride")?.GetValue(obj)
                                ?? t.GetProperty("MessageColorOverride")?.GetValue(obj);
            var argb = ColorArgbFromChannel(channelFlags);
            if (overrideColor is not null)
            {
                try
                {
                    var ct = overrideColor.GetType();
                    var rf = ct.GetProperty("R")?.GetValue(overrideColor) as float?
                             ?? ct.GetField("R")?.GetValue(overrideColor) as float? ?? 1f;
                    var gf = ct.GetProperty("G")?.GetValue(overrideColor) as float?
                             ?? ct.GetField("G")?.GetValue(overrideColor) as float? ?? 1f;
                    var bf = ct.GetProperty("B")?.GetValue(overrideColor) as float?
                             ?? ct.GetField("B")?.GetValue(overrideColor) as float? ?? 1f;
                    var af = ct.GetProperty("A")?.GetValue(overrideColor) as float?
                             ?? ct.GetField("A")?.GetValue(overrideColor) as float? ?? 1f;
                    argb = (unchecked((int)(Math.Clamp(af, 0f, 1f) * 255)) << 24)
                           | ((int)(Math.Clamp(rf, 0f, 1f) * 255) << 16)
                           | ((int)(Math.Clamp(gf, 0f, 1f) * 255) << 8)
                           | (int)(Math.Clamp(bf, 0f, 1f) * 255);
                }
                catch { /* keep channel color */ }
            }

            var text = StripMarkup(string.IsNullOrWhiteSpace(wrapped) ? message : wrapped);
            if (string.IsNullOrWhiteSpace(text))
                text = message;
            if (string.IsNullOrWhiteSpace(text))
                return;

            lock (_chatGate)
            {
                _chatLines.Add(new ChatLine(channel, text.Trim(), DateTime.UtcNow, argb));
                while (_chatLines.Count > MaxChatLines)
                    _chatLines.RemoveAt(0);
                _chatVersion++;
            }

            var sender = ReadNetEntity(t.GetField("SenderEntity")?.GetValue(obj)
                                       ?? t.GetProperty("SenderEntity")?.GetValue(obj));
            var bubbleText = ExtractSpeechBubbleText(wrapped, message);
            if (sender.IsValid() && ShouldShowSpeechBubble(channelFlags, channel) && !string.IsNullOrWhiteSpace(bubbleText))
                EnqueueSpeechBubble(sender, bubbleText.Trim(), argb, channelFlags);

            var audioPath = t.GetField("AudioPath")?.GetValue(obj) as string
                            ?? t.GetProperty("AudioPath")?.GetValue(obj) as string;
            if (!string.IsNullOrWhiteSpace(audioPath))
            {
                var vol = t.GetField("AudioVolume")?.GetValue(obj) as float?
                          ?? t.GetProperty("AudioVolume")?.GetValue(obj) as float?
                          ?? 0f;
                lock (_chatAudioGate)
                {
                    _chatAudioPending.Add((audioPath.Trim(), vol));
                    if (_chatAudioPending.Count > 16)
                        _chatAudioPending.RemoveAt(0);
                }
            }

            Note($"chat [{channel}] {(text.Length <= 80 ? text : text[..80] + "…")}");
        }
        catch (Exception ex)
        {
            Note($"MsgChatMessage FAIL: {ex.Message}");
        }
    }

    static NetEntity ReadNetEntity(object? value)
    {
        if (value is NetEntity ne)
            return ne;
        if (value is int i)
            return new NetEntity(i);
        if (value is uint u)
            return new NetEntity(unchecked((int)u));
        if (value is not null && int.TryParse(value.ToString(), out var parsed))
            return new NetEntity(parsed);
        return default;
    }

    /// <summary>Local / Whisper / LOOC / Emotes / Dead — same channels as ChatUIController.</summary>
    static bool ShouldShowSpeechBubble(ushort flags)
    {
        const ushort mask =
            (1 << 0) | // Local
            (1 << 1) | // Whisper
            (1 << 5) | // LOOC
            (1 << 9) | // Emotes
            (1 << 10); // Dead
        if ((flags & mask) != 0)
            return true;
        // Fallback when only channel name survived (flags lost).
        return false;
    }

    static bool ShouldShowSpeechBubble(ushort flags, string channel)
    {
        if (ShouldShowSpeechBubble(flags))
            return true;
        return channel.Equals("Local", StringComparison.OrdinalIgnoreCase)
               || channel.Equals("Whisper", StringComparison.OrdinalIgnoreCase)
               || channel.Equals("LOOC", StringComparison.OrdinalIgnoreCase)
               || channel.Equals("Emotes", StringComparison.OrdinalIgnoreCase)
               || channel.Equals("Dead", StringComparison.OrdinalIgnoreCase);
    }

    static string ExtractSpeechBubbleText(string wrapped, string message)
    {
        // Fancy bubbles wrap spoken text in BubbleContent.
        var m = System.Text.RegularExpressions.Regex.Match(
            wrapped ?? "",
            @"\[BubbleContent[^\]]*\](.*?)\[/BubbleContent\]",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.Singleline);
        if (m.Success)
        {
            var inner = StripMarkup(m.Groups[1].Value).Trim();
            if (inner.Length > 0)
                return TruncateBubble(inner);
        }

        var plain = StripMarkup(string.IsNullOrWhiteSpace(message) ? wrapped : message).Trim();
        return TruncateBubble(plain);
    }

    static string TruncateBubble(string s)
    {
        if (s.Length <= 140)
            return s;
        return s[..137] + "…";
    }

    sealed class ActiveSpeechBubble
    {
        public NetEntity Entity;
        public string Text = "";
        public int Argb;
        public DateTime ExpiresUtc;
        public float StackOffset;
        public bool EmoteStyle;
    }

    readonly List<ActiveSpeechBubble> _speechBubbles = new();
    readonly object _bubbleGate = new();
    const int SpeechBubbleCap = 4;
    static readonly TimeSpan SpeechBubbleLife = TimeSpan.FromSeconds(4);
    static readonly TimeSpan SpeechBubbleFade = TimeSpan.FromSeconds(0.25);

    void EnqueueSpeechBubble(NetEntity entity, string text, int argb, ushort channelFlags)
    {
        var now = DateTime.UtcNow;
        var emote = (channelFlags & (1 << 9)) != 0 || (channelFlags & (1 << 5)) != 0;
        lock (_bubbleGate)
        {
            PruneSpeechBubblesLocked(now);
            // Approximate content height so older bubbles push up (PC SpeechBubble).
            var approxH = 28f + Math.Min(3, text.Length / 40) * 16f;
            foreach (var existing in _speechBubbles)
            {
                if (existing.Entity == entity)
                    existing.StackOffset += approxH;
            }

            _speechBubbles.Add(new ActiveSpeechBubble
            {
                Entity = entity,
                Text = text,
                Argb = argb,
                ExpiresUtc = now + SpeechBubbleLife,
                StackOffset = 0,
                EmoteStyle = emote,
            });

            var forEnt = 0;
            for (var i = _speechBubbles.Count - 1; i >= 0; i--)
            {
                if (_speechBubbles[i].Entity != entity)
                    continue;
                forEnt++;
                if (forEnt > SpeechBubbleCap)
                    _speechBubbles[i].ExpiresUtc = now; // fade immediately
            }
        }
    }

    void PruneSpeechBubblesLocked(DateTime now)
    {
        _speechBubbles.RemoveAll(b => b.ExpiresUtc < now - SpeechBubbleFade);
    }

    public IReadOnlyList<SpeechBubbleDraw> SnapshotSpeechBubbles()
    {
        var now = DateTime.UtcNow;
        var list = new List<SpeechBubbleDraw>(8);
        lock (_bubbleGate)
        {
            PruneSpeechBubblesLocked(now);
            foreach (var b in _speechBubbles)
            {
                if (!_worldCache.TryGetWorldPos(b.Entity, out var x, out var y))
                    continue;
                var remaining = (b.ExpiresUtc - now).TotalSeconds;
                float alpha = 1f;
                if (remaining <= 0)
                    alpha = Math.Clamp(1f + (float)(remaining / SpeechBubbleFade.TotalSeconds), 0f, 1f);
                else if (remaining < SpeechBubbleFade.TotalSeconds)
                    alpha = (float)(remaining / SpeechBubbleFade.TotalSeconds);

                list.Add(new SpeechBubbleDraw(x, y, b.Text, b.Argb, alpha, b.StackOffset));
            }
        }

        return list;
    }

    /// <summary>Matches Content.Shared.Chat.ChatChannelExtensions.TextColor.</summary>
    static int ColorArgbFromChannel(ushort flags)
    {
        // Prefer exact single-channel match; else default LightGray.
        return flags switch
        {
            1 << 2 => unchecked((int)0xFFFFA500), // Server Orange
            1 << 4 => unchecked((int)0xFF32CD32), // Radio LimeGreen
            1 << 5 => unchecked((int)0xFF48D1CC), // LOOC MediumTurquoise
            1 << 6 => unchecked((int)0xFF87CEFA), // OOC LightSkyBlue
            1 << 10 => unchecked((int)0xFF9370DB), // Dead MediumPurple
            1 << 11 => unchecked((int)0xFFFF0000), // Admin Red
            1 << 12 => unchecked((int)0xFFFF0000), // AdminAlert Red
            1 << 13 => unchecked((int)0xFFFF69B4), // AdminChat HotPink
            1 << 1 => unchecked((int)0xFFA9A9A9), // Whisper DarkGray
            _ when (flags & (1 << 6)) != 0 => unchecked((int)0xFF87CEFA),
            _ when (flags & (1 << 5)) != 0 => unchecked((int)0xFF48D1CC),
            _ when (flags & (1 << 2)) != 0 => unchecked((int)0xFFFFA500),
            _ when (flags & (1 << 4)) != 0 => unchecked((int)0xFF32CD32),
            _ when (flags & (1 << 11)) != 0 => unchecked((int)0xFFFF0000),
            _ when (flags & (1 << 13)) != 0 => unchecked((int)0xFFFF69B4),
            _ when (flags & (1 << 10)) != 0 => unchecked((int)0xFF9370DB),
            _ => unchecked((int)0xFFD3D3D3), // Local / default LightGray
        };
    }

    static string ChatChannelLabel(ushort flags) => flags switch
    {
        1 << 0 => "Local",
        1 << 1 => "Whisper",
        1 << 2 => "Server",
        1 << 4 => "Radio",
        1 << 5 => "LOOC",
        1 << 6 => "OOC",
        1 << 9 => "Emote",
        1 << 10 => "Dead",
        1 << 11 => "Admin",
        1 << 13 => "AHelp",
        _ => "Chat",
    };

    static string StripMarkup(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        // SS14 FormattedMessage tags: [color=#fff], [/color], [bold], …
        return System.Text.RegularExpressions.Regex.Replace(s, @"\[/?[^\]]+\]", "");
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
            TryApplyPendingMapStrings();
            if (_serializer is { HasMappedStrings: true } boot && UserId is { } localId)
            {
                if (_worldCache.Apply(
                        boot.Serializer, payload, localId,
                        out var eye, out var world, out var tick, out var err))
                {
                    LastEye = eye;
                    SyncGhostFlagsFromWorld();
                    // Keep last non-empty world; never wipe sprites/tiles on empty delta blips.
                    if (world is not null
                        && (world.Entities.Count > 0 || (world.Tiles?.Count ?? 0) > 0))
                        LastWorld = world;
                    else if (LastWorld is null && world is not null)
                        LastWorld = world;
                    LastEyeHint = eye!.Detail;
                    // PC eye alignment: camera follows parent grid + InputMover relative
                    // rotation, never the ghost/entity facing that changes during movement.
                    CamX = eye.LocalPosition.X * 32f + eye.EyeOffset.X * 32f + _panOffX;
                    CamY = eye.LocalPosition.Y * 32f + eye.EyeOffset.Y * 32f + _panOffY;
                    EyeWorldRotation = (float)eye.Rotation.Theta;
                    CamRotation = _worldCache.GetGridCameraRotation(eye.Controlled);

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
                    if (world is { Entities.Count: > 0 })
                        LastWorld = world;
                    LastEyeHint = err;
                    if (StatesReceived <= 8 || StatesReceived % 50 == 0)
                        Note($"GameState decode: {err}");
                    // Always ack so the server keeps sending full/delta PVS.
                    var ackTick = eye?.ToSequence.Value ?? tick.Value;
                    try
                    {
                        if (ackTick != 0)
                            SendNamed("MsgStateAck", NetDeliveryMethod.Unreliable, m => m.Write(ackTick));
                    }
                    catch { /* ignore */ }
                }
            }
            else
            {
                if (IsObserving && (StatesReceived <= 5 || StatesReceived % 40 == 0))
                {
                    Note($"GameState decode deferred — strings={HasMappedStrings} ser={_serializer != null} phase={_mapStrPhase} pendingPkg={_pendingMapStrPackage?.Length ?? 0}");
                    if (_mapStrHash is { Length: > 0 } && _serializer is not null && !_serializer.HasMappedStrings)
                        _serializer.TryLoadCachedStrings(_mapStrHash, Note);
                }

                // Ack even without strings so server does not stall PVS forever.
                try
                {
                    // Sequence is unknown without decode — skip ack (server resends full eventually).
                }
                catch { /* ignore */ }
            }

            if (StatesReceived <= 5 || StatesReceived % 20 == 0)
                Note($"MsgState #{StatesReceived} raw={uncompressed}B z={compressed} store={_worldCache.XformCount} {LastEyeHint}");

            if (IsObserving)
                Detail = $"observing · MsgState x{StatesReceived} store={_worldCache.XformCount} {LastEyeHint}";
        }
        catch (Exception ex)
        {
            Note($"MsgState parse: {ex.Message}");
        }
    }

    bool TryApplyPendingMapStrings()
    {
        if (_pendingMapStrPackage is null || _pendingMapStrPackage.Length == 0)
            return _serializer?.HasMappedStrings == true;
        if (_serializer is null || _mapStrHash is null)
            return false;
        if (_serializer.HasMappedStrings)
        {
            _pendingMapStrPackage = null;
            return true;
        }

        if (_serializer.TrySetMappedPackage(_mapStrHash, _pendingMapStrPackage, Note))
        {
            SerializerStatus = _serializer.Status;
            _serializer.TrySaveCachedStrings(_mapStrHash, _pendingMapStrPackage, Note);
            Note($"mapstr: applied buffered package ({_pendingMapStrPackage.Length:N0}B)");
            _pendingMapStrPackage = null;
            return true;
        }

        SerializerStatus = _serializer.Status;
        if (string.IsNullOrWhiteSpace(SerializerStatus) || !SerializerStatus.Contains("SetPackage", StringComparison.Ordinal))
            SerializerStatus = $"serializer: SetPackage failed — {SerializerBootstrap.LastError ?? "unknown"}";
        return false;
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
        if (_serializer is not null)
            TryApplyPendingMapStrings();
        // Do NOT auto-send NeedsStrings here — MsgMapStrServerHandshake owns that.
        // EnsureSerializer used to race the handshake and trigger "Cannot request strings twice".
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
        TryApplyPendingMapStrings();
        if (!string.IsNullOrWhiteSpace(ContentFilesRoot))
        {
            _protoSprites.Invalidate();
            _protoSprites.EnsureLoaded(ContentFilesRoot, Note);
            _tileProtos.Invalidate();
            _tileProtos.EnsureLoaded(ContentFilesRoot, Note);
            _worldCache.SetPrototypeIndex(_protoSprites);
            _worldCache.SetTileIndex(_tileProtos);
        }

        Note($"content ready → serializer={SerializerStatus} strings={HasMappedStrings} pendingPkg={_pendingMapStrPackage?.Length ?? 0} protos={_protoSprites.Count} tiles={_tileProtos.Count}");
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
        _pendingMapStrPackage = null;
        _mapStrRequested = false;
        _mapStrPhase = MapStrPhase.None;
        _worldCache.Clear();
        _transfer.Reset();
        _sawTransferTraffic = false;
        _transferDataRx = 0;
        _lastTransferDataAt = null;
        _transferHandshakeDone = false;
        // Keep AssembliesDirectory / ContentFilesRoot across reconnects in same process.
        IsReady = false;
        IsObserving = false;
        _spawnWarpPending = false;
        _warpCyclePending = false;
        StatesReceived = 0;
        LastStateBytes = 0;
        LastEye = null;
        LastWorld = null;
        LastEyeHint = "";
        SerializerStatus = "serializer: not started";
        CamX = 0;
        CamY = 0;
        CamRotation = 0;
        EyeWorldRotation = 0;
        Zoom = 1f;
        DrawFov = false;
        DrawLighting = false;
        _lightingMode = 1;
        ShowOtherGhosts = true;
        _panOffX = 0;
        _panOffY = 0;
        _flightX = 0;
        _flightY = 0;
        LocalStatus = SessionStatus.Disconnected;
        lock (_chatGate)
        {
            _chatLines.Clear();
            _chatVersion++;
        }
        lock (_warpGate)
        {
            _ghostWarps.Clear();
            _warpVersion++;
        }
    }

    public void Dispose() => Disconnect("dispose");

    sealed record JoinRequest(string Hash, string? Hwid);
}
