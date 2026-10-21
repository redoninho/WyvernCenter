namespace WyvernCenter.Core.Monitoring;

/// <summary>
/// Fan speed in RPM from the firmware's tachometer bytes (MSI_AP 2/3 for the CPU fan, 4/5 for the GPU fan), the
/// same formula MSI Center uses: 60,000,000 / (raw × 2 × 62.5) = 480,000 / raw.
/// </summary>
public static class FanRpm
{
    public static int? FromTach(int? lo, int? hi)
    {
        if (lo is not int l || hi is not int h) return null;
        var raw = h * 256 + l;
        return raw > 0 ? 480000 / raw : 0;
    }
}
