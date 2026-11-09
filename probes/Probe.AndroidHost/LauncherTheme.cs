using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using System.Net.Http;
using System.Text.Json;

namespace Probe.AndroidHost;

/// <summary>
/// Official SS14.Launcher look-and-feel helpers (space-wizards/SS14.Launcher, MIT).
/// Angle buttons replicate the Avalonia AngleBox pentagon: top-right and
/// bottom-left corners are cut at 45°. Palette mirrors ThemeResources.xaml.
/// </summary>
public static class LauncherTheme
{
    // ThemeResources.xaml colors.
    public static readonly Color Bg = Parse("#25252A");
    public static readonly Color Popup = Parse("#202025");
    public static readonly Color Header = Parse("#212126");
    public static readonly Color Fg = Parse("#EEEEEE");
    public static readonly Color FgMuted = Parse("#666666");
    public static readonly Color SubText = Parse("#AAAAAA");
    public static readonly Color ControlMid = Parse("#464966");
    public static readonly Color ControlHigh = Parse("#3E6C45");
    public static readonly Color ButtonHovered = Parse("#575B7F");
    public static readonly Color NanoGold = Parse("#ADA24B");
    public static readonly Color StripeEdge = Parse("#525252");
    public static readonly Color StripeA = Parse("#1E1E22");
    public static readonly Color StripeB = Parse("#262626");
    public static readonly Color TabSelected = Parse("#CC3E6C45");
    public static readonly Color TabHovered = Parse("#CC575B7F");
    public static readonly Color ListSep = Parse("#AA575B7F");
    public static readonly Color Overlay = Parse("#AA000000");
    public static readonly Color Red = Parse("#CC5555");
    public static readonly Color Green = Parse("#6B9B6E");

    static Color Parse(string hex) => new((int)(uint)Convert.ToUInt32(hex.TrimStart('#'), 16));

    /// <summary>
    /// AngleBox pentagon: (0,0) → (w-c,0) → (w,c) → (w,h) → (c,h) → (0,h-c) → close.
    /// </summary>
    sealed class AngleDrawable : Drawable
    {
        readonly Android.Graphics.Path _path = new();
        readonly Paint _paint = new(PaintFlags.AntiAlias);
        readonly float _corner;
        Color _color;

        public AngleDrawable(Color color, float corner)
        {
            _color = color;
            _corner = corner;
            _paint.SetStyle(Paint.Style.Fill);
            _paint.Color = color;
        }

        public override void Draw(Canvas canvas)
        {
            var b = Bounds;
            if (b.Width() <= 0 || b.Height() <= 0)
                return;
            var c = Math.Min(_corner, Math.Min(b.Width(), b.Height()) / 2f);
            _path.Reset();
            _path.MoveTo(0, 0);
            _path.LineTo(b.Width() - c, 0);
            _path.LineTo(b.Width(), c);
            _path.LineTo(b.Width(), b.Height());
            _path.LineTo(c, b.Height());
            _path.LineTo(0, b.Height() - c);
            _path.Close();
            _paint.Color = _color;
            canvas.DrawPath(_path, _paint);
        }

        public void SetColor(Color color)
        {
            _color = color;
            InvalidateSelf();
        }

        public override void SetAlpha(int alpha) => _paint.Alpha = alpha;
        public override void SetColorFilter(ColorFilter? cf) => _paint.SetColorFilter(cf);
#pragma warning disable CS0672 // Overrides of obsolete members are fine on all supported APIs.
        public override int Opacity => (int)Format.Translucent;
#pragma warning restore CS0672
    }

    /// <summary>Give a button the SS14.Launcher angle shape + hover/pressed colors.</summary>
    public static void StyleAngleButton(Button btn, float cornerDip = 8f)
    {
        var density = Application.Context.Resources?.DisplayMetrics?.Density ?? 1f;
        var corner = cornerDip * density;
        btn.Background = MakeAngleStateList(corner);
        btn.SetTextColor(Fg);
        btn.SetAllCaps(false);
        btn.StateListAnimator = null;
        // Keep the pentagon visible around default Material padding.
        btn.SetPadding(
            (int)(14 * density), btn.PaddingTop,
            (int)(14 * density), btn.PaddingBottom);
    }

