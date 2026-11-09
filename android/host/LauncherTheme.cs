using System.Net.Http;
using System.Text.Json;
using global::Android.Graphics;
using global::Android.Views;
using global::Android.Widget;

namespace Content.Android;

/// <summary>
/// Official SS14.Launcher theme helpers (space-wizards/SS14.Launcher, MIT).
/// Palette mirrors SS14.Launcher/Theme/ThemeResources.xaml 1:1.
/// </summary>
public static class LauncherTheme
{
    public static readonly Color Bg = Parse("#25252A");
    public static readonly Color Popup = Parse("#202025");
    public static readonly Color Fg = Parse("#EEEEEE");
    public static readonly Color SubText = Parse("#AAAAAA");
    public static readonly Color ControlMid = Parse("#464966");
    public static readonly Color NanoGold = Parse("#ADA24B");

    static Color Parse(string hex) => new((int)(uint)Convert.ToUInt32(hex.TrimStart('#'), 16));

    // ConnectingOverlay random messages, fetched from the official launcher-data
    // info.json (same source as the desktop launcher); local quips as fallback.
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

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };
    static string[]? _remoteMessages;
    static bool _remoteTried;

    public static async Task<string> GetRandomMessageAsync()
    {
        if (!_remoteTried)
        {
            _remoteTried = true;
            foreach (var url in new[]
            {
                "https://launcher-data.cdn.spacestation14.com/info.json",
                "https://launcher-data.fallback.cdn.spacestation14.com/info.json",
            })
            {
                try
                {
                    using var resp = await Http.GetAsync(url);
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

        var pool = _remoteMessages is { Length: > 0 } ? _remoteMessages : FallbackMessages;
        return pool[Random.Shared.Next(pool.Length)];
    }

    /// <summary>Decode the embedded official logo (logo-long.png).</summary>
    public static Bitmap? LoadEmbeddedLogo()
    {
        try
        {
            var asm = typeof(LauncherTheme).Assembly;
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("logo-long.png", StringComparison.OrdinalIgnoreCase));
            if (name is null)
                return null;
            using var stream = asm.GetManifestResourceStream(name);
            if (stream is null)
                return null;
            return BitmapFactory.DecodeStream(stream);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Thin gold separator line (GoldLineHorizontal).</summary>
    public static View GoldLine(global::Android.Content.Context ctx)
    {
        var v = new View(ctx);
        v.SetBackgroundColor(NanoGold);
        var density = ctx.Resources?.DisplayMetrics?.Density ?? 1f;
        v.LayoutParameters = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Math.Max(1, (int)(2 * density)));
        return v;
    }
}
