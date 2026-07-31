using System.Numerics;
using Port.Client.Rendering;
using Robust.Client.Graphics;
using Robust.Shared.Maths;
using Vector2 = System.Numerics.Vector2;

namespace Port.Platform.Android.Graphics;

/// <summary>
/// Clyde-shaped façade over <see cref="GlesClearRenderer"/>.
/// Keeps GLES details out of Port.Client / MainActivity game loop.
/// </summary>
public sealed class GlesClydeBackend : IClydeWorldView, IClyde
{
    readonly GlesClearRenderer _gles;

    public GlesClydeBackend(GlesClearRenderer gles)
    {
        _gles = gles ?? throw new ArgumentNullException(nameof(gles));
    }

    public GlesClearRenderer Renderer => _gles;

    public Vector2 Camera
    {
        get => new(_camX, _camY);
        set
        {
            _camX = value.X;
            _camY = value.Y;
            _gles.SetCamera(value.X, value.Y);
        }
    }

    public float CameraRotation
    {
        get => _camRot;
        set
        {
            _camRot = value;
            _gles.SetCameraRotation(value);
        }
    }

    public float Zoom
    {
        get => _zoom;
        set
        {
            _zoom = value <= 0 ? 1f : value;
            _gles.SetZoom(_zoom);
        }
    }

    public Vector2 ScreenSize
    {
        get => new(_gles.Width, _gles.Height);
    }

    Vector2i IClyde.ScreenSize => new(_gles.Width, _gles.Height);

    float _camX, _camY, _camRot, _zoom = 1f;

    public void SetContentRoot(string? root) => _gles.SetContentFilesRoot(root);

    public void SetFullbright(bool enabled) => _gles.SetFullbright(enabled);

    public void SetGhostMode(bool enabled) => _gles.SetGhostMode(enabled);

    public void FrameProcess(float dt) => _ = dt;

    public void ArmTextureLoadBurst(int durationFrames = 60) => _gles.ArmTextureLoadBurst(durationFrames);

    public void SetTextureFetcher(Port.Content.AczOnDemandFetcher? fetcher) => _gles.SetTextureFetcher(fetcher);

    public void SetEntities(GlesClearRenderer.EntitySprite[] entities, int count) =>
        _gles.SetEntities(entities, count);

    public void SetTiles(GlesClearRenderer.TileSprite[] tiles, int count) =>
        _gles.SetTiles(tiles, count);

    public void SetSpeechBubbles(GlesClearRenderer.SpeechBubbleSprite[] bubbles, int count) =>
        _gles.SetSpeechBubbles(bubbles, count);

    public float Fps => _gles.Fps;

    public string FormatDiag() => _gles.FormatDiag();
}
