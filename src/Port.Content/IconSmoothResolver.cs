namespace Port.Content;

/// <summary>
/// Single authoritative IconSmooth resolve path for world cache + client pipeline.
/// YAML first, then RSI meta numbered sheets; path invent only when
/// <see cref="SpriteResolveOptions.AuthoritativeOnly"/> is false.
/// Meta-proven base remap (window→rwindow) stays — that is RSI data, not path invent.
/// </summary>
public static class IconSmoothResolver
{
    public static IconSmoothData? Resolve(
        PrototypeSpriteIndex? prototypes,
        string? contentRoot,
        string? prototypeId,
        string? rsiPath,
        Action<string>? onRemapLog = null)
    {
        IconSmoothData? fromYaml = null;
        if (!string.IsNullOrWhiteSpace(prototypeId) && prototypes is not null)
            fromYaml = prototypes.TryGetIconSmooth(prototypeId);

        var inferred = string.IsNullOrWhiteSpace(rsiPath)
            ? null
            : IconSmoothInfer.FromRsi(
                contentRoot,
                rsiPath,
                prototypeId,
                allowPathHeuristic: !SpriteResolveOptions.AuthoritativeOnly);

        if (fromYaml is { } sm)
        {
            // Goob/CD: YAML base "window" vs meta "rwindow0..7".
            // Remap ONLY when YAML base has no numbered sheet and meta has the inferred base.
            if (inferred is { } inf
                && !string.IsNullOrEmpty(inf.StateBase)
                && !string.Equals(sm.StateBase, inf.StateBase, StringComparison.OrdinalIgnoreCase)
                && !IconSmoothInfer.RsiHasNumberedBase(contentRoot, rsiPath, sm.StateBase)
                && IconSmoothInfer.RsiHasNumberedBase(contentRoot, rsiPath, inf.StateBase))
            {
                var remapped = new IconSmoothData(sm.Key, inf.StateBase, sm.Mode, sm.AdditionalKeys);
                onRemapLog?.Invoke(
                    $"iconsmooth base remap {prototypeId}: '{sm.StateBase}' → '{inf.StateBase}' ({rsiPath})");
                return remapped;
            }

            return sm;
        }

        return inferred;
    }
}
