using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lidgren.Network;
using Robust.Shared.Network;
using SpaceWizards.Sodium;

namespace Port.Net;

public enum HandshakeProbePhase
{
    Idle,
    Connecting,
    GuestAttempt,
    AwaitingServerReply,
    AwaitingAuthServer,
    SendingEncryptionResponse,
    LoginSuccess,
    Failed,
    Skipped,
}

public sealed record HandshakeProbeResult(
    HandshakeProbePhase Phase,
    string Detail,
    string? UserName = null,
    Guid? UserId = null,
    LoginType? LoginType = null,
    TimeSpan Elapsed = default);

public sealed class RobustHandshakeProbe
{
    readonly object _gate = new();
    readonly List<string> _log = new();

    public HandshakeProbePhase Phase { get; private set; } = HandshakeProbePhase.Idle;
    public string Detail { get; private set; } = "idle";
    public string? UserName { get; private set; }
    public Guid? UserId { get; private set; }
    public LoginType? LoginType { get; private set; }

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
        sb.AppendLine($"handshake: {Phase}  {Detail}");
        if (!string.IsNullOrWhiteSpace(UserName))
            sb.AppendLine($"user: {UserName}  id={UserId}");
        if (LoginType is { } lt)
            sb.AppendLine($"loginType: {lt}");
        foreach (var line in SnapshotLog())
            sb.AppendLine("  " + line);
        return sb.ToString().TrimEnd();
    }

    public async Task<HandshakeProbeResult> RunAsync(
        GameEndpoint endpoint,
        string authMode,
        string serverPublicKey,
        AuthSessionConfig? auth,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        NetPeer? peer = null;
        try
        {
            Set(HandshakeProbePhase.Connecting, $"{endpoint.Host}:{endpoint.Port}");
            var addrs = await HostResolver.ResolveAsync(endpoint.Host, ct);
            foreach (var a in addrs)
                Note($"candidate {a} ({(HostResolver.IsPrivate(a) ? "private" : "public")})");

            Exception? last = null;
            foreach (var ip in addrs)
            {
                ct.ThrowIfCancellationRequested();
                var perTry = TimeSpan.FromSeconds(Math.Max(6, timeout.TotalSeconds / Math.Max(1, addrs.Count)));
                Note($"handshake try {ip}:{endpoint.Port}");
                try
                {
                    peer?.Shutdown("retry next ip");
                    peer = null;

                    peer = LidgrenPeerFactory.Start(endpoint.AppIdentifier, Note);

                    var conn = peer.Connect(new IPEndPoint(ip, endpoint.Port));
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    linked.CancelAfter(perTry);

                    var loginSent = false;
                    while (!linked.Token.IsCancellationRequested)
                    {
                        NetIncomingMessage? msg;
                        while ((msg = peer.ReadMessage()) != null)
                        {
                            try
                            {
                                if (msg.MessageType == NetIncomingMessageType.StatusChanged)
                                {
                                    var status = (NetConnectionStatus) msg.ReadByte();
                                    var reason = msg.ReadString();
                                    Note($"status -> {status} ({reason})");
                                    if (status == NetConnectionStatus.Disconnected)
                                    {
                                        Note(string.IsNullOrWhiteSpace(reason) ? "disconnected" : reason);
                                        goto NextIp;
                                    }
                                }
                                else if (msg.MessageType == NetIncomingMessageType.Data)
                                {
                                    return await HandleHandshakeDataAsync(
                                        peer, conn, msg, endpoint, authMode, serverPublicKey, auth, ct, sw.Elapsed);
                                }
                                else if (msg.MessageType is NetIncomingMessageType.ErrorMessage
                                         or NetIncomingMessageType.WarningMessage
                                         or NetIncomingMessageType.DebugMessage)
                                {
                                    Note($"{msg.MessageType}: {msg.ReadString()}");
                                }
                            }
                            finally
                            {
                                peer.Recycle(msg);
                            }
                        }

                        if (!loginSent && conn.Status == NetConnectionStatus.Connected)
                        {
                            var wantsAuth = auth?.HasRequiredFields == true;
                            Set(wantsAuth ? HandshakeProbePhase.AwaitingServerReply : HandshakeProbePhase.GuestAttempt,
                                wantsAuth ? "sending MsgLoginStart with auth" : "sending MsgLoginStart as guest");
                            SendLoginStart(peer, conn, auth, needPubKey: string.IsNullOrWhiteSpace(auth?.PublicKey));
                            loginSent = true;
                            // extend wait after login for auth roundtrip
                            linked.CancelAfter(TimeSpan.FromSeconds(20));
                        }

                        await Task.Delay(50, linked.Token);
                    }

                    Note($"timeout on {ip}");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    Set(HandshakeProbePhase.Skipped, "cancelled");
                    return Result(sw.Elapsed);
                }
                catch (Exception ex)
                {
                    last = ex;
                    Note($"fail {ip}: {ex.GetType().Name}: {ex.Message}");
                }

                NextIp: ;
            }

            Set(HandshakeProbePhase.Failed,
                last is null
                    ? "all candidates failed (UDP / handshake)"
                    : $"all candidates failed: {last.GetType().Name}: {last.Message}");
            return Result(sw.Elapsed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Set(HandshakeProbePhase.Skipped, "cancelled");
            return Result(sw.Elapsed);
        }
        catch (Exception ex)
        {
            Set(HandshakeProbePhase.Failed, $"{ex.GetType().Name}: {ex.Message}");
            return Result(sw.Elapsed);
        }
        finally
        {
            try { peer?.Shutdown("handshake probe done"); } catch { }
        }
    }

    async Task<HandshakeProbeResult> HandleHandshakeDataAsync(
        NetPeer peer,
        NetConnection conn,
        NetIncomingMessage msg,
        GameEndpoint endpoint,
        string authMode,
        string serverPublicKey,
        AuthSessionConfig? auth,
        CancellationToken ct,
        TimeSpan elapsedAtEntry)
    {
        var firstByteWasSuccessMarker = msg.PeekBoolean();
        if (firstByteWasSuccessMarker)
        {
            msg.ReadBoolean();
            msg.ReadPadBits();
            ParseLoginSuccess(msg);
            Set(HandshakeProbePhase.LoginSuccess, "guest/no-auth login success");
            return Result(elapsedAtEntry);
        }

        msg.ReadBoolean();
        msg.ReadPadBits();

        if (authMode.Equals("Required", StringComparison.OrdinalIgnoreCase) && (auth?.HasRequiredFields != true))
        {
            Set(HandshakeProbePhase.Failed,
                "server requires auth; create auth-session.json with ROBUST auth token/userId");
            return Result(elapsedAtEntry);
        }

        if (auth?.HasRequiredFields != true)
        {
            Set(HandshakeProbePhase.Failed, "server requested auth but no auth config was provided");
            return Result(elapsedAtEntry);
        }

        Set(HandshakeProbePhase.AwaitingAuthServer, "received MsgEncryptionRequest");
        var encReq = ReadEncryptionRequest(msg);

        // Prefer key from handshake packet when present (official client behavior).
        byte[] publicKey;
        if (encReq.PublicKey is { Length: > 0 })
        {
            publicKey = encReq.PublicKey;
            Note($"using pubkey from MsgEncryptionRequest ({publicKey.Length} bytes)");
        }
        else if (!string.IsNullOrWhiteSpace(auth.PublicKey))
        {
            publicKey = Convert.FromBase64String(auth.PublicKey);
            Note("using pubkey from auth-session.json");
        }
        else if (!string.IsNullOrWhiteSpace(serverPublicKey))
        {
            publicKey = Convert.FromBase64String(serverPublicKey);
            Note("using pubkey from /info");
        }
        else
        {
            Set(HandshakeProbePhase.Failed, "server public key unavailable");
            return Result(elapsedAtEntry);
        }

        if (publicKey.Length != CryptoBox.PublicKeyBytes)
        {
            Set(HandshakeProbePhase.Failed,
                $"invalid public key length {publicKey.Length}, expected {CryptoBox.PublicKeyBytes}");
            return Result(elapsedAtEntry);
        }

        var sharedSecret = new byte[CryptoAeadXChaCha20Poly1305Ietf.KeyBytes];
        RandomNumberGenerator.Fill(sharedSecret);
        // MUST be standard Base64 (not base64url). Auth server does Convert.FromBase64String(hash).
        var authHash = Convert.ToBase64String(MakeAuthHash(sharedSecret, publicKey));

        await JoinAuthServerAsync(auth, authHash, ct);
        Note("api/session/join OK");

        var sealedPayload = new byte[sharedSecret.Length + encReq.VerifyToken.Length];
        sharedSecret.CopyTo(sealedPayload.AsSpan());
        encReq.VerifyToken.CopyTo(sealedPayload.AsSpan(sharedSecret.Length));
        var sealedData = CryptoBox.Seal(sealedPayload, publicKey);

        Set(HandshakeProbePhase.SendingEncryptionResponse, "sending MsgEncryptionResponse");
        var outMsg = peer.CreateMessage();
        WriteEncryptionResponse(outMsg, Guid.Parse(auth.UserId), sealedData, legacyHwid: Array.Empty<byte>());
        peer.SendMessage(outMsg, conn, NetDeliveryMethod.ReliableOrdered);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromSeconds(15));
        while (!linked.Token.IsCancellationRequested)
        {
            NetIncomingMessage? next;
            while ((next = peer.ReadMessage()) != null)
            {
                try
                {
                    if (next.MessageType == NetIncomingMessageType.StatusChanged)
                    {
                        var status = (NetConnectionStatus) next.ReadByte();
                        var reason = next.ReadString();
                        Note($"status -> {status} ({reason})");
                        if (status == NetConnectionStatus.Disconnected)
                        {
                            Set(HandshakeProbePhase.Failed, string.IsNullOrWhiteSpace(reason) ? "disconnected" : reason);
                            return Result(elapsedAtEntry + TimeSpan.FromSeconds(1));
                        }
                    }
                    else if (next.MessageType == NetIncomingMessageType.Data)
                    {
                        if (!TryDecryptIncoming(next, sharedSecret))
                        {
                            Set(HandshakeProbePhase.Failed, "failed to decrypt login success");
                            return Result(elapsedAtEntry);
                        }

                        ParseLoginSuccess(next);
                        Set(HandshakeProbePhase.LoginSuccess, "authenticated login success");
                        return Result(elapsedAtEntry);
                    }
                }
                finally
                {
                    peer.Recycle(next);
                }
            }

            await Task.Delay(50, linked.Token);
        }

        Set(HandshakeProbePhase.Failed, "timeout waiting for encrypted login success");
        return Result(elapsedAtEntry);
    }

    void ParseLoginSuccess(NetIncomingMessage msg)
    {
        UserName = msg.ReadString();
        UserId = msg.ReadGuid();
        var patron = msg.ReadString();
        _ = patron;
        LoginType = (LoginType) msg.ReadByte();
        Note($"login success: {UserName} ({UserId}) type={LoginType}");
    }

    static void SendLoginStart(NetPeer peer, NetConnection conn, AuthSessionConfig? auth, bool needPubKey)
    {
        var msg = peer.CreateMessage();
        msg.Write(auth?.UserName ?? "AndroidGuest");
        msg.Write(auth?.HasRequiredFields == true);
        msg.Write(needPubKey);
        msg.Write(true);
        peer.SendMessage(msg, conn, NetDeliveryMethod.ReliableOrdered);
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

    static async Task JoinAuthServerAsync(AuthSessionConfig auth, string authHash, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var authServer = string.IsNullOrWhiteSpace(auth.AuthServer)
            ? Ss14AuthClient.DefaultAuthServer
            : auth.AuthServer;
        if (!authServer.EndsWith('/'))
            authServer += "/";

        using var req = new HttpRequestMessage(HttpMethod.Post, authServer + "api/session/join");
        req.Headers.Authorization = new AuthenticationHeaderValue("SS14Auth", auth.Token);
        // Same shape as Robust.Shared JoinRequest — JsonSerializerDefaults.Web => camelCase hash/hwid
        req.Content = JsonContent.Create(new JoinRequest(authHash, null), options: new JsonSerializerOptions(JsonSerializerDefaults.Web));
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

    static bool TryDecryptIncoming(NetIncomingMessage message, byte[] key)
    {
        if (message.LengthBytes < sizeof(ulong) + CryptoAeadXChaCha20Poly1305Ietf.AddBytes)
            return false;

        var nonce = message.ReadUInt64();
        var cipherText = message.Data.AsSpan(sizeof(ulong), message.LengthBytes - sizeof(ulong));
        var buffer = cipherText.ToArray();

        Span<byte> nonceData = stackalloc byte[CryptoAeadXChaCha20Poly1305Ietf.NoncePublicBytes];
        nonceData.Fill(0);
        BinaryPrimitives.WriteUInt64LittleEndian(nonceData, nonce);

        var result = CryptoAeadXChaCha20Poly1305Ietf.Decrypt(
            message.Data,
            out var messageLength,
            buffer,
            ReadOnlySpan<byte>.Empty,
            nonceData,
            key);

        message.Position = 0;
        message.LengthBytes = messageLength;
        return result;
    }

    void Set(HandshakeProbePhase phase, string detail)
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
            if (_log.Count > 80)
                _log.RemoveRange(0, _log.Count - 60);
        }
    }

    HandshakeProbeResult Result(TimeSpan elapsed) =>
        new(Phase, Detail, UserName, UserId, LoginType, elapsed);

    sealed record JoinRequest(string Hash, string? Hwid);
}
