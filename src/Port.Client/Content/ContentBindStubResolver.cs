using System.Reflection;
using System.Runtime.Loader;

namespace Port.Client.Content;

/// <summary>
/// Resolves a <c>Robust.Client</c> assembly that shares <see cref="Component"/> identity with
/// the content-dir / ACZ <c>Robust.Shared</c> (content-bind stub or content-bin client).
/// </summary>
public static class ContentBindStubResolver
{
    public const string ContentBindFileName = "Robust.Client.ContentBind.dll";

    public static string? LastResolveLog { get; private set; }

    public static Assembly ResolveClientAssembly(AssemblyLoadContext alc, string directory, Assembly hostStub)
    {
        // 1) Explicit content-bind stub next to ACZ packs.
        var bind = Path.Combine(directory, ContentBindFileName);
        if (File.Exists(bind))
        {
            LastResolveLog = "file " + ContentBindFileName;
            return alc.LoadFromAssemblyPath(Path.GetFullPath(bind));
        }

        // 2) Compile stub sources against this directory's Shared (repo sources or embedded).
        //    Prefer over desktop Clyde client — Android ACZ never ships Robust.Client.
        if (ContentBindStubCompiler.TryCompile(directory, stubSourceRoot: null, out var compiled, out var compileLog)
            && File.Exists(compiled))
        {
            LastResolveLog = "compiled: " + compileLog;
            return alc.LoadFromAssemblyPath(Path.GetFullPath(compiled));
        }

        LastResolveLog = "compile-miss: " + compileLog;

        // 3) Desktop content-bin full Robust.Client (same Shared identity) as fallback.
        var localClient = Path.Combine(directory, "Robust.Client.dll");
        if (File.Exists(localClient) && LooksLikeEngineClient(localClient))
        {
            try
            {
                LastResolveLog += " | engine-client";
                return alc.LoadFromAssemblyPath(Path.GetFullPath(localClient));
            }
            catch
            {
                // fall through
            }
        }

        // 4) Host vendor stub — type-load of Content.Client may fail on EntityQuery constraints.
        LastResolveLog += " | host-stub-fallback";
        return hostStub;
    }

    static bool LooksLikeEngineClient(string path)
    {
        try
        {
            var len = new FileInfo(path).Length;
            return len > 500_000;
        }
        catch
        {
            return false;
        }
    }
}
