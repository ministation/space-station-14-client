using Robust.Shared.GameObjects;

namespace Robust.Client.GameObjects;

/// <summary>
/// SpriteSystem type shell for Content.Client. Real sprite draw stays in GLES until systems wire up.
/// </summary>
public sealed class SpriteSystem : EntitySystem
{
    public override void FrameUpdate(float frameTime) => _ = frameTime;
}
