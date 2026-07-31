namespace Robust.Client.State;

public abstract class State
{
    // Must stay protected — Content.Client overrides reduce access vs public base methods.
    protected virtual void Startup()
    {
    }

    protected virtual void Shutdown()
    {
    }

    internal void StartupInternal() => Startup();
    internal void ShutdownInternal() => Shutdown();

    public virtual void FrameUpdate(float frameTime) => _ = frameTime;
}

public interface IStateManager
{
    State? CurrentState { get; }
    void RequestStateChange<T>() where T : State, new();
    void RequestStateChange(Type stateType);
}

public sealed class StateManager : IStateManager
{
    public State? CurrentState { get; private set; }

    public void RequestStateChange<T>() where T : State, new() =>
        RequestStateChange(typeof(T));

    public void RequestStateChange(Type stateType)
    {
        CurrentState?.ShutdownInternal();
        CurrentState = (State)Activator.CreateInstance(stateType)!;
        CurrentState.StartupInternal();
    }
}
