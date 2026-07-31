using Port.Content;

namespace Port.Client.Sprites;

/// <summary>
/// Single entry for sprite / IconSmooth resolution on the full client path.
/// Delegates IconSmooth to <see cref="IconSmoothResolver"/> (YAML + RSI meta).
/// </summary>
public sealed class AuthoritativeSpritePipeline
{
    readonly PrototypeSpriteIndex _prototypes;

    public AuthoritativeSpritePipeline(PrototypeSpriteIndex prototypes)
    {
        _prototypes = prototypes ?? throw new ArgumentNullException(nameof(prototypes));
    }

    public IconSmoothData? ResolveIconSmooth(string? prototypeId, string? rsiPath, string? contentRoot) =>
        IconSmoothResolver.Resolve(_prototypes, contentRoot, prototypeId, rsiPath);

    public PrototypeSpriteIndex.ResolvedSprite? ResolveSprite(string? prototypeId) =>
        string.IsNullOrWhiteSpace(prototypeId) ? null : _prototypes.TryGetResolvedSprite(prototypeId);

    public string? ResolveSpritePath(string? prototypeId) =>
        string.IsNullOrWhiteSpace(prototypeId) ? null : _prototypes.TryGetSprite(prototypeId);
}
