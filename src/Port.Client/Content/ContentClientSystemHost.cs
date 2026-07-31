using System.Reflection;
using Port.Client.Bootstrap;

namespace Port.Client.Content;

/// <summary>
/// After full Content.Client type-load, enumerates EntitySystem types and constructs
/// those with a public parameterless ctor (no IoC inject / Initialize yet).
/// </summary>
public sealed class ContentClientSystemHost : IClientSystem
{
    public ContentClientLoadSystem? LoadSystem { get; set; }
    public Action<string>? Log { get; set; }

    public string Status { get; private set; } = "idle";
    public bool Attempted { get; private set; }
    public int ConstructedCount { get; private set; }
    public int SystemTypeCount { get; private set; }

    public void Initialize()
    {
    }

    public void FrameUpdate(float dt)
    {
        if (Attempted || LoadSystem is null || !LoadSystem.Attempted)
            return;
        if (!ClientFeatureFlags.RunContentSystemHost)
        {
            Attempted = true;
            Status = "skip flag-off";
            return;
        }
        if (!LoadSystem.Host.FullTypeLoadOk)
        {
            Attempted = true;
            Status = "skip typeload-partial";
            Log?.Invoke($"content.systems: {Status}");
            return;
        }

        Attempted = true;
        try
        {
            Status = Bootstrap();
        }
        catch (Exception ex)
        {
            Status = "systems CRASH: " + ex.GetType().Name + ": " + ex.Message;
        }
        Log?.Invoke($"content.systems: {Status}");
    }

    public string Bootstrap()
    {
        var host = LoadSystem?.Host;
        if (host is null)
            return "no host";

        var constructed = 0;
        var types = 0;
        var failures = 0;
        string? sample = null;

        foreach (var asm in host.Loaded)
        {
            var name = asm.GetName().Name ?? "";
            if (!name.Contains("Client", StringComparison.OrdinalIgnoreCase))
                continue;

            Type[] exported;
            try { exported = asm.GetExportedTypes(); }
            catch { continue; }

            foreach (var t in exported)
            {
                if (t is not { IsClass: true, IsAbstract: false })
                    continue;
                // Don't use typeof(EntitySystem) — vendor vs content-ALC Shared identities differ.
                if (!t.Name.EndsWith("System", StringComparison.Ordinal))
                    continue;
                if (t.Name.Contains("UIController", StringComparison.Ordinal))
                    continue;

                // Skip VisualizerSystem`1 open form
                if (t.IsGenericTypeDefinition)
                    continue;

                types++;
                sample ??= t.FullName;

                var ctor = t.GetConstructor(Type.EmptyTypes);
                if (ctor is null)
                    continue;

                try
                {
                    _ = ctor.Invoke(null);
                    constructed++;
                }
                catch
                {
                    failures++;
                }
            }
        }

        SystemTypeCount = types;
        ConstructedCount = constructed;
        return $"systems={types} constructed={constructed} fail={failures} e.g. {sample ?? "-"}";
    }

    public void Reset()
    {
        Attempted = false;
        Status = "idle";
        ConstructedCount = 0;
        SystemTypeCount = 0;
    }

    public void Shutdown()
    {
        LoadSystem = null;
        Log = null;
    }
}
