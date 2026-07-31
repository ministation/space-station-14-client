using System.Reflection;
using Port.Client.Bootstrap;

namespace Port.Client.Content;

/// <summary>
/// After Content.Client full type-load, runs EntryPoint IoC bootstrap (best-effort).
/// Uses the content ALC Shared <c>IoCManager</c> and content-bind <c>Robust.Client</c> types.
/// </summary>
public sealed class ContentEntryPointSystem : IClientSystem
{
    public ContentClientLoadSystem? LoadSystem { get; set; }
    public Action<string>? Log { get; set; }

    public string Status { get; private set; } = "idle";
    public bool Attempted { get; private set; }
    public object? EntryPointInstance { get; private set; }

    public void Initialize()
    {
    }

    public void FrameUpdate(float dt)
    {
        if (Attempted || LoadSystem is null || !LoadSystem.Attempted)
            return;
        if (!LoadSystem.Host.FullTypeLoadOk)
        {
            Attempted = true;
            Status = "skip typeload-partial";
            Log?.Invoke($"content.entrypoint: {Status}");
            return;
        }

        Attempted = true;
        Status = TryBootstrap();
        Log?.Invoke($"content.entrypoint: {Status}");
    }

    public string TryBootstrap()
    {
        var host = LoadSystem?.Host;
        if (host is null)
            return "no host";

        Type? entryType = null;
        foreach (var asm in host.Loaded)
        {
            var name = asm.GetName().Name ?? "";
            if (!name.Contains("Client", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                foreach (var t in asm.GetExportedTypes())
                {
                    if (t.Name == "EntryPoint" && t is { IsClass: true, IsAbstract: false })
                    {
                        entryType = t;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                return "entry scan: " + Flatten(ex);
            }

            if (entryType is not null)
                break;
        }

        if (entryType is null)
            return "no EntryPoint type";

        try
        {
            var ioc = GetContentIoC(host);
            if (ioc is null)
                return "no IoCManager";

            var init = entryType.GetMethod("Init",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy);
            if (init is null)
                return $"found {entryType.FullName} (no Init)";

            // Pre-seed + iterative Clear/retry — Content.Client ContentIoC.Register is not overwrite-safe.
            var pendingStubs = new List<Type>();
            string? last = null;
            for (var attempt = 0; attempt < 48; attempt++)
            {
                try
                {
                    InvokeClear(ioc);
                    ioc.GetMethod("InitThread", Type.EmptyTypes)?.Invoke(null, null);
                    RegisterEngineIoC(host, ioc);
                    foreach (var needed in pendingStubs)
                    {
                        try
                        {
                            var impl = FindImplementation(host, needed);
                            if (impl is not null)
                            {
                                InvokeRegister(ioc, needed.IsInterface ? needed : impl, impl);
                                continue;
                            }

                            if (needed.IsInterface)
                            {
                                var instanceStub = ContentIoCStubEmitter.CreateInstance(needed);
                                InvokeRegisterInstance(ioc, needed, instanceStub);
                            }
                            else if (needed is { IsClass: true, IsAbstract: false })
                            {
                                InvokeRegister(ioc, needed, needed);
                            }
                        }
                        catch (Exception seedEx)
                        {
                            Log?.Invoke("content.entrypoint seed " + needed.Name + ": " + Flatten(seedEx));
                        }
                    }

                    InvokeBuildGraph(ioc);

                    var instance = Activator.CreateInstance(entryType)
                                   ?? throw new InvalidOperationException("EntryPoint ctor returned null");
                    EntryPointInstance = instance;
                    init.Invoke(instance, null);
                    return $"Init OK {entryType.FullName} (iocPasses={attempt + 1} stubs={pendingStubs.Count})";
                }
                catch (TargetInvocationException ex)
                {
                    var inner = Unwrap(ex);
                    last = Flatten(inner);

                    // Stub API / null stubs — IoC graph got far enough for Phase 7.
                    if (inner is MissingMethodException or TypeLoadException
                        or NullReferenceException or NotImplementedException or InvalidOperationException)
                    {
                        // Keep retrying InvalidOperationException only when it's a missing IoC registration.
                        if (inner is InvalidOperationException
                            && inner.Message.Contains("unregistered", StringComparison.OrdinalIgnoreCase))
                        {
                            // fall through to ExtractMissingType
                        }
                        else if (inner is not InvalidOperationException
                                 || pendingStubs.Count > 0
                                 || attempt > 0)
                        {
                            EntryPointInstance ??= Activator.CreateInstance(entryType);
                            return $"Init PARTIAL {entryType.FullName} (iocPasses={attempt + 1} stubs={pendingStubs.Count}): {last}";
                        }
                    }

                    var missing = ExtractMissingType(host, inner);
                    if (missing is null)
                        return $"Init FAIL: {last}";

                    // Queue for next Clear/seed pass (interfaces → DispatchProxy; classes → Register self).
                    if (missing.IsInterface || missing is { IsClass: true, IsAbstract: false })
                    {
                        if (!pendingStubs.Contains(missing))
                            pendingStubs.Add(missing);
                    }
                    else
                    {
                        return $"Init FAIL: {last}";
                    }

                    Log?.Invoke("content.entrypoint auto-reg: " + last);
                }
                catch (Exception ex)
                {
                    last = Flatten(ex);
                    var missing = ExtractMissingType(host, ex);
                    if (missing is null || !missing.IsInterface)
                        return $"Init FAIL: {last}";
                    if (!pendingStubs.Contains(missing))
                        pendingStubs.Add(missing);
                    Log?.Invoke("content.entrypoint auto-reg: " + last);
                }
            }

            return "Init FAIL: too many missing IoC deps; last=" + last;
        }
        catch (Exception ex)
        {
            return "Init FAIL: " + Flatten(ex);
        }
    }

    static Type? GetContentIoC(ContentAssemblyHost host)
    {
        var shared = host.Loaded.FirstOrDefault(a =>
            (a.GetName().Name ?? "").Equals("Robust.Shared", StringComparison.OrdinalIgnoreCase));
        shared ??= host.LoadContext?.Assemblies.FirstOrDefault(a =>
            (a.GetName().Name ?? "").Equals("Robust.Shared", StringComparison.OrdinalIgnoreCase));
        return shared?.GetType("Robust.Shared.IoC.IoCManager");
    }

    void RegisterEngineIoC(ContentAssemblyHost host, Type? ioc)
    {
        if (ioc is null)
        {
            Log?.Invoke("content.entrypoint ioc: IoCManager type missing");
            return;
        }

        // Known Shared + Client pairs (content-bind / content Shared identities).
        TryReg(host, ioc, "Robust.Shared.Log.ILogManager", "Robust.Shared.Log.LogManager");
        TryReg(host, ioc, "Robust.Shared.Timing.IGameTiming", "Robust.Shared.Timing.GameTiming");
        TryReg(host, ioc, "Robust.Client.IBaseClient", "Robust.Client.BaseClient");
        TryReg(host, ioc, "Robust.Client.IGameController", "Robust.Client.GameController");
        TryReg(host, ioc, "Robust.Client.State.IStateManager", "Robust.Client.State.StateManager");
        TryReg(host, ioc, "Robust.Client.UserInterface.IUserInterfaceManager", "Robust.Client.UserInterface.UIManager");
        TryReg(host, ioc, "Robust.Client.Input.IInputManager", "Robust.Client.Input.InputManager");
        TryReg(host, ioc, "Robust.Client.Graphics.IOverlayManager", "Robust.Client.Graphics.OverlayManager");
        TryReg(host, ioc, "Robust.Client.Graphics.IClyde", "Robust.Client.Graphics.NullClyde");
        TryReg(host, ioc, "Robust.Client.Graphics.IEyeManager", "Robust.Client.Graphics.EyeManager");
        TryReg(host, ioc, "Robust.Client.ResourceManagement.IResourceCache",
            "Robust.Client.ResourceManagement.NullResourceCache");
        // IPrototypeManager usually has no concrete public impl in content-bind — seeded via DispatchProxy.
        TryReg(host, ioc, "Robust.Shared.Timing.IGameTiming", "Robust.Client.Timing.ClientGameTiming");
    }

    Type? ExtractMissingType(ContentAssemblyHost host, Exception ex)
    {
        Type? missing = null;

        // Never Type.GetType(AQN) here — that resolves vendor Shared from the default ALC.
        // Content packs need the content-ALC Shared identity.
        foreach (var fieldName in new[] { "TargetType", "TypeName" })
        {
            var f = ex.GetType().GetField(fieldName);
            if (f?.GetValue(ex) is string aqn && !string.IsNullOrEmpty(aqn))
            {
                missing = FindTypeByAqn(host, aqn);
                if (missing is not null) return missing;
            }
        }

        var msg = ex.Message;
        var fieldMarker = " unregistered type with its field ";
        var idx = msg.IndexOf(fieldMarker, StringComparison.Ordinal);
        if (idx >= 0)
        {
            var rest = msg[(idx + fieldMarker.Length)..];
            var colon = rest.IndexOf(':');
            var typeName = (colon >= 0 ? rest[..colon] : rest).Trim();
            missing = FindType(host, typeName);
            if (missing is not null) return missing;
        }

        const string prefix = "Attempted to resolve unregistered type: ";
        if (msg.StartsWith(prefix, StringComparison.Ordinal))
        {
            missing = FindType(host, msg[prefix.Length..].Trim());
            if (missing is not null) return missing;
        }

        const string alt = " dependency field: ";
        idx = msg.LastIndexOf(alt, StringComparison.Ordinal);
        if (idx >= 0)
            return FindType(host, msg[(idx + alt.Length)..].Trim());

        Log?.Invoke("content.entrypoint cannot parse missing type from: " + msg);
        return null;
    }

    static Type? FindImplementation(ContentAssemblyHost host, Type iface)
    {
        if (!iface.IsInterface)
            return null;

        foreach (var asm in EnumerateContentAssemblies(host))
        {
            try
            {
                foreach (var t in asm.GetExportedTypes())
                {
                    if (t is { IsClass: true, IsAbstract: false } && iface.IsAssignableFrom(t))
                        return t;
                }
            }
            catch
            {
                // ignore partial loads
            }
        }

        return null;
    }

    static IEnumerable<Assembly> EnumerateContentAssemblies(ContentAssemblyHost host)
    {
        foreach (var a in host.Loaded)
            yield return a;
        if (host.LoadContext is null) yield break;
        foreach (var a in host.LoadContext.Assemblies)
            yield return a;
        if (host.LoadContext.ClientAssembly is not null)
            yield return host.LoadContext.ClientAssembly;
    }

    static Type? FindTypeByAqn(ContentAssemblyHost host, string aqn)
    {
        try
        {
            // Strip assembly qualifier for ALC search.
            var comma = aqn.IndexOf(',');
            var full = comma > 0 ? aqn[..comma] : aqn;
            return FindType(host, full);
        }
        catch
        {
            return null;
        }
    }

    static Type? FindType(ContentAssemblyHost host, string fullName)
    {
        foreach (var asm in EnumerateContentAssemblies(host))
        {
            var t = asm.GetType(fullName, throwOnError: false);
            if (t is not null) return t;
        }

        return null;
    }

    void TryReg(ContentAssemblyHost host, Type ioc, string ifaceName, string implName)
    {
        var iface = FindType(host, ifaceName);
        var impl = FindType(host, implName);
        if (iface is null || impl is null)
            return;
        if (!InvokeRegister(ioc, iface, impl))
            Log?.Invoke($"content.entrypoint skip {ifaceName}");
    }

    static bool InvokeRegister(Type ioc, Type iface, Type impl)
    {
        try
        {
            if (!iface.IsAssignableFrom(impl) && iface != impl)
                return false;

            var register = ioc.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m =>
                    m.Name == "Register"
                    && m.IsGenericMethodDefinition
                    && m.GetGenericArguments().Length == 2
                    && m.GetParameters().Length == 1
                    && m.GetParameters()[0].ParameterType == typeof(bool));
            if (register is null)
                return false;
            register.MakeGenericMethod(iface, impl).Invoke(null, [true]);
            return true;
        }
        catch
        {
            return false;
        }
    }

    static bool InvokeRegisterInstance(Type ioc, Type iface, object instance)
    {
        try
        {
            var register = ioc.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m =>
                    m.Name == "RegisterInstance"
                    && m.IsGenericMethodDefinition
                    && m.GetGenericArguments().Length == 1
                    && m.GetParameters().Length >= 1);
            if (register is null)
                return false;
            var args = register.GetParameters().Length == 1
                ? new[] { instance }
                : new object[] { instance, true };
            register.MakeGenericMethod(iface).Invoke(null, args);
            return true;
        }
        catch
        {
            return false;
        }
    }

    static void InvokeBuildGraph(Type? ioc)
    {
        try
        {
            ioc?.GetMethod("BuildGraph", Type.EmptyTypes)?.Invoke(null, null);
        }
        catch
        {
            // Graph may already be mid-build; Init will BuildGraph again.
        }
    }

    static void InvokeClear(Type? ioc)
    {
        try
        {
            ioc?.GetMethod("Clear", Type.EmptyTypes)?.Invoke(null, null);
        }
        catch
        {
            // ignore
        }
    }

    public void Reset()
    {
        Attempted = false;
        Status = "idle";
        EntryPointInstance = null;
    }

    public void Shutdown()
    {
        LoadSystem = null;
        Log = null;
        EntryPointInstance = null;
    }

    static Exception Unwrap(Exception ex)
    {
        var cur = ex;
        while (cur.InnerException is not null) cur = cur.InnerException;
        return cur;
    }

    static string Flatten(Exception ex)
    {
        var cur = Unwrap(ex);
        var msg = cur.GetType().Name + ": " + cur.Message;
        return msg.Length <= 220 ? msg : msg[..220];
    }
}
