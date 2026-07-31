using Robust.Shared.GameObjects;
using Robust.Shared.IoC;

namespace Robust.Client.GameObjects;

/// <summary>
/// Appearance visualizer base used by Content.Client systems.
/// Host does not run IoC subscriptions yet — type shape only.
/// </summary>
/// <remarks>
/// Loaded via content-bind stub / content-bin Robust.Client against the same Shared as Content.*.
/// </remarks>
public abstract class VisualizerSystem<T> : EntitySystem
    where T : Component
{
    [Dependency] protected readonly AppearanceSystem AppearanceSystem = default!;
    [Dependency] protected readonly AnimationPlayerSystem AnimationSystem = default!;

    public override void Initialize()
    {
        base.Initialize();
        // Event wiring needs a live EntityManager; skip until IoC gameplay bootstrap.
    }

    protected virtual void OnAppearanceChange(EntityUid uid, T component, ref AppearanceChangeEvent args)
    {
        _ = (uid, component, args);
    }
}
