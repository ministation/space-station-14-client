namespace Port.Client.Bootstrap;

/// <summary>
/// GameController-shaped tick loop for the Android client.
/// Probe.AndroidHost should thin down to wiring this loop instead of owning game logic.
/// </summary>
public sealed class ClientLoop
{
    readonly List<IClientSystem> _systems = new();
    bool _running;

    public IReadOnlyList<IClientSystem> Systems => _systems;
    public bool IsRunning => _running;
    public long Tick { get; private set; }

    public void Add(IClientSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        _systems.Add(system);
    }

    public void Start()
    {
        if (_running) return;
        foreach (var s in _systems)
            s.Initialize();
        _running = true;
    }

    public void FrameUpdate(float dt)
    {
        if (!_running) return;
        Tick++;
        foreach (var s in _systems)
            s.FrameUpdate(dt);
    }

    public void Shutdown()
    {
        if (!_running) return;
        for (var i = _systems.Count - 1; i >= 0; i--)
            _systems[i].Shutdown();
        _running = false;
    }
}

public interface IClientSystem
{
    void Initialize();
    void FrameUpdate(float dt);
    void Shutdown();
}
