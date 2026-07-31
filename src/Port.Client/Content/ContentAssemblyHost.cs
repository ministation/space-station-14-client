using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Port.Net;
using Robust.Client;

namespace Port.Client.Content;

/// <summary>
/// Loads Content assemblies for the full client path (not the NetSerializer type map).
/// *.Client packs require <see cref="ClientFeatureFlags.LoadContentClientAssemblies"/>.
/// Uses an isolated ALC so content-bin/ACZ Shared is not replaced by vendor Shared.
/// </summary>
public sealed class ContentAssemblyHost
{
    static int s_resolveHooked;
    ContentLoadContext? _alc;
    readonly List<Assembly> _loaded = new();
    readonly List<string> _failures = new();

    public IReadOnlyList<Assembly> Loaded => _loaded;
    public IReadOnlyList<string> Failures => _failures;
    public string Status { get; private set; } = "idle";
    public string? AssembliesDirectory { get; private set; }

    public static void EnsureAssemblyResolveHook()
    {
        if (Interlocked.Exchange(ref s_resolveHooked, 1) == 1)
            return;

        // Default-context fallback for code that still loads Content into Default.
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            if (name.Name is null)
                return null;
            if (name.Name.Equals("Robust.Client", StringComparison.OrdinalIgnoreCase))
                return typeof(MobileClientStub).Assembly;
            if (name.Name.Equals("Robust.Server", StringComparison.OrdinalIgnoreCase))
                return typeof(Robust.Server.MobileServerStub).Assembly;
            return null;
        };
    }

    public int LoadFromDirectory(string directory)
    {
        _failures.Clear();
        _loaded.Clear();
        AssembliesDirectory = directory;
        if (!Directory.Exists(directory))
        {
            Status = $"missing dir {directory}";
            return 0;
        }

        EnsureAssemblyResolveHook();
        _alc = new ContentLoadContext(directory);

        // Shared first, then Client — reduces missing-type noise during Client load.
        var files = Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly)
            .OrderBy(p => ClientRank(Path.GetFileName(p)))
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Prefer content-bin Shared/Maths before Content packs bind types.
        foreach (var engine in new[] { "Robust.Shared.dll", "Robust.Maths.dll", "Robust.Shared.Maths.dll" })
        {
            var enginePath = Path.Combine(directory, engine);
            if (!File.Exists(enginePath))
                continue;
            try
            {
                _alc.LoadFromAssemblyPath(Path.GetFullPath(enginePath));
            }
            catch (Exception ex)
            {
                _failures.Add($"{engine}: {Flatten(ex)}");
            }
        }

        var count = 0;
        foreach (var path in files)
        {
            var name = Path.GetFileName(path);
            if (!ShouldLoad(name))
                continue;
            try
            {
                var asm = _alc.LoadFromAssemblyPath(Path.GetFullPath(path));
                _loaded.Add(asm);
                count++;
            }
            catch (Exception ex)
            {
                _failures.Add($"{name}: {Flatten(ex)}");
            }
        }

        Status = $"loaded={count} fail={_failures.Count} dir={directory}";
        return count;
    }

    public ContentClientScanResult ScanClientTypes()
    {
        string? entry = null;
        var systems = 0;
        foreach (var asm in _loaded)
        {
            var asmName = asm.GetName().Name ?? "";
            if (!asmName.Contains("Client", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var loc = asm.Location;
                if (!string.IsNullOrEmpty(loc) && File.Exists(loc))
                {
                    var meta = ContentMetadataScan.ScanAssemblyFile(loc);
                    systems += meta.EntitySystemCount + meta.VisualizerCount;
                    if (entry is null && meta.EntryPointCount > 0)
                        entry = asmName + ".EntryPoint?";
                    continue;
                }
            }
            catch (Exception ex)
            {
                _failures.Add($"scan-meta {asmName}: {Flatten(ex)}");
            }

            try
            {
                foreach (var t in asm.GetExportedTypes())
                {
                    if (entry is null
                        && typeof(GameClient).IsAssignableFrom(t)
                        && !t.IsAbstract)
                        entry = t.FullName;
                    if (t.Name.EndsWith("System", StringComparison.Ordinal)
                        && t is { IsClass: true, IsAbstract: false })
                        systems++;
                }
            }
            catch (ReflectionTypeLoadException ex)
            {
                foreach (var le in ex.LoaderExceptions)
                {
                    if (le is not null)
                        _failures.Add($"scan {asmName}: {le.Message}");
                }
            }
            catch (Exception ex)
            {
                _failures.Add($"scan {asmName}: {Flatten(ex)}");
            }
        }

        return new ContentClientScanResult(entry, systems, _loaded.Count, _failures.Count);
    }

    public string FormatReport(int maxFails = 12)
    {
        var sb = new StringBuilder();
        sb.Append(Status);
        var scan = ScanClientTypes();
        sb.Append($" entry={(scan.EntryPointType ?? "-")} systems~{scan.SystemTypeCount}");
        foreach (var f in _failures.Take(maxFails))
            sb.Append(" | ").Append(f);
        if (_failures.Count > maxFails)
            sb.Append($" | …+{_failures.Count - maxFails}");
        return sb.ToString();
    }

    public static bool ShouldLoad(string fileName)
    {
        if (!fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return false;
        if (fileName.StartsWith("Robust.Server", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".Server.dll", StringComparison.OrdinalIgnoreCase))
            return false;
        if (fileName.Contains("Tests", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("Integration", StringComparison.OrdinalIgnoreCase))
            return false;

        var isClient = fileName.EndsWith(".Client.dll", StringComparison.OrdinalIgnoreCase)
                       || fileName.Contains(".Client.", StringComparison.OrdinalIgnoreCase);
        if (isClient && !ClientFeatureFlags.LoadContentClientAssemblies)
            return false;

        // UIKit packs often need Clyde UI surface — still load when client load is on;
        // failures are recorded, not fatal.
        return fileName.StartsWith("Content.", StringComparison.OrdinalIgnoreCase)
               || fileName.Contains("Goobstation", StringComparison.OrdinalIgnoreCase)
               || fileName.Contains("Content.", StringComparison.OrdinalIgnoreCase);
    }

    static int ClientRank(string fileName)
    {
        if (fileName.Contains(".Shared", StringComparison.OrdinalIgnoreCase))
            return 0;
        if (fileName.Contains(".Client", StringComparison.OrdinalIgnoreCase))
            return 2;
        return 1;
    }

    static string Flatten(Exception ex)
    {
        var cur = ex;
        while (cur.InnerException is not null)
            cur = cur.InnerException;
        var msg = cur.Message;
        return msg.Length <= 160 ? msg : msg[..160];
    }
}

public readonly record struct ContentClientScanResult(
    string? EntryPointType,
    int SystemTypeCount,
    int LoadedAssemblies,
    int FailureCount);
