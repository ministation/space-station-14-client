using Android.Graphics;
using Android.Graphics.Drawables;

namespace Content.Android.Launcher.UI;

public static class LauncherTheme
{
    public static readonly Color Background = Color.ParseColor("#25252A");
    public static readonly Color HeaderBackground = Color.ParseColor("#212126");
    public static readonly Color PopupBackground = Color.ParseColor("#202025");
    public static readonly Color Foreground = Color.ParseColor("#EEEEEE");
    public static readonly Color ForegroundMuted = Color.ParseColor("#666666");
    public static readonly Color SubText = Color.ParseColor("#AAAAAA");
    public static readonly Color NanoGold = Color.ParseColor("#ADA24B");
    public static readonly Color ControlMid = Color.ParseColor("#464966");
    public static readonly Color ControlHigh = Color.ParseColor("#3E6C45");
    public static readonly Color ButtonHover = Color.ParseColor("#575B7F");
    public static readonly Color RowBase = Color.ParseColor("#1E1E22");
    public static readonly Color RowAlternate = Color.ParseColor("#262626");
    public static readonly Color Separator = Color.ParseColor("#2E2E35");
    public static readonly Color StripeEdge = Color.ParseColor("#525252");
    public static readonly Color TabSelected = Color.ParseColor("#CC3E6C45");
    public static readonly Color ListSeparator = Color.ParseColor("#AA575B7F");

    public static GradientDrawable CreateBox(Color color, float radiusDp = 4, Color? strokeColor = null, float strokeWidthDp = 1)
    {
        var density = global::Android.App.Application.Context.Resources?.DisplayMetrics?.Density ?? 1f;
        var gd = new GradientDrawable();
        gd.SetColor(color);
        // Official launcher look is square: diagonal cuts read as flat on a phone.
        gd.SetCornerRadius(0);
        if (strokeColor.HasValue)
        {
            gd.SetStroke((int)(strokeWidthDp * density), strokeColor.Value);
        }
        return gd;
    }
}
