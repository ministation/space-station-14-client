using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Robust.Shared.GameStates;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Network;
using Robust.Shared.Reflection;
using Robust.Shared.Serialization;

namespace Port.Net;

public sealed class SerializerBootstrap : IDisposable
{
    public IRobustSerializer Serializer { get; }
    internal IRobustMappedStringSerializer MappedStrings { get; }
    public IReflectionManager Reflection { get; }
    public string TypeHash { get; }
    public IReadOnlyList<string> LoadedAssemblies { get; }
    public string Status { get; private set; }
    public static string? LastError { get; private set; }

    readonly DependencyCollection _container;
    readonly AssemblyLoadContext? _loadContext;
    bool _packageLoaded;

    SerializerBootstrap(
        DependencyCollection container,
        AssemblyLoadContext? loadContext,
        IRobustSerializer serializer,
        IRobustMappedStringSerializer mapped,
        IReflectionManager reflection,
        string typeHash,
        IReadOnlyList<string> loaded,
        string status)
    {
        _container = container;
        _loadContext = loadContext;
        Serializer = serializer;
        MappedStrings = mapped;
        Reflection = reflection;
        TypeHash = typeHash;
        LoadedAssemblies = loaded;
        Status = status;
    }

    public static SerializerBootstrap? TryCreate(string? assembliesDirectory, Action<string>? log = null)
    {
        LastError = null;
        AssemblyLoadContext? alc = null;
        try
        {
            if (string.IsNullOrWhiteSpace(assembliesDirectory) || !Directory.Exists(assembliesDirectory))
            {
                LastError = $"no assemblies dir ({assembliesDirectory})";
                log?.Invoke($"serializer: {LastError}");
                return null;
            }

            var dlls = Directory.GetFiles(assembliesDirectory, "*.dll")
                .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
                .ToArray();
            log?.Invoke($"serializer: found {dlls.Length} dll(s) in {assembliesDirectory}");

            alc = new AssemblyLoadContext("SS14Content", isCollectible: true);
            alc.Resolving += (_, name) =>
            {
                if (string.IsNullOrEmpty(name.Name))
                    return null;
                var candidate = Path.Combine(assembliesDirectory, name.Name + ".dll");
                if (!File.Exists(candidate))
                    return null;
                try
                {
                    return alc.LoadFromAssemblyPath(Path.GetFullPath(candidate));
                }
                catch
                {
                    return null;
                }
            };

            var loadedNames = new List<string>();
            var contentAsms = new List<Assembly>();

            // Prefer Shared content assemblies; Client UI assemblies are optional / often fail on mobile.
            foreach (var path in dlls)
            {
                var name = Path.GetFileName(path);
                if (name.Contains("Content.Client", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("Content.Shared", StringComparison.OrdinalIgnoreCase))
                {
                    log?.Invoke($"serializer: skip {name}");
                    continue;
                }

                // Server-only packs are useless / harmful for client serializer.
                if (name.Contains("Content.Server", StringComparison.OrdinalIgnoreCase)
                    || name.Contains(".Server.", StringComparison.OrdinalIgnoreCase))
                {
                    log?.Invoke($"serializer: skip server {name}");
                    continue;
                }

                try
                {
                    var full = Path.GetFullPath(path);
                    // Load bytes so Android path locks / duplicate Default ALC loads are avoided.
                    var bytes = File.ReadAllBytes(full);
                    var asm = alc.LoadFromStream(new MemoryStream(bytes));
                    contentAsms.Add(asm);
                    loadedNames.Add(name);
                    log?.Invoke($"serializer: loaded {name}");
                }
                catch (Exception ex)
                {
                    var msg = Flatten(ex);
                    log?.Invoke($"serializer: FAIL load {name}: {msg}");
                }
            }

            var container = new DependencyCollection();
            container.Register<ILogManager, LogManager>();
            container.RegisterInstance<INetManager>(new StubNetManager());
            container.Register<IReflectionManager, PortReflectionManager>();
            container.Register<IRobustMappedStringSerializer, RobustMappedStringSerializer>();
            container.Register<IRobustSerializer, PortRobustSerializer>();
            container.BuildGraph();

            IoCManager.InitThread(container, replaceExisting: true);

            var reflection = container.Resolve<IReflectionManager>();
            reflection.Initialize();
            reflection.LoadAssemblies(typeof(GameState).Assembly);

            foreach (var asm in contentAsms)
            {
                try
                {
                    reflection.LoadAssemblies(asm);
                }
                catch (Exception ex)
                {
                    log?.Invoke($"serializer: FAIL reflect {asm.GetName().Name}: {Flatten(ex)}");
                }
            }

            var serializer = container.Resolve<IRobustSerializer>();
            serializer.Initialize();
            var hash = serializer.GetSerializableTypesHashString();
            var status =
                $"ready hash={hash[..Math.Min(12, hash.Length)]} asms={loadedNames.Count}/{dlls.Length}";
            log?.Invoke($"serializer: init OK {status}");

            return new SerializerBootstrap(
                container,
                alc,
                serializer,
                container.Resolve<IRobustMappedStringSerializer>(),
                reflection,
                hash,
                loadedNames,
                status);
        }
        catch (Exception ex)
        {
            LastError = Flatten(ex);
            log?.Invoke($"serializer: bootstrap FAIL {LastError}");
            try { IoCManager.Clear(); } catch { /* ignore */ }
            try { alc?.Unload(); } catch { /* ignore */ }
            return null;
        }
    }

    static string Flatten(Exception ex)
    {
        var sb = new StringBuilder();
        for (var e = ex; e != null; e = e.InnerException!)
        {
            if (sb.Length > 0) sb.Append(" → ");
            sb.Append(e.GetType().Name).Append(": ").Append(e.Message);
            if (e is ReflectionTypeLoadException rtle && rtle.LoaderExceptions is { Length: > 0 })
            {
                foreach (var le in rtle.LoaderExceptions)
                {
                    if (le is null) continue;
                    sb.Append(" | ").Append(le.GetType().Name).Append(": ").Append(le.Message);
                }
            }
            if (ReferenceEquals(e, e.InnerException))
                break;
        }
        return sb.ToString();
    }

    public bool TrySetMappedPackage(byte[] hash, byte[] package, Action<string>? log = null)
    {
        try
        {
            MappedStrings.SetPackage(hash, package);
            _packageLoaded = true;
            Status = $"strings OK ({package.Length:N0}B) · {Status}";
            log?.Invoke($"serializer: mapped strings loaded ({package.Length:N0} B)");
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"serializer: SetPackage FAIL: {ex.Message}");
            return false;
        }
    }

