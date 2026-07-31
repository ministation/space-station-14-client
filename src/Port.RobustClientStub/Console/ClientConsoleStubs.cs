namespace Robust.Client.Console;

public partial interface IClientConGroupImplementation
{
}

public partial interface IClientConGroupController
{
    IClientConGroupImplementation? Implementation { get; set; }
}

public sealed class NullClientConGroupController : IClientConGroupController
{
    public IClientConGroupImplementation? Implementation { get; set; }
}

public partial interface IClientConsoleHost
{
}

public sealed class NullClientConsoleHost : IClientConsoleHost
{
}
