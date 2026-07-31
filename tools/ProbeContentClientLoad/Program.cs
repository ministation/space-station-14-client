using System.Reflection;
using System.Text.RegularExpressions;
using Port.Client;
using Port.Client.Content;
using Robust.Client;

var contentDir = args.ElementAtOrDefault(0)
                 ?? @"c:\ss14\space-station-14\bin\Content.Client";

ClientFeatureFlags.LoadContentClientAssemblies = true;
ContentAssemblyHost.EnsureAssemblyResolveHook();

Console.WriteLine($"stub={typeof(MobileClientStub).Assembly.Location}");
Console.WriteLine($"dir={contentDir}");

var host = new ContentAssemblyHost();
var n = host.LoadFromDirectory(contentDir);
Console.WriteLine(host.FormatReport(maxFails: 40));
Console.WriteLine($"typeload={(host.FullTypeLoadOk ? "OK" : "PARTIAL")} {host.BindStubLog}");

var fails = host.Failures.ToList();
foreach (var asm in host.Loaded)
{
    var name = asm.GetName().Name + ".dll";
    try
    {
        Console.WriteLine($"OK  {name} types={asm.GetExportedTypes().Length}");
    }
    catch (ReflectionTypeLoadException ex)
    {
        var les = ex.LoaderExceptions.Where(e => e != null).ToList();
        Console.WriteLine($"PART {name} loaderEx={les.Count}");
        foreach (var le in les.Take(25))
        {
            var msg = Flatten(le!);
            fails.Add($"{name}: {msg}");
            Console.WriteLine("  " + msg);
        }
    }
    catch (Exception ex)
    {
        var msg = Flatten(ex);
        fails.Add($"{name}: {msg}");
        Console.WriteLine($"FAIL-TYPES {name}: {msg}");
    }
}

var gameplay = ContentMetadataScan.ScanLoaded(host.Loaded);
Console.WriteLine(
    $"--- gameplay systems~{gameplay.EntitySystemCount} visualizers~{gameplay.VisualizerCount} entry~{gameplay.EntryPointCount} typeFail={gameplay.TypeLoadFailures} ---");
if (gameplay.SampleVisualizer is not null)
    Console.WriteLine("sample visualizer: " + gameplay.SampleVisualizer);

if (host.FullTypeLoadOk)
{
    Console.WriteLine("--- entrypoint ---");
    try
    {
        var entryAsm = host.Loaded.First(a =>
            (a.GetName().Name ?? "").Equals("Content.Client", StringComparison.OrdinalIgnoreCase));
        var entryType = entryAsm.GetExportedTypes().First(t => t.Name == "EntryPoint");
        Console.WriteLine("EntryPoint type: " + entryType.FullName);
        var systems = entryAsm.GetExportedTypes().Count(t =>
            t is { IsClass: true, IsAbstract: false } && t.Name.EndsWith("System"));
        Console.WriteLine($"exported concrete *System types: {systems}");

        var load = new ContentClientLoadSystem();
        load.UseHost(host);
        load.Attempted = true;
        var ep = new ContentEntryPointSystem { LoadSystem = load, Log = Console.WriteLine };
        Console.WriteLine("entrypoint: " + ep.TryBootstrap());
        var systemHost = new ContentClientSystemHost { LoadSystem = load, Log = Console.WriteLine };
        Console.WriteLine("systems: " + systemHost.Bootstrap());
    }
    catch (Exception ex)
    {
        Console.WriteLine("entrypoint probe: " + Flatten(ex));
    }
}

Console.WriteLine($"--- loaded={n} fails={fails.Count} ---");
var missing = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
foreach (var f in fails)
{
    foreach (var token in ExtractMissing(f))
        missing[token] = missing.GetValueOrDefault(token) + 1;
}

Console.WriteLine("--- missing tokens ---");
foreach (var kv in missing.OrderByDescending(k => k.Value).ThenBy(k => k.Key).Take(80))
    Console.WriteLine($"{kv.Value,3}  {kv.Key}");

static string Flatten(Exception ex)
{
    var cur = ex;
    while (cur.InnerException is not null) cur = cur.InnerException;
    return cur.GetType().Name + ": " + cur.Message;
}

static IEnumerable<string> ExtractMissing(string msg)
{
    foreach (Match m in Regex.Matches(msg, @"Could not load type '([^']+)'"))
        yield return m.Groups[1].Value;
    foreach (Match m in Regex.Matches(msg, @"Could not load file or assembly '([^']+)'"))
        yield return m.Groups[1].Value;
    foreach (Match m in Regex.Matches(msg, @"Method not found: '([^']+)'"))
        yield return m.Groups[1].Value;
}
