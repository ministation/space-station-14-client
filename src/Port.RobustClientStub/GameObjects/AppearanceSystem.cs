using Robust.Shared.GameObjects;
using Robust.Shared.GameStates;

namespace Robust.Client.GameObjects;

/// <summary>Client AppearanceSystem shell — queues are no-ops until EntitySystem IoC runs.</summary>
public sealed class AppearanceSystem : SharedAppearanceSystem
{
    protected override void OnAppearanceGetState(
        EntityUid uid,
        AppearanceComponent component,
        ref ComponentGetState args)
    {
        _ = (uid, component, args);
    }

    public void OnChangeData(EntityUid uid, SpriteComponent? sprite = null, AppearanceComponent? appearanceComponent = null)
    {
        _ = (uid, sprite, appearanceComponent);
    }
}

/// <summary>Raised when appearance data changes (Content.Client visualizers subscribe).</summary>
[ByRefEvent]
public struct AppearanceChangeEvent
{
    public AppearanceComponent Component;
    public IReadOnlyDictionary<Enum, object> AppearanceData;
    public SpriteComponent? Sprite;
}
