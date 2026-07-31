using System.Numerics;
using Port.Client.Bootstrap;
using Robust.Client.Graphics;

namespace Port.Client.Rendering;

/// <summary>
/// Syncs Robust eye + Clyde world view each tick. GameSession camera remains source of truth
/// until a full EyeSystem port lands.
/// </summary>
public sealed class ClydeRenderSystem : IClientSystem
{
    public IClydeWorldView WorldView { get; set; }
    public IClyde Clyde { get; set; }
    public IEyeManager Eyes { get; }

    /// <summary>Optional camera source (session CamX/CamY/Zoom).</summary>
    public Func<(float X, float Y, float Rot, float Zoom)>? CameraSource { get; set; }

    public ClydeRenderSystem(
        IClydeWorldView? worldView = null,
        IClyde? clyde = null,
        IEyeManager? eyes = null)
    {
        WorldView = worldView ?? new NullClydeWorldView();
        Clyde = clyde ?? new NullClyde();
        Eyes = eyes ?? new EyeManager();
    }

    public void AttachBackend(IClydeWorldView worldView, IClyde? clyde = null)
    {
        WorldView = worldView ?? throw new ArgumentNullException(nameof(worldView));
        if (clyde is not null)
            Clyde = clyde;
    }

    public void Initialize()
    {
        Eyes.CurrentEye ??= new Eye();
    }

    public void FrameUpdate(float dt)
    {
        if (CameraSource is { } src)
        {
            var (x, y, rot, zoom) = src();
            WorldView.Camera = new Vector2(x, y);
            WorldView.CameraRotation = rot;
            WorldView.Zoom = zoom;
            if (Eyes.CurrentEye is { } eye)
            {
                eye.Position = new Vector2(x, y);
                eye.Zoom = zoom;
            }
        }

        WorldView.FrameProcess(dt);
        Clyde.FrameProcess(dt);
    }

    public void Shutdown()
    {
        CameraSource = null;
    }
}
