using System.Diagnostics;

namespace Port.Platform.Android;

/// <summary>
/// Simple host clock. Later this maps toward IGameTiming without full engine boot.
/// </summary>
public sealed class AndroidClock
{
    readonly Stopwatch _sw = new();
    long _ticks;

    public bool IsRunning => _sw.IsRunning;
    public TimeSpan Elapsed => _sw.Elapsed;
    public long TickCount => Interlocked.Read(ref _ticks);

    public void Start()
    {
        if (!_sw.IsRunning)
            _sw.Start();
    }

    public void Stop()
    {
        if (_sw.IsRunning)
            _sw.Stop();
    }

    public long Pulse()
    {
        if (_sw.IsRunning)
            return Interlocked.Increment(ref _ticks);
        return TickCount;
    }
}
