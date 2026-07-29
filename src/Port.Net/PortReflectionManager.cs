using Robust.Shared.Reflection;

namespace Port.Net;

public sealed class PortReflectionManager : ReflectionManager
{
    static readonly string[] Prefixes =
    [
        "",
        "Robust.Shared.",
        "Robust.Client.",
        "Content.Shared.",
        "Content.Client.",
        "Content.Shared._RMC14.",
        "Content.Shared.Goobstation.",
    ];

    protected override IEnumerable<string> TypePrefixes => Prefixes;
}
