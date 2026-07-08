using BTCPayServer.Plugins.Electrum.Services;
using Xunit;

public class EffectiveStatusTests
{
    [Theory]
    [InlineData(true,  true,  true)]
    [InlineData(true,  false, true)]   // NBX synced, Electrum down -> available
    [InlineData(false, true,  true)]   // NBX syncing, Electrum up  -> available (the P1-review gap)
    [InlineData(false, false, false)]  // both down -> not available
    public void EffectiveSynced(bool nbx, bool electrum, bool expected)
        => Assert.Equal(expected, ElectrumStatusMonitor.EffectiveSynced(nbx, electrum));
}
