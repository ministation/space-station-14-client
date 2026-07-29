namespace Port.Platform.Android;

public enum TouchActionKind
{
    Down,
    Move,
    Up,
    Cancel,
}

public sealed record TouchSample(
    TouchActionKind Action,
    float X,
    float Y,
    long TimestampMs,
    int PointerId);

/// <summary>
/// Touch event queue. Future: map into Robust.Client.Input mouse/pointer events.
/// </summary>
public sealed class AndroidTouchQueue
{
    readonly object _gate = new();
    readonly Queue<TouchSample> _queue = new();
    TouchSample? _last;

    public int Count { get; private set; }

    public void Push(TouchSample sample)
    {
        lock (_gate)
        {
            _last = sample;
            Count++;
            _queue.Enqueue(sample);
            while (_queue.Count > 64)
                _queue.Dequeue();
        }
    }

    public TouchSample? LastOrDefault()
    {
        lock (_gate)
            return _last;
    }

    public int Drain(Span<TouchSample> buffer)
    {
        lock (_gate)
        {
            var n = 0;
            while (n < buffer.Length && _queue.Count > 0)
            {
                buffer[n++] = _queue.Dequeue();
            }
            return n;
        }
    }
}
