using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WyvernCenter.Core.Bridge;

/// <summary>
/// Guards the elevated bridge against pipe squatting: before sending anything, the pipe's server process must be
/// the process of the MSI Foundation Service itself (only admins can create or change services).
/// </summary>
public static class PipeServerCheck
{
    public static void EnsureTrusted(uint serverPid, uint servicePid)
    {
        if (serverPid == 0 || servicePid == 0 || serverPid != servicePid)
            throw new UnauthorizedAccessException(
                $"Pipe server (pid {serverPid}) is not the MSI Foundation Service (pid {servicePid}); refusing to talk to it.");
    }

    public static uint ServerPid(SafePipeHandle pipe) =>
        GetNamedPipeServerProcessId(pipe, out var pid) ? pid : throw new Win32Exception();

    /// <summary>Process id of a running service, or 0 when it isn't running.</summary>
    public static uint ServicePid(string serviceName)
    {
        var scm = OpenSCManager(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero) throw new Win32Exception();
        try
        {
            var svc = OpenService(scm, serviceName, ServiceQueryStatus);
            if (svc == IntPtr.Zero) throw new Win32Exception();
            try
            {
                var status = new ServiceStatusProcess();
                return QueryServiceStatusEx(svc, ScStatusProcessInfo, ref status, Marshal.SizeOf<ServiceStatusProcess>(), out _)
                    ? status.ProcessId
                    : throw new Win32Exception();
            }
            finally
            {
                CloseServiceHandle(svc);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const int ScStatusProcessInfo = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode,
                    CheckPoint, WaitHint, ProcessId, ServiceFlags;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenService(IntPtr scm, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatusEx(IntPtr service, int infoLevel, ref ServiceStatusProcess buffer, int size, out int needed);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
