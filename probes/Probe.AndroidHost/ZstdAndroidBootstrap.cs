using System.Reflection;
using System.Runtime.InteropServices;

namespace Probe.AndroidHost;

/// <summary>
/// Map DllImport("zstd") → Android packaged libzstd.so before Robust mapped-string / MsgState inflate.
/// Natives are from com.github.luben:zstd-jni (exports full ZSTD_* C API).
/// </summary>
static class ZstdAndroidBootstrap
{
    static int _done;

    public static void EnsureLoaded()
    {
        if (Interlocked.Exchange(ref _done, 1) == 1)
            return;

        try
        {
            // Touch Robust.ZStd so its ModuleInitializer / SharpZstd load runs; then override resolver.
            _ = typeof(Robust.Shared.Utility.ZStd);
        }
        catch
        {
            /* ignore */
        }

        try
        {
            var interop = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "SharpZstd.Interop");
            if (interop != null)
                NativeLibrary.SetDllImportResolver(interop, Resolve);
        }
        catch (InvalidOperationException)
        {
            // Robust ModuleInitializer already registered — our early TryLoad still helps.
        }
        catch
        {
            /* ignore */
        }

        foreach (var candidate in new[] { "zstd", "libzstd" })
        {
            if (NativeLibrary.TryLoad(candidate, Assembly.GetExecutingAssembly(), null, out _))
                return;
            if (NativeLibrary.TryLoad(candidate, out _))
                return;
        }

        try
        {
            Java.Lang.JavaSystem.LoadLibrary("zstd");
        }
        catch
        {
            /* SetPackage / ZStdDecompressStream will surface DllNotFoundException */
        }
    }

    static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!libraryName.Equals("zstd", StringComparison.OrdinalIgnoreCase)
            && !libraryName.Equals("libzstd", StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero;

        foreach (var name in new[] { "zstd", "libzstd" })
        {
            if (NativeLibrary.TryLoad(name, assembly, searchPath, out var handle))
                return handle;
            if (NativeLibrary.TryLoad(name, Assembly.GetExecutingAssembly(), searchPath, out handle))
                return handle;
            if (NativeLibrary.TryLoad(name, out handle))
                return handle;
        }

        return IntPtr.Zero;
    }
}
