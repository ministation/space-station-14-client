using System.Reflection;
using System.Runtime.InteropServices;

namespace Probe.AndroidHost;

/// <summary>
/// Map DllImport("libsodium") → Android packaged libsodium.so before CryptoBox static init.
/// </summary>
static class SodiumAndroidBootstrap
{
    static int _done;

    public static void EnsureLoaded()
    {
        if (Interlocked.Exchange(ref _done, 1) == 1)
            return;

        NativeLibrary.SetDllImportResolver(typeof(SpaceWizards.Sodium.CryptoBox).Assembly, Resolve);
        try
        {
            // Also cover Interop assembly which owns the actual P/Invokes.
            var interop = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "SpaceWizards.Sodium.Interop");
            if (interop != null)
                NativeLibrary.SetDllImportResolver(interop, Resolve);
        }
        catch
        {
            /* ignore */
        }

        // Force early load so TypeInitializationException surfaces with a clearer path.
        foreach (var candidate in new[] { "libsodium", "sodium" })
        {
            if (NativeLibrary.TryLoad(candidate, Assembly.GetExecutingAssembly(), null, out _))
                return;
        }

        try
        {
            Java.Lang.JavaSystem.LoadLibrary("sodium");
        }
        catch
        {
            /* last resort — CryptoBox will throw with detail */
        }
    }

    static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!libraryName.Equals("libsodium", StringComparison.OrdinalIgnoreCase)
            && !libraryName.Equals("sodium", StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero;

        foreach (var name in new[] { "libsodium", "sodium", "liblibsodium" })
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
