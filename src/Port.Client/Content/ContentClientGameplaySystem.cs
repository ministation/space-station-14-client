using System.Reflection;
using System.Text;
using Port.Client.Bootstrap;
using Robust.Client.GameObjects;
using Robust.Shared.GameObjects;

namespace Port.Client.Content;

/// <summary>
/// After Content.*.Client loads, discovers EntitySystem / VisualizerSystem types for gameplay bootstrap.
/// Does not construct or IoC-inject systems yet.
/// </summary>
public sealed class ContentClientGameplaySystem : IClientSystem
{
    public ContentClientLoadSystem? LoadSystem { get; set; }
    public Action<string>? Log { get; set; }

    public ContentGameplayScanResult LastScan { get; private set; }
    public string Status { get; private set; } = "idle";
    bool _scanned;

    public void Initialize()
    {
    }

    public void FrameUpdate(float dt)
    {
        if (_scanned || LoadSystem is null || !LoadSystem.Attempted)
            return;
        _scanned = true;
        LastScan = ContentMetadataScan.ScanLoaded(LoadSystem.Host.Loaded);
        if (LastScan.EntitySystemCount == 0 && LastScan.VisualizerCount == 0)
            LastScan = Scan(LoadSystem.Host.Loaded);
        Status = Format(LastScan);
        Log?.Invoke($"content.gameplay: {Status}");
    }

    public ContentGameplayScanResult ScanNow()
    {
        _scanned = true;
        var asms = LoadSystem?.Host.Loaded ?? Array.Empty<Assembly>();
        // Prefer PE metadata — full GetExportedTypes often fails on Component identity skew.
        LastScan = ContentMetadataScan.ScanLoaded(asms);
        if (LastScan.EntitySystemCount == 0 && LastScan.VisualizerCount == 0)
            LastScan = Scan(asms);
        Status = Format(LastScan);
        return LastScan;
    }

    public void Reset()
    {
        _scanned = false;
        Status = "idle";
        LastScan = default;
    }

    public void Shutdown()
    {
        LoadSystem = null;
        Log = null;
    }

    public static ContentGameplayScanResult Scan(IReadOnlyList<Assembly> assemblies)
    {
        var systems = 0;
        var visualizers = 0;
        var entry = 0;
        var typeLoadFails = 0;
        string? sampleVisualizer = null;

        foreach (var asm in assemblies)
        {
            var name = asm.GetName().Name ?? "";
            if (!name.Contains("Client", StringComparison.OrdinalIgnoreCase))
                continue;

            Type[] types;
            try
            {
                types = asm.GetExportedTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                typeLoadFails += ex.LoaderExceptions.Count(e => e is not null);
                types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
            }
            catch
            {
                typeLoadFails++;
                continue;
            }

            foreach (var t in types)
            {
                if (t is not { IsClass: true, IsAbstract: false })
                    continue;

                if (typeof(Robust.Client.GameClient).IsAssignableFrom(t))
                    entry++;

                if (IsVisualizer(t))
                {
                    visualizers++;
                    sampleVisualizer ??= t.FullName;
                    continue;
                }

                if (typeof(EntitySystem).IsAssignableFrom(t) || t.Name.EndsWith("System", StringComparison.Ordinal))
                    systems++;
            }
        }

        return new ContentGameplayScanResult(systems, visualizers, entry, typeLoadFails, sampleVisualizer);
    }

    static bool IsVisualizer(Type t)
    {
        for (var cur = t.BaseType; cur is not null; cur = cur.BaseType)
        {
            if (cur.IsGenericType && cur.GetGenericTypeDefinition() == typeof(VisualizerSystem<>))
                return true;
            if (cur.Name.StartsWith("VisualizerSystem", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    static string Format(ContentGameplayScanResult r)
    {
        var sb = new StringBuilder();
        sb.Append($"systems~{r.EntitySystemCount} visualizers~{r.VisualizerCount} entry~{r.EntryPointCount}");
        if (r.TypeLoadFailures > 0)
            sb.Append($" typeFail={r.TypeLoadFailures}");
        if (r.SampleVisualizer is not null)
            sb.Append(" e.g. ").Append(r.SampleVisualizer);
        return sb.ToString();
    }
}

public readonly record struct ContentGameplayScanResult(
    int EntitySystemCount,
    int VisualizerCount,
    int EntryPointCount,
    int TypeLoadFailures,
    string? SampleVisualizer);