    public bool HasMappedStrings => _packageLoaded;
    public string? StringsCacheDirectory { get; set; }

    public bool TryLoadCachedStrings(byte[] hash, Action<string>? log = null)
    {
        if (_packageLoaded || string.IsNullOrWhiteSpace(StringsCacheDirectory))
            return _packageLoaded;
        try
        {
            var name = "strings-" + Convert.ToHexString(hash).ToLowerInvariant() + ".bin";
            var path = Path.Combine(StringsCacheDirectory, name);
            if (!File.Exists(path))
                return false;
            var package = File.ReadAllBytes(path);
            return TrySetMappedPackage(hash, package, log);
        }
        catch (Exception ex)
        {
            log?.Invoke($"serializer: cache load FAIL: {ex.Message}");
            return false;
        }
    }

    public void TrySaveCachedStrings(byte[] hash, byte[] package, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(StringsCacheDirectory))
            return;
        try
        {
            Directory.CreateDirectory(StringsCacheDirectory);
            var name = "strings-" + Convert.ToHexString(hash).ToLowerInvariant() + ".bin";
            var path = Path.Combine(StringsCacheDirectory, name);
            File.WriteAllBytes(path, package);
            log?.Invoke($"serializer: cached strings → {name} ({package.Length:N0}B)");
        }
        catch (Exception ex)
        {
            log?.Invoke($"serializer: cache save FAIL: {ex.Message}");
        }
    }

    public void Dispose()
    {
        try { IoCManager.Clear(); } catch { /* ignore */ }
        try { _loadContext?.Unload(); } catch { /* ignore */ }
    }
}
