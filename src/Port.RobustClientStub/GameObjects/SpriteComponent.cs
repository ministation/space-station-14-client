namespace Robust.Client.GameObjects;

/// <summary>
/// SpriteComponent shell for Content.Client / AppearanceChangeEvent.
/// Intentionally does <em>not</em> inherit vendor <c>Component</c> — Content packs bind ACZ Shared's
/// <c>Component</c>, and <c>EntityQuery&lt;SpriteComponent&gt;</c> would otherwise fail the constraint
/// across two Component type identities. Full component wiring lands with Shared unification.
/// </summary>
public sealed class SpriteComponent
{
}
