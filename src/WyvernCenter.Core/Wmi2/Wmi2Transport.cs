using WyvernCenter.Core.AcpiWmi;

namespace WyvernCenter.Core.Wmi2;

/// <summary>One raw MSI_ACPI method call: 32-byte package in, the firmware's output out.</summary>
public interface IWmi2Transport
{
    byte[] Call(Wmi2Method method, byte[] input);
}

/// <summary>
/// MSI_ACPI by firmware GUID through advapi32 (no MSI class definitions or software needed). The instance is
/// found once by asking each candidate for Get_WMI, which only reads the interface version.
/// </summary>
public sealed class GuidWmi2Transport : IWmi2Transport, IDisposable
{
    /// <summary>MSI's software always uses 0_0; the others cover a firmware that numbers its WMI device differently.</summary>
    public static readonly IReadOnlyList<string> CandidateInstances =
        new[] { @"ACPI\PNP0C14\0_0", @"ACPI\PNP0C14\1_0", @"ACPI\PNP0C14\2_0", @"ACPI\PNP0C14\3_0" };

    private readonly AcpiWmiBlock _block;
    private readonly object _lock = new();

    public string Instance { get; }

    private GuidWmi2Transport(AcpiWmiBlock block, string instance)
    {
        _block = block;
        Instance = instance;
    }

    /// <summary>Null when the firmware has no MSI_ACPI GUID (WMI v1 laptops, non-MSI machines). Never throws.</summary>
    public static GuidWmi2Transport? TryOpen(Action<string>? log = null)
    {
        AcpiWmiBlock block;
        try { block = AcpiWmiBlock.Open(Wmi2Protocol.Guid, AcpiWmiBlock.AccessExecute); }
        catch (AcpiWmiException ex)
        {
            log?.Invoke("WMI2: " + ex.Message);
            return null;
        }

        foreach (var instance in CandidateInstances)
        {
            try
            {
                block.Execute(instance, (uint)Wmi2Method.GetWmi, Wmi2Protocol.Input(0));
                log?.Invoke($"WMI2: firmware answers on {instance}");
                return new GuidWmi2Transport(block, instance);
            }
            catch (AcpiWmiException ex) when (ex.Code == AcpiWmiException.InstanceNotFound)
            {
                // try the next name
            }
            catch (AcpiWmiException ex)
            {
                log?.Invoke($"WMI2: {instance}: {ex.Message}");
            }
        }
        block.Dispose();
        log?.Invoke("WMI2: GUID present but no instance answered");
        return null;
    }

    public byte[] Call(Wmi2Method method, byte[] input)
    {
        lock (_lock)
            return _block.Execute(Instance, (uint)method, input);
    }

    public void Dispose()
    {
        lock (_lock) _block.Dispose();
    }
}
