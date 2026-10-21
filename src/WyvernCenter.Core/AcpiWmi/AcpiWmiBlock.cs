using System.Runtime.InteropServices;

namespace WyvernCenter.Core.AcpiWmi;

/// <summary>
/// A firmware WMI block opened by its GUID through Windows' own WMI kernel interface (advapi32 → wmiacpi.sys).
/// Unlike the root\WMI classes, this needs no class definition (MOF) in Windows: the GUIDs come straight from the
/// firmware's _WDG table, so it works on a laptop that never had MSI software installed. Requires administrator rights.
/// </summary>
public sealed class AcpiWmiBlock : IDisposable
{
    public const uint AccessQuery = 0x0001, AccessSet = 0x0002, AccessExecute = 0x0010;

    private IntPtr _handle;

    private AcpiWmiBlock(IntPtr handle) => _handle = handle;

    /// <exception cref="AcpiWmiException">The firmware doesn't have this GUID (4200), or access was denied (5).</exception>
    public static AcpiWmiBlock Open(Guid guid, uint access)
    {
        var rc = Native.WmiOpenBlock(ref guid, access, out var handle);
        if (rc != 0) throw new AcpiWmiException(rc, $"open {guid}");
        return new AcpiWmiBlock(handle);
    }

    /// <summary>The data of one instance (e.g. ACPI\PNP0C14\0_7).</summary>
    public byte[] QuerySingle(string instanceName)
    {
        var size = 256u;
        while (true)
        {
            var buffer = new byte[size];
            var rc = Native.WmiQuerySingleInstanceW(Handle, instanceName, ref size, buffer);
            if (rc == AcpiWmiException.InsufficientBuffer && size > buffer.Length) continue;
            if (rc != 0) throw new AcpiWmiException(rc, $"query {instanceName}");
            return Wnode.SingleInstanceData(buffer);
        }
    }

    public void SetSingle(string instanceName, byte[] data)
    {
        var rc = Native.WmiSetSingleInstanceW(Handle, instanceName, 0, (uint)data.Length, data);
        if (rc != 0) throw new AcpiWmiException(rc, $"set {instanceName}");
    }

    /// <summary>Runs a firmware method. Input and output are the raw data blocks (no WNODE header).</summary>
    public byte[] Execute(string instanceName, uint methodId, byte[] input)
    {
        var size = 256u;
        while (true)
        {
            var output = new byte[size];
            var rc = Native.WmiExecuteMethodW(Handle, instanceName, methodId, (uint)input.Length, input, ref size, output);
            if (rc == AcpiWmiException.InsufficientBuffer && size > output.Length) continue;
            if (rc != 0) throw new AcpiWmiException(rc, $"method {methodId} on {instanceName}");
            return output.AsSpan(0, (int)Math.Min(size, (uint)output.Length)).ToArray();
        }
    }

    private IntPtr Handle => _handle != IntPtr.Zero ? _handle : throw new ObjectDisposedException(nameof(AcpiWmiBlock));

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        Native.WmiCloseBlock(_handle);
        _handle = IntPtr.Zero;
    }

    private static class Native
    {
        [DllImport("advapi32.dll")]
        public static extern uint WmiOpenBlock(ref Guid guid, uint desiredAccess, out IntPtr handle);

        [DllImport("advapi32.dll")]
        public static extern uint WmiCloseBlock(IntPtr handle);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        public static extern uint WmiQuerySingleInstanceW(IntPtr handle, string instanceName, ref uint bufferSize, byte[] buffer);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        public static extern uint WmiSetSingleInstanceW(IntPtr handle, string instanceName, uint reserved, uint valueBufferSize, byte[] valueBuffer);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        public static extern uint WmiExecuteMethodW(IntPtr handle, string instanceName, uint methodId, uint inputSize, byte[] input,
            ref uint outputSize, byte[] output);
    }
}

public sealed class AcpiWmiException : Exception
{
    public const uint AccessDenied = 5, InsufficientBuffer = 122, GuidNotFound = 4200, InstanceNotFound = 4201, ItemNotFound = 4202;

    public uint Code { get; }

    public AcpiWmiException(uint code, string what) : base($"{what}: {Describe(code)}") => Code = code;

    public static string Describe(uint code) => code switch
    {
        AccessDenied => "access denied (run as administrator)",
        GuidNotFound => "not in this laptop's firmware",
        InstanceNotFound => "instance not found",
        ItemNotFound => "method not implemented by the firmware",
        _ => $"error {code}",
    };
}

/// <summary>Reads the WNODE_SINGLE_INSTANCE structure WmiQuerySingleInstance returns (wmistr.h).</summary>
public static class Wnode
{
    // WNODE_HEADER is 48 bytes; then OffsetInstanceName, InstanceIndex, DataBlockOffset, SizeDataBlock (ULONG each).
    private const int DataBlockOffsetAt = 56, SizeDataBlockAt = 60;

    public static byte[] SingleInstanceData(byte[] wnode)
    {
        if (wnode.Length < SizeDataBlockAt + 4) throw new InvalidDataException("WNODE too short");
        var offset = BitConverter.ToUInt32(wnode, DataBlockOffsetAt);
        var size = BitConverter.ToUInt32(wnode, SizeDataBlockAt);
        if (offset > wnode.Length || size > wnode.Length - offset) throw new InvalidDataException("WNODE data outside the buffer");
        return wnode.AsSpan((int)offset, (int)size).ToArray();
    }
}