    static StateListDrawable MakeAngleStateList(float corner)
    {
        var sl = new StateListDrawable();
        sl.AddState(new[] { Android.Resource.Attribute.StatePressed }, new AngleDrawable(ControlHigh, corner));
        sl.AddState(new[] { Android.Resource.Attribute.StateHover }, new AngleDrawable(ButtonHovered, corner));
        sl.AddState(new[] { Android.Resource.Attribute.StateFocused }, new AngleDrawable(ButtonHovered, corner));
        sl.AddState(new int[0], new AngleDrawable(ControlMid, corner));
        return sl;
    }

    /// <summary>Style every button under <paramref name="root"/>.</summary>
    public static void StyleButtons(View root)
    {
        if (root is Button b)
            StyleAngleButton(b);
        if (root is ViewGroup vg)
            for (var i = 0; i < vg.ChildCount; i++)
                StyleButtons(vg.GetChildAt(i));
    }

    // ---- ConnectingOverlay random messages (RandomMessage control in the launcher).
    // Fetched live from the official launcher-data info.json; local quips as fallback.
    static readonly string[] FallbackMessages =
    [
        "Космос — это пустота. Как и мой холодильник.",
        "ИИ уже наблюдает за вами. Он просто молчит.",
        "Клоун на мостике. Опять.",
        "Не стой под разгерметизацией. Серьёзно.",
        "Сингулярность голодная. Не кормите её ассистентами.",
        "Травматолог уже в пути. Возможно.",
        "Учёные что-то взорвали. Ничего нового.",
        "Ксеноботаник сказал: «это точно безопасно».",
        "Загрузка атмосферной катастрофы… шутка. Наверное.",
        "Не забудьте про противогаз. И про ноги.",
    ];

    internal static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    static readonly HttpClient InfoHttp = new() { Timeout = TimeSpan.FromSeconds(6) };
    static string[]? _remoteMessages;
    static bool _remoteTried;

    public static async Task<string> GetRandomMessageAsync()
    {
        if (!_remoteTried)
        {
            _remoteTried = true;
            try
            {
                // Same source the official launcher uses (ConfigConstants.UrlLauncherInfo).
                foreach (var url in new[]
                {
                    "https://launcher-data.cdn.spacestation14.com/info.json",
                    "https://launcher-data.fallback.cdn.spacestation14.com/info.json",
                })
                {
                    try
                    {
                        using var resp = await InfoHttp.GetAsync(url);
                        if (!resp.IsSuccessStatusCode)
                            continue;
                        await using var stream = await resp.Content.ReadAsStreamAsync();
                        using var doc = await JsonDocument.ParseAsync(stream);
                        if (doc.RootElement.ValueKind == JsonValueKind.Object
                            && doc.RootElement.TryGetProperty("messages", out var msgs)
                            && msgs.ValueKind == JsonValueKind.Object
                            && msgs.TryGetProperty("en-US", out var en)
                            && en.ValueKind == JsonValueKind.Array)
                        {
                            var list = en.EnumerateArray()
                                .Where(v => v.ValueKind == JsonValueKind.String)
                                .Select(v => v.GetString())
                                .Where(s => !string.IsNullOrWhiteSpace(s))
                                .Cast<string>()
                                .Take(200)
                                .ToArray();
                            if (list.Length > 0)
                            {
                                _remoteMessages = list;
                                break;
                            }
                        }
                    }
                    catch { /* try next */ }
                }
            }
            catch { /* offline */ }
        }

        var pool = _remoteMessages is { Length: > 0 } ? _remoteMessages : FallbackMessages;
        return pool[Random.Shared.Next(pool.Length)];
    }

