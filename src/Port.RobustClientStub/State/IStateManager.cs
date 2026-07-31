namespace Robust.Client.State;

public abstract class State
{
    public virtual void Startup()
    {
    }

    public virtual void Shutdown()
    {
    }

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
        CurrentState?.Shutdown();
        CurrentState = (State)Activator.CreateInstance(stateType)!;
        CurrentState.Startup();
    }
}
