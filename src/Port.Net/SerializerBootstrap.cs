using System.Reflection;
using System.Runtime.Loader;
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

    readonly DependencyCollection _container;
    bool _packageLoaded;

    SerializerBootstrap(
        DependencyCollection container,
        IRobustSerializer serializer,
        IRobustMappedStringSerializer mapped,
        IReflectionManager reflection,
        string typeHash,
        IReadOnlyList<string> loaded,
        string status)
    {
        _container = container;
        Serializer = serializer;
        MappedStrings = mapped;
        Reflection = reflection;
        TypeHash = typeHash;
        LoadedAssemblies = loaded;
        Status = status;
    }

    public static SerializerBootstrap? TryCreate(string? assembliesDirectory, Action<string>? log = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(assembliesDirectory) || !Directory.Exists(assembliesDirectory))
            {
                log?.Invoke($"serializer: no assemblies dir ({assembliesDirectory})");
                return null;
            }

            var loadedNames = new List<string>();
            var contentAsms = new List<Assembly>();

            foreach (var path in Directory.GetFiles(assembliesDirectory, "*.dll")
                         .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(path);
                // Prefer Shared content; skip pure Client UI if present (DEBUG serializer rejects Client in DEBUG).
                if (name.Contains("Content.Client", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("Content.Shared", StringComparison.OrdinalIgnoreCase))
                {
                    log?.Invoke($"serializer: skip {name}");
                    continue;
                }

                try
                {
                    var asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(path));
                    contentAsms.Add(asm);
                    loadedNames.Add(name);
                    log?.Invoke($"serializer: loaded {name}");
                }
                catch (Exception ex)
                {
                    log?.Invoke($"serializer: FAIL load {name}: {ex.GetType().Name}: {ex.Message}");
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
                reflection.LoadAssemblies(asm);

            var serializer = container.Resolve<IRobustSerializer>();
            serializer.Initialize();
            var hash = serializer.GetSerializableTypesHashString();
            log?.Invoke($"serializer: init OK typesHash={hash[..Math.Min(16, hash.Length)]}… asms={loadedNames.Count}");

            return new SerializerBootstrap(
                container,
                serializer,
                container.Resolve<IRobustMappedStringSerializer>(),
                reflection,
                hash,
                loadedNames,
                $"ready hash={hash[..Math.Min(12, hash.Length)]} asms={loadedNames.Count}");
        }
        catch (Exception ex)
        {
            log?.Invoke($"serializer: bootstrap FAIL {ex.GetType().Name}: {ex.Message}");
            try { IoCManager.Clear(); } catch { /* ignore */ }
            return null;
        }
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

    public void Dispose()
    {
        try { IoCManager.Clear(); } catch { /* ignore */ }
    }
}