    /// <summary>
    /// Direct-connect / add-favorite dialog (DirectConnectDialog + AddFavoriteDialog).
    /// Returns (name, address) or null.
    /// </summary>
    public static async Task<(string Name, string Address)?> ShowAddressDialogAsync(
        Activity activity, string title, bool withName, string? presetName = null, string? presetAddress = null)
    {
        var tcs = new TaskCompletionSource<(string?, string?)>();
        var density = Application.Context.Resources?.DisplayMetrics?.Density ?? 1f;
        var pad = (int)(12 * density);

        var layout = new LinearLayout(activity)
        {
            Orientation = Orientation.Vertical,
        };
        layout.SetBackgroundColor(Popup);
        layout.SetPadding(pad, pad, pad, 0);

        EditText? nameBox = null;
        if (withName)
        {
            nameBox = new EditText(activity)
            {
                Hint = "Название сервера",
                InputType = Android.Text.InputTypes.ClassText,
            };
            nameBox.SetSingleLine(true);
            nameBox.Text = presetName ?? "";
            StyleInput(nameBox);
            layout.AddView(nameBox, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, (int)(44 * density))
            { BottomMargin = (int)(8 * density) });
        }

        var addrBox = new EditText(activity)
        {
            Hint = "ss14://host:port",
            InputType = Android.Text.InputTypes.ClassText | Android.Text.InputTypes.TextVariationUri,
        };
        addrBox.SetSingleLine(true);
        addrBox.Text = presetAddress ?? "";
        addrBox.Typeface = Typeface.MonoSpace;
        StyleInput(addrBox);
        layout.AddView(addrBox, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, (int)(44 * density)));

        var dialog = new AlertDialog.Builder(activity)
            .SetTitle(title)
            .SetView(layout)
            .SetPositiveButton("ОК", (s, e) => tcs.TrySetResult((nameBox?.Text?.Trim(), addrBox.Text?.Trim())))
            .SetNegativeButton("Отмена", (s, e) => tcs.TrySetResult(null))
            .Create();

        dialog.Show();
        // Dark popup palette.
        dialog.Window?.SetBackgroundDrawable(new ColorDrawable(Popup));
        dialog.GetButton((int)DialogButtonType.Positive)?.SetTextColor(Fg);
        dialog.GetButton((int)DialogButtonType.Negative)?.SetTextColor(SubText);

        var result = await tcs.Task;
        if (result is null || string.IsNullOrWhiteSpace(result.Item2))
            return null;
        return (string.IsNullOrWhiteSpace(result.Item1) ? null : result.Item1, result.Item2);
    }

    static void StyleInput(EditText box)
    {
        box.SetTextColor(Fg);
        box.SetHintTextColor(FgMuted);
        box.SetBackgroundResource(Resource.Drawable.la_input);
    }

    /// <summary>Open a URL in the external browser (Discord/website/account links).</summary>
    public static void OpenUrl(Activity activity, string url)
    {
        try
        {
            var intent = new Android.Content.Intent(Android.Content.Intent.ActionView,
                Android.Net.Uri.Parse(url));
            intent.AddFlags(Android.Content.ActivityFlags.NewTask);
            activity.StartActivity(intent);
        }
        catch (Exception ex)
        {
            Toast.MakeText(activity, $"Не удалось открыть ссылку: {ex.Message}", ToastLength.Short)?.Show();
        }
    }

    // Official launcher links (ConfigConstants.cs).
    public const string DiscordUrl = "https://discord.ss14.io/";
    public const string WebsiteUrl = "https://spacestation14.com";
    public const string NewsFeedUrl = "https://spacestation14.com/post/index.xml";
    public const string AccountBaseUrl = "https://account.spacestation14.com/Identity/Account/";
    public const string AccountRegisterUrl = AccountBaseUrl + "Register";
    public const string AccountForgotPasswordUrl = AccountBaseUrl + "ForgotPassword";
    public const string AccountResendConfirmationUrl = AccountBaseUrl + "ResendEmailConfirmation";
    public const string AccountManageUrl = AccountBaseUrl + "Manage";
}
