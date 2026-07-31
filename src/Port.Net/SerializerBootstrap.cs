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

            // Alphabetical load order is part of the type-map contract with this content pack.
            // Reordering / optional-skipping packs shifted hash C71B907F5563 → F40B9C432034 and
            // broke every MsgState (GetDeserializeTrampolineFromId NRE / IndexOutOfRange).
            var dlls = Directory.GetFiles(assembliesDirectory, "*.dll")
                .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
                .ToArray();
            log?.Invoke($"serializer: found {dlls.Length} dll(s) in {assembliesDirectory}");

            alc = new AssemblyLoadContext("SS14Content", isCollectible: false);
            var clientStub = typeof(Robust.Client.MobileClientStub).Assembly;
            var serverStub = typeof(Robust.Server.MobileServerStub).Assembly;
            // Ensure default context has already loaded stubs before content ALC resolves them.
            _ = clientStub.FullName;
            _ = serverStub.FullName;

            alc.Resolving += (_, name) =>
            {
                if (string.IsNullOrEmpty(name.Name))
                    return null;

                // Content packs often reference desktop engine assemblies we must not load.
                if (name.Name.Equals("Robust.Client", StringComparison.OrdinalIgnoreCase)
                    || name.Name.StartsWith("Robust.Client.", StringComparison.OrdinalIgnoreCase))
                    return clientStub;
                if (name.Name.Equals("Robust.Server", StringComparison.OrdinalIgnoreCase)
                    || name.Name.StartsWith("Robust.Server.", StringComparison.OrdinalIgnoreCase))
                    return serverStub;

                // Prefer already-loaded engine assemblies from the default context.
                foreach (var loaded in AssemblyLoadContext.Default.Assemblies)
                {
                    if (string.Equals(loaded.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase))
                        return loaded;
                }

                var candidate = Path.Combine(assembliesDirectory, name.Name + ".dll");
                if (!File.Exists(candidate))
                    return null;
                if (ShouldSkipContentDll(name.Name + ".dll"))
                    return null;
                try
                {
                    return alc.LoadFromAssemblyPath(Path.GetFullPath(candidate));
                }
                catch (Exception ex)
                {
                    log?.Invoke($"serializer: resolve FAIL {name.Name}: {Flatten(ex)}");
                    return null;
                }
            };

            var loadedNames = new List<string>();
            var skippedNames = new List<string>();
            var failedNames = new List<string>();
            var contentAsms = new List<Assembly>();

            // GameState NetSerializable component types live in Shared content packs.
            // Forks often ship extra *.Shared.dll (Goob/RMC/etc.) — must load those too,
            // otherwise the type-map hash diverges and DeserializeDirect throws InvalidData/NRE.
            foreach (var path in dlls)
            {
                var name = Path.GetFileName(path);
                if (!ShouldLoadForSerializer(name))
                {
                    skippedNames.Add(name);
                    log?.Invoke($"serializer: skip {name}");
                    continue;
                }

                try
                {
                    var full = Path.GetFullPath(path);
                    var bytes = File.ReadAllBytes(full);
                    var asm = alc.LoadFromStream(new MemoryStream(bytes));
                    // Force metadata resolve early — missing Robust.Client stubs surface here.
                    try
                    {
                        _ = asm.GetTypes();
                    }
                    catch (ReflectionTypeLoadException rtle)
                    {
                        var first = rtle.LoaderExceptions?.FirstOrDefault(e => e != null)?.Message ?? rtle.Message;
                        log?.Invoke($"serializer: WARN GetTypes {name}: {first}");
                        // Still keep the assembly — NetSerializable types that did load still help the type map.
                    }

                    contentAsms.Add(asm);
                    loadedNames.Add(name);
                    log?.Invoke($"serializer: loaded {name}");
                }
                catch (Exception ex)
                {
                    failedNames.Add(name);
                    log?.Invoke($"serializer: FAIL load {name}: {Flatten(ex)}");
                }
            }

            if (contentAsms.Count == 0)
                log?.Invoke("serializer: WARNING no shared content loaded — GameState decode may fail");
            if (skippedNames.Count > 0)
                log?.Invoke("serializer: skipped " + string.Join(", ", skippedNames));
            if (failedNames.Count > 0)
                log?.Invoke("serializer: failed " + string.Join(", ", failedNames));

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
            try
            {
                serializer.Initialize();
            }
            catch (Exception ex)
            {
                LastError = "serializer.Initialize: " + Flatten(ex);
                log?.Invoke($"serializer: Initialize FAIL {LastError}");
                throw;
            }
            var hash = serializer.GetSerializableTypesHashString();
            var typeCount = 0;
            try
            {
                if (serializer is RobustSerializer concrete)
                    typeCount = concrete.GetTypeMap().Count;
            }
            catch { /* ignore */ }

            var status =
                $"ready hash={hash[..Math.Min(12, hash.Length)]} asms={loadedNames.Count}/{dlls.Length} types={typeCount}";
            if (failedNames.Count > 0)
            {
                var shortFails = string.Join(",", failedNames.Select(ShortDll));
                status += $" fail={failedNames.Count}({shortFails})";
            }
            log?.Invoke($"serializer: init OK {status}");
            if (loadedNames.Count > 0)
                log?.Invoke("serializer: asms " + string.Join(", ", loadedNames));

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

    static string ShortDll(string fileName)
    {
        const string suffix = ".dll";
        var n = fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? fileName[..^suffix.Length]
            : fileName;
        if (n.StartsWith("Content.", StringComparison.OrdinalIgnoreCase))
            n = n["Content.".Length..];
        return n.Length <= 40 ? n : n[..40];
    }

    static bool ShouldSkipContentDll(string fileName)
    {
        if (fileName.StartsWith("Robust.Client", StringComparison.OrdinalIgnoreCase))
            return true;
        if (fileName.StartsWith("Robust.Server", StringComparison.OrdinalIgnoreCase))
            return true;
        // Only skip true client/server packs — not Content.Shared / *.Interfaces.Shared.
        if (fileName.EndsWith(".Client.dll", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains(".Client.", StringComparison.OrdinalIgnoreCase))
            return true;
        if (fileName.EndsWith(".Server.dll", StringComparison.OrdinalIgnoreCase)
            || (fileName.Contains(".Server.", StringComparison.OrdinalIgnoreCase)
                && !fileName.Contains("Database", StringComparison.OrdinalIgnoreCase)))
            return true;
        if (fileName.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("SDL", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("Clyde", StringComparison.OrdinalIgnoreCase))
            return true;
        // Client-only UI packs — not NetSerializable; load FAIL polluted fail= and DESER noise.
        if (fileName.Contains("UIKit", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains(".UI.", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    static bool ShouldLoadForSerializer(string fileName)
    {
        // Load every non-skipped content DLL. NetSerializable lives in Shared/Common/Maths/
        // Goobstation packs; holes → GetDeserializeTrampolineFromId IndexOutOfRange.
        if (ShouldSkipContentDll(fileName))
            return false;
        if (fileName.Contains("Integration", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("Tests", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase))
            return false;
        return fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
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
        if (package is null || package.Length == 0)
        {
            log?.Invoke("serializer: SetPackage skipped — empty package");
            return false;
        }

        try
        {
            try
            {
                MappedStrings.SetPackage(hash, package);
            }
            catch (InvalidOperationException ex) when (
                ex.Message.Contains("Hash mismatch", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("verify strings", StringComparison.OrdinalIgnoreCase))
            {
                // Android/sodium/cache edge cases: package still usable for GameState decode.
                log?.Invoke($"serializer: {ex.Message}");
                ForceLoadMappedPackage(package, hash, log);
            }

            _packageLoaded = true;
            Status = $"strings OK ({package.Length:N0}B) · {Status}";
            log?.Invoke($"serializer: mapped strings loaded ({package.Length:N0} B)");
            // Client Initialize() locks an empty dict; LoadFromPackage fills strings but leaves Locked alone.
            // Keep Locked=true so WriteMappedString asserts stay valid if touched later.
            try
            {
                var dictField = MappedStrings.GetType().GetField("_dict", BindingFlags.Instance | BindingFlags.NonPublic);
                var dict = dictField?.GetValue(MappedStrings);
                dict?.GetType().GetProperty("Locked")?.SetValue(dict, true);
                MappedStrings.GetType().GetField("_stringMapHash", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.SetValue(MappedStrings, hash);
            }
            catch
            {
                /* ignore */
            }
            return true;
        }
        catch (Exception ex)
        {
            var detail = Flatten(ex);
            LastError = detail;
            Status = $"serializer: SetPackage failed — {detail}";
            log?.Invoke($"serializer: SetPackage FAIL: {detail}");
            // Drop corrupt cache so the next connect fetches a fresh package.
            TryDeleteCachedStrings(hash, log);
            return false;
        }
    }

    /// <summary>
    /// Load mapped-string package without the official hash gate (still wires _dict for reads).
    /// </summary>
    void ForceLoadMappedPackage(byte[] package, byte[] serverHash, Action<string>? log)
    {
        var serType = MappedStrings.GetType();
        var dictField = serType.GetField("_dict", BindingFlags.Instance | BindingFlags.NonPublic)
                        ?? throw new MissingFieldException(serType.Name, "_dict");
        var dict = dictField.GetValue(MappedStrings)
                   ?? throw new InvalidOperationException("mapped string dict is null");
        var load = dict.GetType().GetMethod(
                       "LoadFromPackage",
                       BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                       binder: null,
                       types: new[] { typeof(byte[]), typeof(byte[]).MakeByRefType() },
                       modifiers: null)
                   ?? throw new MissingMethodException(dict.GetType().Name, "LoadFromPackage");

        var args = new object?[] { package, null };
        var count = (int)load.Invoke(dict, args)!;
        var hashResult = (byte[])args[1]!;

        serType.GetField("_stringMapHash", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.SetValue(MappedStrings, serverHash);
        serType.GetField("_mappedStringsPackage", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.SetValue(MappedStrings, package);
        dict.GetType().GetProperty("Locked")?.SetValue(dict, true);

        var match = hashResult.AsSpan().SequenceEqual(serverHash);
        log?.Invoke(
            $"serializer: force-loaded {count} strings hashMatch={match} " +
            $"pkg={package.Length:N0}B got={Convert.ToHexString(hashResult)[..Math.Min(16, hashResult.Length * 2)]} " +
            $"want={Convert.ToHexString(serverHash)[..Math.Min(16, serverHash.Length * 2)]}");
        if (!match)
            log?.Invoke("serializer: WARNING proceeding with hash-mismatched string package");
    }

    void TryDeleteCachedStrings(byte[] hash, Action<string>? log)
    {
        if (string.IsNullOrWhiteSpace(StringsCacheDirectory))
            return;
        try
        {
            var name = "strings-" + Convert.ToHexString(hash).ToLowerInvariant() + ".bin";
            var path = Path.Combine(StringsCacheDirectory, name);
            if (File.Exists(path))
            {
                File.Delete(path);
                log?.Invoke($"serializer: deleted bad cache {name}");
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"serializer: cache delete FAIL: {ex.Message}");
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
