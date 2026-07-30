using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Util;
using Android.Views;
using Color = Android.Graphics.Color;
using Handler = Android.OS.Handler;

namespace Probe.AndroidHost;

/// <summary>Simple multi-layer starfield — SS14 connect-screen vibe.</summary>
public sealed class ParallaxStarfieldView : View
{
    readonly Paint _bg = new() { AntiAlias = true };
    readonly Paint _star = new() { AntiAlias = true };
    readonly Paint _nebula = new() { AntiAlias = true };
    readonly Random _rng = new(42);
    Star[] _stars = Array.Empty<Star>();
    float _t;
    bool _running;
    readonly Handler _handler;
    Action? _tick;

    struct Star
    {
        public float X, Y, Z, Size;
        public int Argb;
    }

    public ParallaxStarfieldView(Context context) : base(context)
    {
        _handler = new Handler(context.MainLooper!);
        InitPaints();
    }

    public ParallaxStarfieldView(Context context, IAttributeSet? attrs) : base(context, attrs)
    {
        _handler = new Handler(context.MainLooper!);
        InitPaints();
    }

    void InitPaints()
    {
        _bg.Color = Color.ParseColor("#05070F");
        _nebula.Color = Color.Argb(40, 40, 70, 140);
    }

    protected override void OnSizeChanged(int w, int h, int oldw, int oldh)
    {
        base.OnSizeChanged(w, h, oldw, oldh);
        if (w <= 0 || h <= 0) return;
        var count = Math.Clamp(w * h / 2800, 120, 420);
        _stars = new Star[count];
        for (var i = 0; i < count; i++)
        {
            var z = 0.25f + (float)_rng.NextDouble() * 2.4f;
            var bright = (int)(120 + 135 * (1f / z));
            bright = Math.Clamp(bright, 90, 255);
            _stars[i] = new Star
            {
                X = (float)_rng.NextDouble() * w,
                Y = (float)_rng.NextDouble() * h,
                Z = z,
                Size = Math.Max(0.8f, 2.6f / z),
                Argb = Color.Argb(bright, bright, bright, Math.Min(255, bright + 20)),
            };
        }
    }

    protected override void OnAttachedToWindow()
    {
        base.OnAttachedToWindow();
        _running = true;
        _tick = () =>
        {
            if (!_running) return;
            _t += 0.016f;
            Invalidate();
            _handler.PostDelayed(_tick!, 16);
        };
        _handler.Post(_tick);
    }

    protected override void OnDetachedFromWindow()
    {
        _running = false;
        _handler.RemoveCallbacksAndMessages(null);
        base.OnDetachedFromWindow();
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        var w = Width;
        var h = Height;
        if (w <= 0 || h <= 0) return;

        canvas.DrawRect(0, 0, w, h, _bg);

        // Soft nebula blobs
        _nebula.Color = Color.Argb(28, 55, 80, 160);
        canvas.DrawCircle(w * 0.2f + MathF.Sin(_t * 0.15f) * 30, h * 0.35f, w * 0.35f, _nebula);
        _nebula.Color = Color.Argb(22, 90, 40, 120);
        canvas.DrawCircle(w * 0.75f + MathF.Cos(_t * 0.12f) * 40, h * 0.6f, w * 0.4f, _nebula);

            foreach (var i in Enumerable.Range(0, _stars.Length))
            {
                ref var s = ref _stars[i];
                var speed = 18f / s.Z;
                s.Y += speed * 0.016f;
                if (s.Y > h + 4)
                {
                    s.Y = -4;
                    s.X = (float)_rng.NextDouble() * w;
                }

                _star.Color = new Color(s.Argb);
                canvas.DrawCircle(s.X, s.Y, s.Size, _star);
            }
    }
}
