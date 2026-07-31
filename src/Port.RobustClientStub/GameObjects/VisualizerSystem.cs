using Robust.Shared.GameObjects;
using Robust.Shared.IoC;

namespace Robust.Client.GameObjects;

/// <summary>
/// Appearance visualizer base used by Content.Client systems.
/// Host does not run IoC subscriptions yet — type shape only.
/// </summary>
/// <remarks>
/// Constraint is intentionally <c>class</c>, not <c>Component</c>: Content packs bind their own
/// ACZ/content-bin <c>Component</c>, while this stub is compiled against vendor Shared — a
/// <c>where T : Component</c> constraint fails type-load across those two Component identities.
/// </remarks>
public abstract class VisualizerSystem<T> : EntitySystem
    where T : class
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
