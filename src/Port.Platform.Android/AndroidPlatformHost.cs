namespace Port.Platform.Android;

/// <summary>
/// Lifecycle states mapped from Android Activity without pulling Robust.Client yet.
/// </summary>
public enum PlatformLifecycle
{
    Created,
    Started,
    Resumed,
    Paused,
    Stopped,
    Destroyed,
}

public sealed class AndroidPlatformHost
{
    readonly object _gate = new();
    readonly List<string> _log = new(64);

    public PlatformLifecycle State { get; private set; } = PlatformLifecycle.Created;
    public AndroidContentPaths Paths { get; }
    public AndroidClock Clock { get; } = new();
    public AndroidTouchQueue Touch { get; } = new();

    public AndroidPlatformHost(AndroidContentPaths paths)
    {
        Paths = paths;
        Note($"host created; files={paths.FilesDir}");
    }

    public void OnLifecycle(PlatformLifecycle next)
    {
        lock (_gate)
        {
            State = next;
            Note($"lifecycle -> {next}");
            if (next == PlatformLifecycle.Resumed)
                Clock.Start();
            if (next is PlatformLifecycle.Paused or PlatformLifecycle.Stopped or PlatformLifecycle.Destroyed)
                Clock.Stop();
        }
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(Paths.FilesDir);
        Directory.CreateDirectory(Paths.CacheDir);
        Directory.CreateDirectory(Paths.ContentDir);
        Directory.CreateDirectory(Paths.UserDataDir);
        Note("content directories ensured");
    }

    public IReadOnlyList<string> SnapshotLog(int max = 12)
    {
        lock (_gate)
        {
            if (_log.Count <= max)
                return _log.ToArray();
            return _log.Skip(_log.Count - max).ToArray();
        }
    }

    public string FormatStatus()
    {
        var touch = Touch.LastOrDefault();
        var touchLine = touch is null
            ? "touch: (none yet — tap the pad)"
            : $"touch: #{Touch.Count} {touch.Action} ({touch.X:0},{touch.Y:0}) t={touch.TimestampMs}ms";

        return string.Join('\n',
            $"platform: {State}",
            $"ticks: {Clock.TickCount}  elapsed: {Clock.Elapsed.TotalSeconds:0.0}s  running: {Clock.IsRunning}",
            touchLine,
            $"files: {Paths.FilesDir}",
            $"content: {Paths.ContentDir}",
            $"userdata: {Paths.UserDataDir}",
            "",
            "log:",
            string.Join('\n', SnapshotLog().Select(l => "  " + l)));
    }

    void Note(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
        _log.Add(line);
        if (_log.Count > 200)
            _log.RemoveRange(0, _log.Count - 150);
        global::Android.Util.Log.Info("RobustPort", line);
    }
}
