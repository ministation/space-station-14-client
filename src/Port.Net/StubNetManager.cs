using Robust.Shared.Network;

namespace Port.Net;

/// <summary>
/// Minimal INetManager so RobustMappedStringSerializer.Initialize can register handlers (no-op).
/// </summary>
sealed class StubNetManager : INetManager
{
    public bool IsServer => false;
    public bool IsClient => true;
    public bool IsRunning => true;
    public bool IsConnected => false;
    public NetworkStats Statistics => default;
    public IEnumerable<INetChannel> Channels => Array.Empty<INetChannel>();
    public int ChannelCount => 0;
    public int Port => 0;
    public IReadOnlyDictionary<Type, long> MessageBandwidthUsage { get; } = new Dictionary<Type, long>();

    public void ResetBandwidthMetrics() { }
    public void Initialize(bool isServer) { }
    public void StartServer() { }
    public void Shutdown(string reason) { }
    public void ProcessPackets() { }
    public void ServerSendToAll(NetMessage message) { }
    public void ServerSendMessage(NetMessage message, INetChannel recipient) { }
    public void ServerSendToMany(NetMessage message, List<INetChannel> recipients) { }
    public void ClientSendMessage(NetMessage message) { }

    public event Func<NetConnectingArgs, Task>? Connecting;
    public event EventHandler<NetChannelArgs>? Connected;
    public event EventHandler<NetDisconnectedArgs>? Disconnect;

    public void RegisterNetMessage<T>(ProcessMessage<T>? rxCallback = null, NetMessageAccept accept = NetMessageAccept.Both)
        where T : NetMessage, new()
    {
        // Handlers unused — Port drives mapstr/state manually.
    }

    public T CreateNetMessage<T>() where T : NetMessage, new() => new();
}
