using Android.Content;
using Android.Graphics;
using Android.Util;
using Android.Views;
using Color = Android.Graphics.Color;

namespace Probe.AndroidHost;

/// <summary>On-screen virtual stick: normalized output in [-1,1] for X/Y.</summary>
public sealed class VirtualJoystickView : View
{
    readonly Paint _basePaint = new() { AntiAlias = true, Color = new Color(40, 48, 58, 160) };
    readonly Paint _ringPaint = new() { AntiAlias = true, Color = new Color(212, 197, 169, 180), StrokeWidth = 3f };
    readonly Paint _knobPaint = new() { AntiAlias = true, Color = new Color(243, 240, 232, 220) };

    float _cx, _cy, _radius, _knobR;
    float _kx, _ky;
    bool _active;

    public float AxisX { get; private set; }
    public float AxisY { get; private set; }
    public event Action<float, float>? AxisChanged;

    public VirtualJoystickView(Context context) : base(context)
    {
        _ringPaint.SetStyle(Paint.Style.Stroke!);
        _basePaint.SetStyle(Paint.Style.Fill!);
        _knobPaint.SetStyle(Paint.Style.Fill!);
        SetWillNotDraw(false);
    }

    public VirtualJoystickView(Context context, IAttributeSet? attrs) : base(context, attrs)
    {
        _ringPaint.SetStyle(Paint.Style.Stroke!);
        _basePaint.SetStyle(Paint.Style.Fill!);
        _knobPaint.SetStyle(Paint.Style.Fill!);
        SetWillNotDraw(false);
    }

    protected override void OnSizeChanged(int w, int h, int oldw, int oldh)
    {
        base.OnSizeChanged(w, h, oldw, oldh);
        _cx = w * 0.5f;
        _cy = h * 0.5f;
        _radius = Math.Min(w, h) * 0.42f;
        _knobR = _radius * 0.38f;
        ResetKnob();
    }

    void ResetKnob()
    {
        _kx = _cx;
        _ky = _cy;
        AxisX = 0;
        AxisY = 0;
        _active = false;
        Invalidate();
        AxisChanged?.Invoke(0, 0);
    }

    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e is null) return false;
        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
            case MotionEventActions.Move:
                _active = true;
                var dx = e.GetX() - _cx;
                var dy = e.GetY() - _cy;
                var len = MathF.Sqrt(dx * dx + dy * dy);
                if (len > _radius && len > 0.001f)
                {
                    dx = dx / len * _radius;
                    dy = dy / len * _radius;
                    len = _radius;
                }

                _kx = _cx + dx;
                _ky = _cy + dy;
                AxisX = _radius > 0.001f ? dx / _radius : 0;
                // Screen Y down → world/flight Y up
                AxisY = _radius > 0.001f ? -dy / _radius : 0;
                Invalidate();
                AxisChanged?.Invoke(AxisX, AxisY);
                break;
            case MotionEventActions.Up:
            case MotionEventActions.Cancel:
                ResetKnob();
                break;
        }

        return true;
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        canvas.DrawCircle(_cx, _cy, _radius, _basePaint);
        canvas.DrawCircle(_cx, _cy, _radius, _ringPaint);
        canvas.DrawCircle(_kx, _ky, _knobR, _knobPaint);
        if (!_active)
            canvas.DrawCircle(_cx, _cy, 3f, _ringPaint);
    }
}
