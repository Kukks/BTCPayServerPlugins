using Xunit;

namespace BTCPayServer.Plugins.LNURLVerify.Tests;

public class TokenActivationsTests
{
    static readonly BTCPayServer.Payments.PaymentMethodId Usdt = TokenAssets.PaymentMethodIdOf("USDT");

    [Fact]
    public async Task Activations_of_one_invoice_and_asset_never_overlap()
    {
        var activations = new TokenActivations();
        var running = 0;
        var overlapped = false;
        async Task<bool> Activate()
        {
            if (Interlocked.Increment(ref running) > 1) overlapped = true;
            await Task.Delay(50);
            Interlocked.Decrement(ref running);
            return true;
        }
        await Task.WhenAll(activations.Run("inv", Usdt, new[] { "a" }, Activate), activations.Run("inv", Usdt, new[] { "b" }, Activate));
        Assert.False(overlapped);
    }

    [Fact]
    public async Task A_run_hands_its_networks_to_the_activation_it_starts()
    {
        var activations = new TokenActivations();
        IReadOnlyCollection<string>? seen = null;
        await activations.Run("inv", Usdt, new[] { "usdt-arbitrum" }, () =>
        {
            seen = activations.Take("inv", Usdt);
            return Task.FromResult(true);
        });
        Assert.Equal(new[] { "usdt-arbitrum" }, seen);
        Assert.Empty(activations.Take("inv", Usdt));
    }
}
