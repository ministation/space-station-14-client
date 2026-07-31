using Robust.Shared.GameObjects;

namespace Robust.Client.GameObjects;

/// <summary>
/// SpriteComponent shell for Content.Client / AppearanceChangeEvent.
/// Must inherit the same <c>Component</c> identity as the Shared bound in the load ALC
/// (content-bind stub or content-bin Robust.Client — never mix vendor Shared with ACZ Shared).
/// </summary>
public sealed class SpriteComponent : Component
{
}
