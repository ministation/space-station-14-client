using System.Reflection;
using System.Runtime.Loader;
using Robust.Client;

namespace Port.Client.Content;

/// <summary>
/// Isolated ALC so ACZ/content-bin <c>Robust.Shared</c> wins over the app's vendor Shared,
/// and <c>Robust.Client</c> shares that same Shared identity (content-bind / content-bin client).
/// </summary>
public sealed class ContentLoadContext : AssemblyLoadContext
{
    readonly string _directory;
    readonly Assembly _hostClientStub;
    readonly Assembly _serverStub;
    Assembly? _resolvedClient;

    // Non-collectible: IoC DispatchProxy stubs live in Port.Client (default ALC) and must
    // implement interfaces from content-bin/ACZ Shared loaded here.
    public ContentLoadContext(string directory, bool isCollectible = false)
        : base("content-host", isCollectible)
    {
        _directory = Path.GetFullPath(directory);
        _hostClientStub = typeof(MobileClientStub).Assembly;
        _serverStub = typeof(Robust.Server.MobileServerStub).Assembly;
    }

    public Assembly ClientAssembly => _resolvedClient ?? _hostClientStub;

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is null)
            return null;

        if (assemblyName.Name.Equals("Robust.Client", StringComparison.OrdinalIgnoreCase))
            return _resolvedClient ??= ContentBindStubResolver.ResolveClientAssembly(this, _directory, _hostClientStub);

        if (assemblyName.Name.Equals("Robust.Server", StringComparison.OrdinalIgnoreCase))
            return _serverStub;

        var local = Path.Combine(_directory, assemblyName.Name + ".dll");
        if (File.Exists(local))
        {
            // Never load content-bin Robust.Client via generic path — goes through resolver
            // so we can prefer ContentBind / skip broken native clients.
            if (assemblyName.Name.Equals("Robust.Client", StringComparison.OrdinalIgnoreCase))
                return _resolvedClient ??= ContentBindStubResolver.ResolveClientAssembly(this, _directory, _hostClientStub);

            return LoadFromAssemblyPath(local);
        }

        return null;
    }

    public void PrefetchClientAssembly()
    {
        _resolvedClient ??= ContentBindStubResolver.ResolveClientAssembly(this, _directory, _hostClientStub);
    }
}
