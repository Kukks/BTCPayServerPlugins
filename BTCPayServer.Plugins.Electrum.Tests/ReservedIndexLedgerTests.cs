using BTCPayServer.Plugins.Electrum.Services;
using Xunit;

public class ReservedIndexLedgerTests
{
    [Theory]
    [InlineData(-1, 0, 0)]
    [InlineData(5, 3, 5)]
    [InlineData(5, 7, 7)]
    [InlineData(-1, -1, -1)]
    public void Merge_keeps_the_high_water(int current, int observed, int expected)
        => Assert.Equal(expected, ReservedIndexLedger.Merge(current, observed));
}
