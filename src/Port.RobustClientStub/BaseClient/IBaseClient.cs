namespace Robust.Client;

public enum ClientRunLevel : byte
{
    Error = 0,
    Initialize,
    Connecting,
    Connected,
    InGame,
    SinglePlayerGame,
}

public sealed class RunLevelChangedEventArgs : EventArgs
{
    public ClientRunLevel OldLevel { get; }
    public ClientRunLevel NewLevel { get; }

    public RunLevelChangedEventArgs(ClientRunLevel oldLevel, ClientRunLevel newLevel)
    {
        OldLevel = oldLevel;
        NewLevel = newLevel;
    }
}

/// <summary>IBaseClient shell — connection run-level tracking for Content.Client.</summary>
public interface IBaseClient
{
    ClientRunLevel RunLevel { get; }
    event EventHandler<RunLevelChangedEventArgs>? RunLevelChanged;
}

public sealed class BaseClient : IBaseClient
{
    ClientRunLevel _level = ClientRunLevel.Initialize;

    public ClientRunLevel RunLevel
    {
        get => _level;
        set
        {
            if (_level == value) return;
            var old = _level;
            _level = value;
            RunLevelChanged?.Invoke(this, new RunLevelChangedEventArgs(old, value));
        }
    }

    public event EventHandler<RunLevelChangedEventArgs>? RunLevelChanged;
}
