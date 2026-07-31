using Port.Client.Bootstrap;
using Port.Net;

namespace Port.Client.Content;

/// <summary>
/// Loads Content.*.Client assemblies after ACZ is ready.
/// Does not run EntryPoint IoC yet — discovery + type-load validation only.
/// </summary>
public sealed class ContentClientLoadSystem : IClientSystem
{
    public ContentAssemblyHost Host { get; private set; } = new();
    public string Status { get; private set; } = "idle";
    public bool Attempted { get; set; }

    /// <summary>Probe / test hook to reuse an already-loaded host.</summary>
    public void UseHost(ContentAssemblyHost host) => Host = host;

    public Func<string?>? AssembliesDirectorySource { get; set; }
    public Action<string>? Log { get; set; }

    public void Initialize()
    {
        ContentAssemblyHost.EnsureAssemblyResolveHook();
    }

    public void FrameUpdate(float dt)
    {
        if (Attempted || !ClientFeatureFlags.LoadContentClientAssemblies)
            return;
        var dir = AssembliesDirectorySource?.Invoke();
        if (string.IsNullOrWhiteSpace(dir) || !ContentAssemblyLocator.HasDlls(dir))
            return;

        Attempted = true;
        TryLoad(dir);
    }

    public int TryLoad(string assembliesDirectory)
    {
        Attempted = true;
        var n = Host.LoadFromDirectory(assembliesDirectory);
        Status = Host.FormatReport();
        Log?.Invoke($"content.client: {Status}");
        return n;
    }

    public void Reset()
    {
        Attempted = false;
        Status = "idle";
    }

    public void Shutdown()
    {
        AssembliesDirectorySource = null;
        Log = null;
    }
}
