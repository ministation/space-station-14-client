using System.Buffers.Binary;
using System.Text;
using Lidgren.Network;

namespace Port.Net;

/// <summary>
/// Minimal transfer receiver (Lidgren + WebSocket framing).
/// Server will not send MsgPlayerList until NetworkResourceAckMessage (Key=1)
/// acknowledges TransferKeyNetworkDownload.
/// </summary>
public sealed class PortTransferReceiver
{
    public const string TransferKeyNetworkDownload = "TransferKeyNetworkDownload";
    public const int AckInitial = 1;
    public const int MaxChunk = 16384;

    [Flags]
    enum TransferFlags : byte
    {
        None = 0,
        Start = 1 << 0,
        Finish = 1 << 1,
        HasData = 1 << 2,
    }

    (TransferFlags Flags, long Id, string? Key)? _pendingLidgren;
    TransferFlags _wsFlags;
    long _wsId;
    string? _wsKey;
    bool _wsAwaitingData;

    MemoryStream? _data;
    string? _activeKey;
    int _ackKey = AckInitial;
    bool _downloadFinished;
    bool _ackSent;

    public bool DownloadFinished => _downloadFinished;
    public bool AckSent => _ackSent;
    public int LastAckKey => _ackKey;
    public int Chunks { get; private set; }

    public void Reset()
    {
        _pendingLidgren = null;
        _wsAwaitingData = false;
        _data?.Dispose();
        _data = null;
        _activeKey = null;
        _downloadFinished = false;
        _ackSent = false;
        _ackKey = AckInitial;
        Chunks = 0;
    }

    public void ReadMsgTransferData(NetIncomingMessage msg, Action<string>? log = null)
    {
        var len = msg.ReadVariableInt32();
        if (len is < 0 or > MaxChunk + 256)
            return;
        OnLidgrenPayload(msg.ReadBytes(len), log);
    }

    /// <summary>Lidgren: alternating header MsgTransferData / data MsgTransferData.</summary>
    public void OnLidgrenPayload(byte[] payload, Action<string>? log = null)
    {
        Chunks++;
        if (payload.Length < 2)
            return;

        if (_pendingLidgren is null)
        {
            if (!TryParseHeader(payload, out var flags, out var id, out var key))
                return;

            OnHeaderStart(flags, key);

            if ((flags & TransferFlags.HasData) == 0)
            {
                if ((flags & TransferFlags.Finish) != 0)
                    Complete(_activeKey ?? key, log);
                return;
            }

            _pendingLidgren = (flags, id, key ?? _activeKey);
            return;
        }

        var (pFlags, _, pKey) = _pendingLidgren.Value;
        AppendData(payload);
        _pendingLidgren = null;

        if ((pFlags & TransferFlags.Finish) != 0)
            Complete(pKey ?? _activeKey, log);
    }

    /// <summary>WebSocket: one binary message = header; if HasData, next message(s) = data.</summary>
    public void OnWebSocketMessage(byte[] payload, Action<string>? log = null)
    {
        Chunks++;
        if (_wsAwaitingData)
        {
            AppendData(payload);
            _wsAwaitingData = false;
            if ((_wsFlags & TransferFlags.Finish) != 0)
                Complete(_wsKey ?? _activeKey, log);
            return;
        }

        if (!TryParseHeader(payload, out var flags, out var id, out var key))
            return;

        OnHeaderStart(flags, key);
        _wsFlags = flags;
        _wsId = id;
        _wsKey = key ?? _activeKey;

        if ((flags & TransferFlags.HasData) != 0)
        {
            _wsAwaitingData = true;
            return;
        }

        if ((flags & TransferFlags.Finish) != 0)
            Complete(_activeKey ?? key, log);
    }

    static bool TryParseHeader(byte[] payload, out TransferFlags flags, out long id, out string? key)
    {
        flags = default;
        id = 0;
        key = null;
        if (payload.Length < 10)
            return false;
        flags = (TransferFlags)payload[1];
        id = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(2, 8));
        if ((flags & TransferFlags.Start) != 0 && payload.Length > 11)
        {
            var keyLen = payload[10];
            if (11 + keyLen <= payload.Length)
                key = Encoding.UTF8.GetString(payload, 11, keyLen);
        }

        return true;
    }

    void OnHeaderStart(TransferFlags flags, string? key)
    {
        if ((flags & TransferFlags.Start) == 0)
            return;
        _activeKey = key;
        _data?.Dispose();
        _data = new MemoryStream();
    }

    void AppendData(byte[] payload)
    {
        _data ??= new MemoryStream();
        _data.Write(payload, 0, payload.Length);
    }

    void Complete(string? key, Action<string>? log)
    {
        try
        {
            var isDownload = string.Equals(key, TransferKeyNetworkDownload, StringComparison.Ordinal)
                             || (key is null && string.Equals(_activeKey, TransferKeyNetworkDownload, StringComparison.Ordinal));

            if (isDownload || (key is null && _data is { Length: >= 4 }))
            {
                _ackKey = AckInitial;
                if (_data is { Length: >= 4 })
                {
                    _data.Position = 0;
                    Span<byte> four = stackalloc byte[4];
                    var read = _data.Read(four);
                    if (read == 4)
                    {
                        var k = BinaryPrimitives.ReadInt32LittleEndian(four);
                        if (k != 0)
                            _ackKey = k;
                    }
                }

                _downloadFinished = true;
                log?.Invoke(
                    $"transfer: download complete key={key ?? _activeKey ?? "?"} ackKey={_ackKey} bytes={_data?.Length ?? 0}");
            }
            else if (!string.IsNullOrEmpty(key))
            {
                log?.Invoke($"transfer: finished other key={key} bytes={_data?.Length ?? 0}");
            }
        }
        finally
        {
            _data?.Dispose();
            _data = null;
            _pendingLidgren = null;
            _wsAwaitingData = false;
        }
    }

    /// <summary>Force-ready ACK if traffic went quiet without a clean Finish (best-effort).</summary>
    public void ForceFinishForAck(Action<string>? log = null)
    {
        if (_downloadFinished)
            return;
        if (_data is { Length: >= 4 } || Chunks > 0)
        {
            _ackKey = AckInitial;
            if (_data is { Length: >= 4 })
            {
                _data.Position = 0;
                Span<byte> four = stackalloc byte[4];
                if (_data.Read(four) == 4)
                {
                    var k = BinaryPrimitives.ReadInt32LittleEndian(four);
                    if (k != 0)
                        _ackKey = k;
                }
            }

            _downloadFinished = true;
            log?.Invoke($"transfer: force-finish for ack key={_ackKey} chunks={Chunks} bytes={_data?.Length ?? 0}");
        }
    }

    public void MarkAckSent() => _ackSent = true;
}
