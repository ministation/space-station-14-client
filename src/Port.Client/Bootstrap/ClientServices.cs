using Port.Content;
using Port.Net;

namespace Port.Client.Bootstrap;

/// <summary>Shared service bag for the full client (replaces MainActivity ad-hoc fields over time).</summary>
public sealed class ClientServices
{
    public required GameSessionClient Session { get; init; }
    public required PrototypeSpriteIndex Prototypes { get; init; }
    public required TilePrototypeIndex Tiles { get; init; }
    public required AczOnDemandFetcher TextureFetcher { get; init; }
    public string? ContentFilesRoot { get; set; }
    public ClientLoop Loop { get; } = new();
}
