namespace WyvernCenter.Core.Features;

public enum GpuWrite { Ok, NotSupported, Failed }

/// <summary>Writes to the NVIDIA GPU's clock limit (NVML). Calls wake the GPU: only make them while it's awake.</summary>
public interface IGpuClock
{
    GpuWrite SetMaxClock(int mhz);
    GpuWrite ResetClocks();
}

/// <summary>
/// Keeps the GPU's clock cap matching the profile in use without waking the GPU: writes only while it is awake,
/// again after every sleep (the driver can drop the lock when the GPU powers off), and never again once the driver
/// has said the setting isn't supported. A failed write is retried on the next wake or the next change, not on
/// every tick (each try initialises NVML, which can keep the GPU from sleeping). Hardware thread only.
/// </summary>
public sealed class GpuClockState
{
    private readonly IGpuClock _gpu;
    private bool _awake, _attempted;
    private int? _attemptedMhz;

    public GpuClockState(IGpuClock gpu) => _gpu = gpu;

    public bool Supported { get; private set; } = true;
    /// <summary>True once the GPU has been seen awake (before that, the UI says settings apply when it wakes).</summary>
    public bool EverAwake { get; private set; }
    /// <summary>Outcome of the last write; null before any.</summary>
    public GpuWrite? LastResult { get; private set; }
    /// <summary>False only after a successful reset: until then a cap may be in force (ours, or left by an earlier session).</summary>
    public bool CapMayBeActive { get; private set; } = true;

    /// <summary>Something else changed the clocks: write again on the next update.</summary>
    public void Forget() => _attempted = false;

    /// <returns>True when the GPU was called (the caller may then release NVML).</returns>
    public bool Update(int? desiredMhz, bool awake)
    {
        var woke = awake && !_awake;
        _awake = awake;
        if (!awake || !Supported) return false;
        EverAwake = true;
        var changed = !_attempted || _attemptedMhz != desiredMhz;
        if (!woke && !changed) return false;
        _attempted = true;
        _attemptedMhz = desiredMhz;
        if (desiredMhz is null && !CapMayBeActive) return false; // nothing to undo: don't touch the GPU
        Record(desiredMhz is int m ? _gpu.SetMaxClock(m) : _gpu.ResetClocks(), desiredMhz is not null);
        return true;
    }

    /// <summary>Restore: remove any cap now (even if the GPU sleeps), unless nothing can be capped.</summary>
    /// <returns>True when the GPU was called.</returns>
    public bool ResetIfCapped()
    {
        if (!Supported || !CapMayBeActive) return false;
        Record(_gpu.ResetClocks(), capSet: false);
        _attempted = false;
        return true;
    }

    private void Record(GpuWrite result, bool capSet)
    {
        LastResult = result;
        if (result == GpuWrite.NotSupported) Supported = false;
        if (result == GpuWrite.Ok) CapMayBeActive = capSet;
    }
}
