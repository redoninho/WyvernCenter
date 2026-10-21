using System.Reflection;
using System.Runtime.InteropServices;

namespace WyvernCenter.Core;

/// <summary>
/// Where NVIDIA's NVML may be loaded from: System32 (current drivers) or NVIDIA's admin-only NVSMI folder, always by
/// full path. Wyvern Center runs elevated; a bare-name load would also search the app's folder and the current directory,
/// where a non-admin could plant a DLL (DllImports are already limited to System32, see NativeSearchPath.cs).
/// </summary>
public static class NativeLibraries
{
    private static int _registered;

    public static readonly IReadOnlyList<string> NvmlPaths = new[]
    {
        Path.Combine(Environment.SystemDirectory, "nvml.dll"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvml.dll"),
    };

    /// <summary>Routes this assembly's nvml.dll imports to <see cref="NvmlPaths"/> only. Safe to call more than once.</summary>
    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1) return;
        NativeLibrary.SetDllImportResolver(typeof(NativeLibraries).Assembly, Resolve);
    }

    /// <summary>Loads NVML from a trusted path; zero when it isn't installed.</summary>
    public static IntPtr LoadNvml()
    {
        foreach (var path in NvmlPaths)
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out var h)) return h;
        return IntPtr.Zero;
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? paths) =>
        string.Equals(name, "nvml.dll", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "nvml", StringComparison.OrdinalIgnoreCase)
            ? LoadNvml()   // zero → DllNotFoundException, handled by the callers
            : IntPtr.Zero; // everything else: the default (System32 only, above)
}
