using System.Runtime.InteropServices;

// Elevated app: native DLLs (nvml, powrprof, user32, ...) load only from System32, never from the app folder or PATH.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
