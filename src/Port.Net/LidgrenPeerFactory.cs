using System.Net;
using Lidgren.Network;

namespace Port.Net;

static class LidgrenPeerFactory
{
    /// <summary>
    /// Peer config tuned for mobile NATs / flaky UDP (more handshake retries).
    /// Default Lidgren gives up ~15s (5 × 3s) with "no response from remote host".
    /// </summary>
    public static NetPeerConfiguration Create(string appIdentifier)
    {
        var config = new NetPeerConfiguration(appIdentifier)
        {
            AcceptIncomingConnections = false,
            EnableUPnP = false,
            AutoFlushSendQueue = true,
            ConnectionTimeout = 30f,
            // Handshake: ~25 attempts × 1s ≈ 25s of Connect packets before give-up.
            ResendHandshakeInterval = 1.0f,
            MaximumHandshakeAttempts = 25,
            Port = 0,
            LocalAddress = IPAddress.Any,
        };

        config.EnableMessageType(NetIncomingMessageType.StatusChanged);
        config.EnableMessageType(NetIncomingMessageType.ErrorMessage);
        config.EnableMessageType(NetIncomingMessageType.WarningMessage);
        config.EnableMessageType(NetIncomingMessageType.DebugMessage);
        config.EnableMessageType(NetIncomingMessageType.VerboseDebugMessage);
        return config;
    }

    public static NetPeer Start(string appIdentifier, Action<string>? note = null)
    {
        var peer = new NetPeer(Create(appIdentifier));
        peer.Start();
        try
        {
            note?.Invoke($"NetPeer bound udp/{peer.Port} local={peer.Configuration.LocalAddress}");
        }
        catch
        {
            note?.Invoke("NetPeer started");
        }

        return peer;
    }
}
