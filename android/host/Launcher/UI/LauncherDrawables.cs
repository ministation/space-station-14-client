using Android.Graphics;
using Android.Graphics.Drawables;
using Path = Android.Graphics.Path;

namespace Content.Android.Launcher.UI;

public enum AngleSide
{
    Both = 0,
    OpenLeft = 1,
    OpenRight = 2,
    OpenLeftRight = 3
}

/// <summary>
/// Official launcher buttons are rectangles with the corners cut diagonally, not rounded.
/// </summary>
public sealed class AngleDrawable : Drawable
{
    public Color Fill { get; set; }
    public AngleSide Side { get; set; }
    public float CornerSize { get; set; }

    public AngleDrawable(Color fill, float cornerSize, AngleSide side = AngleSide.Both)
    {
        Fill = fill;
        CornerSize = cornerSize;
        Side = side;
    }

    public override void Draw(Canvas canvas)
    {
        var bounds = Bounds;
        if (bounds.Width() <= 0 || bounds.Height() <= 0)
            return;

        var c = System.Math.Min(CornerSize, System.Math.Min(bounds.Width(), bounds.Height()) / 2f);
        var left = (float)bounds.Left;
        var top = (float)bounds.Top;
        var right = (float)bounds.Right;
        var bottom = (float)bounds.Bottom;

        using var path = new Path();
        path.MoveTo(left, top);
        if (Side is AngleSide.OpenRight or AngleSide.OpenLeftRight)
        {
            path.LineTo(right, top);
        }
        else
        {
            path.LineTo(right - c, top);
            path.LineTo(right, top + c);
        }

        path.LineTo(right, bottom);
        if (Side is AngleSide.OpenLeft or AngleSide.OpenLeftRight)
        {
            path.LineTo(left, bottom);
        }
        else
        {
            path.LineTo(left + c, bottom);
            path.LineTo(left, bottom - c);
        }

        path.Close();

        using var paint = new Paint(PaintFlags.AntiAlias);
        var argb = Fill.ToArgb();
        paint.SetARGB((argb >> 24) & 0xFF, (argb >> 16) & 0xFF, (argb >> 8) & 0xFF, argb & 0xFF);
        paint.SetStyle(Paint.Style.Fill);
        canvas.DrawPath(path, paint);
    }

    public override void SetAlpha(int alpha)
    {
    }

    public override void SetColorFilter(ColorFilter? colorFilter)
    {
    }

    public override int Opacity => (int)Format.Translucent;
}

/// <summary>
/// The striped background the official launcher uses behind tabs and the login footer: 32x32 tile.
/// </summary>
public sealed class StripedBackgroundDrawable : Drawable
{
    private readonly BitmapShader _shader;
    private readonly Paint _paint = new(PaintFlags.AntiAlias);

    public StripedBackgroundDrawable(float density)
    {
        var size = System.Math.Max(16, (int)(32 * density));
        var bitmap = Bitmap.CreateBitmap(size, size, Bitmap.Config.Argb8888!)!;
        using (var canvas = new Canvas(bitmap))
        {
            var k = size / 32f;
            using var background = new Paint(PaintFlags.AntiAlias);
            background.SetARGB(255, 0x1E, 0x1E, 0x22);
            canvas.DrawRect(0, 0, size, size, background);

            using var stripe = new Paint(PaintFlags.AntiAlias);
            stripe.SetARGB(255, 0x26, 0x26, 0x26);

            using var pathA = new Path();
            pathA.MoveTo(0, 8 * k);
            pathA.LineTo(24 * k, 32 * k);
            pathA.LineTo(8 * k, 32 * k);
            pathA.LineTo(0, 24 * k);
            pathA.Close();
            canvas.DrawPath(pathA, stripe);

            using var pathB = new Path();
            pathB.MoveTo(8 * k, 0);
            pathB.LineTo(24 * k, 0);
            pathB.LineTo(32 * k, 8 * k);
            pathB.LineTo(32 * k, 24 * k);
            pathB.Close();
            canvas.DrawPath(pathB, stripe);
        }

        _shader = new BitmapShader(bitmap, Shader.TileMode.Repeat!, Shader.TileMode.Repeat!);
        _paint.SetShader(_shader);
    }

    public override void Draw(Canvas canvas)
    {
        canvas.DrawRect(Bounds, _paint);
    }

    public override void SetAlpha(int alpha)
    {
    }

    public override void SetColorFilter(ColorFilter? colorFilter)
    {
    }

    public override int Opacity => (int)Format.Opaque;
}