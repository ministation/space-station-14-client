namespace Robust.Client.Graphics;

public abstract class Overlay
{
    public virtual void FrameUpdate(float frameTime) => _ = frameTime;
}

public interface IOverlayManager
{
    void AddOverlay(Overlay overlay);
    bool RemoveOverlay(Overlay overlay);
    bool RemoveOverlay<T>() where T : Overlay;
}

public sealed class OverlayManager : IOverlayManager
{
    readonly List<Overlay> _overlays = new();

    public IReadOnlyList<Overlay> Overlays => _overlays;

    public void AddOverlay(Overlay overlay)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        if (!_overlays.Contains(overlay))
            _overlays.Add(overlay);
    }

    public bool RemoveOverlay(Overlay overlay) => _overlays.Remove(overlay);

    public bool RemoveOverlay<T>() where T : Overlay
    {
        for (var i = _overlays.Count - 1; i >= 0; i--)
        {
            if (_overlays[i] is T)
            {
                _overlays.RemoveAt(i);
                return true;
            }
        }

        return false;
    }
}
