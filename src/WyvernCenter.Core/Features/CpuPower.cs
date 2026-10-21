namespace WyvernCenter.Core.Features;

/// <summary>Windows processor boost policy (PERFBOOSTMODE): Off, Efficient (efficient aggressive), Aggressive.</summary>
public enum CpuBoost { Off, Efficient, Aggressive }

/// <summary>CPU Tier 1: Windows power-plan settings only. No MSRs, no driver.</summary>
public interface ICpuPower
{
    CpuBoost GetBoost(bool ac);
    /// <summary>Sets boost mode for both AC and battery on the active scheme.</summary>
    void SetBoost(CpuBoost boost);
    int GetMaxState(bool ac);
    void SetMaxState(bool ac, int percent);
    /// <summary>Maximum processor frequency in MHz for AC and battery on the active scheme; 0 = no limit.</summary>
    void SetMaxFrequency(int mhz);
    int GetMaxFrequency(bool ac);
    void SaveOriginalsIfMissing();
    void RestoreOriginals();
}
