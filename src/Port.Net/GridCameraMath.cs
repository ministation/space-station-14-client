namespace Port.Net;

/// <summary>Pure grid-camera transforms matching PC SharedMoverController relative movement.</summary>
public static class GridCameraMath
{
    public static (float X, float Y) RotateScreenInput(float x, float y, float gridCameraRotation)
    {
        var c = MathF.Cos(gridCameraRotation);
        var s = MathF.Sin(gridCameraRotation);
        return (x * c - y * s, x * s + y * c);
    }
}
