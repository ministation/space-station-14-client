using System.Reflection;
using System.Runtime.Loader;
using Port.Net;

namespace Port.Client.Content;

/// <summary>
/// Loads Content assemblies for the full client path.
/// Shared packs always load; *.Client packs require
/// <see cref="SerializerLoadPolicy.LoadContentClientAssemblies"/>.
/// </summary>
public sealed class ContentAssemblyHost
{
    readonly List<Assembly> _loaded = new();

    public IReadOnlyList<Assembly> Loaded => _loaded;

    public int LoadFromDirectory(string directory)
    {
        if (!Directory.Exists(directory))
            return 0;

        var count = 0;
        foreach (var path in Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly)
                     .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(path);
            if (!ShouldLoad(name))
                continue;
            try
            {
                var asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(path));
                _loaded.Add(asm);
                count++;
            }
            catch
            {
                // Missing Robust.Client surface — expected until stubs expand.
            }
        }

        return count;
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
        if (isClient && !SerializerLoadPolicy.LoadContentClientAssemblies)
            return false;

        return fileName.StartsWith("Content.", StringComparison.OrdinalIgnoreCase)
               || fileName.Contains("Goobstation", StringComparison.OrdinalIgnoreCase)
               || fileName.Contains("Content.", StringComparison.OrdinalIgnoreCase);
    }
}
