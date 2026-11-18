using WyvernCenter.Core.Features;

namespace WyvernCenter.Tests;

public class ChargeTopUpTests
{
    [Fact]
    public void BelowLimitAndNotCharging_RaisesThenPutsBackAtTheLimit()
    {
        var t = new ChargeTopUp();
        Assert.Equal(79, t.Next(target: 70, battery: 68, onAc: true, charging: false)); // 68 < 79-10: EC starts charging
        Assert.Null(t.Next(70, 69, true, true));                                         // charging: wait
        Assert.Equal(70, t.Next(70, 70, true, true));                                    // reached: back to 70, EC stops
        Assert.Null(t.RaisedTo);
        Assert.Null(t.Next(70, 70, true, false));                                        // holding at the limit
    }

    [Fact]
    public void Unplugged_PutsTheLimitBackAndDoesNothingElse()
    {
        var t = new ChargeTopUp();
        Assert.Null(t.Next(75, 72, onAc: false, charging: false));
        Assert.Equal(83, t.Next(75, 72, true, false));
        Assert.Equal(75, t.Next(75, 73, onAc: false, charging: false));
    }

    [Theory]
    [InlineData(75, 75, false)]  // at the limit
    [InlineData(75, 80, false)]  // above it
    [InlineData(75, 74, false)]  // 1 % below: not worth a top-up
    [InlineData(75, 60, false)]  // more than 10 below: the EC charges by itself
    [InlineData(75, 72, true)]   // already charging
    [InlineData(100, 95, false)] // no limit
    public void NoChange(int target, int battery, bool charging) =>
        Assert.Null(new ChargeTopUp().Next(target, battery, onAc: true, charging));

    [Fact]
    public void NeverRaisesPast100()
    {
        var t = new ChargeTopUp();
        Assert.Equal(100, t.Next(95, 92, true, false));
    }
}
