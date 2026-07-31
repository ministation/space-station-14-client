using Robust.Shared.GameObjects;

namespace Robust.Client.GameObjects;

public sealed class AnimationPlayerComponent : Component
{
    public bool HasPlayingAnimation { get; set; }
}

public sealed class AnimationPlayerSystem : EntitySystem
{
    public override void FrameUpdate(float frameTime) => _ = frameTime;
}

public sealed class AnimationCompletedEvent : EntityEventArgs
{
    public string Key { get; set; } = "";
}
