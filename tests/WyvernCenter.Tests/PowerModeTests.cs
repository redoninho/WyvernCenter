using WyvernCenter.Core.Features;

namespace WyvernCenter.Tests;

public sealed class FakeOverlay : IPowerOverlay
{
    public PowerOverlay? Current { get; private set; }
    public bool Throw { get; set; }
    public PowerOverlay? Get() => Current;
    public void Set(PowerOverlay o)
    {
        if (Throw) throw new InvalidOperationException("overlay api missing");
        Current = o;
    }
}

public class PowerModeTests
{
    [Theory]
    [InlineData(PowerOverlay.Efficiency, "961cc777-2547-4f9d-8174-7d86181b8a7a")]
    [InlineData(PowerOverlay.Balanced, "00000000-0000-0000-0000-000000000000")]
    [InlineData(PowerOverlay.Performance, "ded574b5-45a0-4f42-8737-46345c09c238")]
    public void Overlay_GuidMapping(PowerOverlay o, string guid)
    {
        Assert.Equal(Guid.Parse(guid), WindowsPowerOverlay.ToGuid(o));
        Assert.Equal(o, WindowsPowerOverlay.FromGuid(Guid.Parse(guid)));
    }

    [Fact]
    public void Overlay_UnknownGuidIsNull() => Assert.Null(WindowsPowerOverlay.FromGuid(Guid.NewGuid()));
}
