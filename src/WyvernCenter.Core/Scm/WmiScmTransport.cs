using System.Management;

namespace WyvernCenter.Core.Scm;

/// <summary>
/// SCM0 over root\WMI. Each class is enumerated once; instances are cached by the index in their InstanceName
/// (ACPI\PNP0C14\0_N), so WMI builds the object paths itself. Reads refresh the cached object; writes
/// set the property and Put(). Requires administrator rights.
/// </summary>
public sealed class WmiScmTransport : IScmTransport
{
    private readonly ManagementScope _scope = new(@"\\.\root\WMI");
    private readonly Dictionary<ScmClass, ManagementObject?[]> _instances = new();
    private readonly object _lock = new();

    public WmiScmTransport() => _scope.Connect();

    public int Read(ScmClass c, int index)
    {
        lock (_lock)
        {
            var obj = Instance(c, index);
            obj.Get();
            return Convert.ToInt32(obj[ScmNames.Property(c)]);
        }
    }

    public void Write(ScmClass c, int index, int value)
    {
        lock (_lock)
        {
            var obj = Instance(c, index);
            // Battery blocks are sint16; the rest are uint8. Convert to the property's own type.
            obj[ScmNames.Property(c)] = c is ScmClass.MasterBattery or ScmClass.SlaveBattery
                ? (object)checked((short)value)
                : checked((byte)value);
            obj.Put();
        }
    }

    public IReadOnlyList<int> ReadAll(ScmClass c)
    {
        lock (_lock)
        {
            var all = Instances(c, refresh: true);
            return all.Select(o => o is null ? 0 : Convert.ToInt32(o[ScmNames.Property(c)])).ToArray();
        }
    }

    private ManagementObject Instance(ScmClass c, int index)
    {
        if (index < 0 || index >= ScmNames.Count(c))
            throw new ArgumentOutOfRangeException(nameof(index), $"{ScmNames.WmiClass(c)} has {ScmNames.Count(c)} instances.");
        return Instances(c, refresh: false)[index]
            ?? throw new InvalidOperationException($"{ScmNames.WmiClass(c)} instance {index} not present.");
    }

    private ManagementObject?[] Instances(ScmClass c, bool refresh)
    {
        if (!refresh && _instances.TryGetValue(c, out var cached)) return cached;

        var found = new ManagementObject?[ScmNames.Count(c)];
        using var searcher = new ManagementObjectSearcher(_scope, new ObjectQuery($"SELECT * FROM {ScmNames.WmiClass(c)}"));
        foreach (ManagementObject o in searcher.Get())
        {
            var i = ScmNames.InstanceIndex((string)o["InstanceName"]);
            if (i >= 0 && i < found.Length) found[i] = o;
            else o.Dispose();
        }
        if (_instances.TryGetValue(c, out var old))
            foreach (var o in old) o?.Dispose();
        _instances[c] = found;
        return found;
    }
}
