using System.Reflection;
using System.Runtime.Loader;
using Robust.Client;

namespace Port.Client.Content;

/// <summary>
/// Isolated ALC so ACZ/content-bin <c>Robust.Shared</c> wins over the app's vendor Shared.
/// Default ALC fallback otherwise binds Content.Shared to the wrong API surface (e.g. IComponentDelta).
/// </summary>
public sealed class ContentLoadContext : AssemblyLoadContext
{
    readonly string _directory;
    readonly Assembly _clientStub;
    readonly Assembly _serverStub;

    public ContentLoadContext(string directory, bool isCollectible = true)
        : base("content-host", isCollectible)
    {
        _directory = Path.GetFullPath(directory);
        _clientStub = typeof(MobileClientStub).Assembly;
        _serverStub = typeof(Robust.Server.MobileServerStub).Assembly;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is null)
            return null;

        if (assemblyName.Name.Equals("Robust.Client", StringComparison.OrdinalIgnoreCase))
            return _clientStub;

        if (assemblyName.Name.Equals("Robust.Server", StringComparison.OrdinalIgnoreCase))
            return _serverStub;

        var local = Path.Combine(_directory, assemblyName.Name + ".dll");
        if (File.Exists(local))
            return LoadFromAssemblyPath(local);

        return null;
    }
}
