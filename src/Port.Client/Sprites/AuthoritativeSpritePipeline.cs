using Port.Content;

namespace Port.Client.Sprites;

/// <summary>
/// Single entry for sprite / IconSmooth resolution on the full client path.
/// Prefer prototype YAML + RSI meta; heuristics are opt-in via <see cref="ClientFeatureFlags"/>.
/// </summary>
public sealed class AuthoritativeSpritePipeline
{
    readonly PrototypeSpriteIndex _prototypes;

    public AuthoritativeSpritePipeline(PrototypeSpriteIndex prototypes)
    {
        _prototypes = prototypes ?? throw new ArgumentNullException(nameof(prototypes));
    }

    public IconSmoothData? ResolveIconSmooth(string? prototypeId, string? rsiPath, string? contentRoot)
    {
        // 1) YAML wins (including partial base-only merge over parents).
        if (!string.IsNullOrWhiteSpace(prototypeId))
        {
            var fromYaml = _prototypes.TryGetIconSmooth(prototypeId);
            if (fromYaml is not null)
                return fromYaml;
        }

        if (string.IsNullOrWhiteSpace(rsiPath))
            return null;

        // 2) Meta-only numbered sheets (solid0..7) — still authoritative RSI data.
        // 3) Heuristic path invent is disabled in authoritative mode.
        return IconSmoothInfer.FromRsi(
            contentRoot,
            rsiPath,
            prototypeId,
            allowPathHeuristic: !ClientFeatureFlags.AuthoritativeSprites);
    }

    public PrototypeSpriteIndex.ResolvedSprite? ResolveSprite(string? prototypeId) =>
        string.IsNullOrWhiteSpace(prototypeId) ? null : _prototypes.TryGetResolvedSprite(prototypeId);

    public string? ResolveSpritePath(string? prototypeId) =>
        string.IsNullOrWhiteSpace(prototypeId) ? null : _prototypes.TryGetSprite(prototypeId);
}
