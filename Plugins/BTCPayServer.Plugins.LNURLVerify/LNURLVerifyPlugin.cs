#nullable enable
using System.Net.Http;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BTCPayServer.Plugins.LNURLVerify;

public class LNURLVerifyPlugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    {
        new() { Identifier = nameof(BTCPayServer), Condition = ">=2.4.2" }
    };

    public override void Execute(IServiceCollection services)
    {
        services.AddUIExtension("ln-payment-method-setup-tab", "LNURLVerify/LNPaymentMethodSetupTab");
        services.AddSingleton<LNURLVerifyConnectionStringHandler>();
        services.AddSingleton<ILightningConnectionStringHandler>(sp =>
            sp.GetRequiredService<LNURLVerifyConnectionStringHandler>());
        services.AddSingleton<LNURLVerifyPollerService>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<LNURLVerifyPollerService>());
        foreach (var rail in LnurlRails.All)
        {
            var r = rail;
            services.AddSingleton<IPaymentMethodHandler>(sp => new LnurlRailPaymentHandler(r,
                sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<BTCPayNetworkProvider>().BTC.NBitcoinNetwork));
            services.AddDefaultPrettyName(r.PaymentMethodId, r.PrettyName);
        }
        services.AddHostedService<LnurlRailRecorder>();
        services.AddHostedService<LnurlRailProvisioner>();
        services.AddSingleton<RailActivationFailures>();
        services.AddSingleton<RailActivationGate>();
        services.AddSingleton<IGlobalCheckoutModelExtension, LnurlRailCheckoutExtension>();
        services.AddUIExtension("checkout-end", "LNURLVerify/LnurlRailsCheckout");
        services.AddUIExtension("store-invoices-payments", "LNURLVerify/LnurlRailPayments");
        base.Execute(services);
    }
}
