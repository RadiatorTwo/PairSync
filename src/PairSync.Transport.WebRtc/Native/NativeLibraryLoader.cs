using System.Reflection;
using System.Runtime.InteropServices;

namespace PairSync.Transport.WebRtc.Native;

/// <summary>
/// Resolves "datachannel" to the platform file name, preferring the copy next to the app
/// (from native/runtimes/{rid}/native) and falling back to a system installation (e.g. the AUR package).
/// </summary>
internal static class NativeLibraryLoader
{
    private static int _registered;

    public static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 0)
            NativeLibrary.SetDllImportResolver(typeof(NativeLibraryLoader).Assembly, Resolve);
    }

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != RtcNative.Library)
            return 0;

        foreach (var candidate in CandidateNames())
        {
            var local = Path.Combine(AppContext.BaseDirectory, candidate);
            if (File.Exists(local) && NativeLibrary.TryLoad(local, out var handle))
                return handle;
            if (NativeLibrary.TryLoad(candidate, assembly, searchPath, out handle))
                return handle;
        }

        throw new DllNotFoundException(
            "libdatachannel was not found. Build it with native/build-win.ps1 (Windows) or native/build-linux.sh (Linux), " +
            "or install the distribution package (e.g. AUR 'libdatachannel').");
    }

    private static string[] CandidateNames()
    {
        if (OperatingSystem.IsWindows())
            return ["datachannel.dll"];
        if (OperatingSystem.IsMacOS())
            return ["libdatachannel.dylib", "libdatachannel.0.24.dylib"];
        return ["libdatachannel.so", "libdatachannel.so.0.24"];
    }
}
