#nullable enable
using System.Net.Http;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Lightning;
using BTCPayServer.Logging;
using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.LNURLVerify;

public class LNURLVerifyPlugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    {
        new() { Identifier = nameof(BTCPayServer), Condition = ">=2.4.2" }
    };

    public override void Execute(IServiceCollection services)
    {
        var bootstrap = (services as PluginServiceCollection)?.BootstrapServices;
        var (assets, rejected) = TokenAssets.Parse(bootstrap?.GetService<IConfiguration>()?[TokenAssets.ConfigKey]);
        if (rejected.Count > 0)
            bootstrap?.GetService<Logs>()?.Configuration.LogWarning(
                "LNURL Verify ignores {Codes} in {Key}: a code is 1-16 letters or digits and cannot name an LNURL rail",
                string.Join(", ", rejected), TokenAssets.ConfigKey);
        services.AddSingleton(assets);
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
        services.AddSingleton<TokenActivations>();
        foreach (var code in assets.Codes)
        {
            var c = code;
            services.AddSingleton<IPaymentMethodHandler>(sp => new LnurlTokenPaymentHandler(c, sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<BTCPayNetworkProvider>().BTC.NBitcoinNetwork, sp.GetRequiredService<TokenActivations>()));
            services.AddDefaultPrettyName(TokenAssets.PaymentMethodIdOf(c), c);
            services.AddSingleton<ICheckoutModelExtension>(sp => new LnurlTokenCheckoutExtension(TokenAssets.PaymentMethodIdOf(c),
                sp.GetRequiredService<PaymentMethodHandlerDictionary>(), sp.GetRequiredService<ILogger<LnurlTokenCheckoutExtension>>()));
        }
        services.AddUIExtension("checkout-end", "LNURLVerify/LnurlTokenCheckout");
        services.AddHostedService<LnurlRailRecorder>();
        services.AddHostedService<LnurlRailProvisioner>();
        services.AddSingleton<RailActivationFailures>();
        services.AddSingleton<RailActivationGate>();
        services.AddSingleton<IGlobalCheckoutModelExtension, LnurlRailCheckoutExtension>();
        services.AddUIExtension("checkout-end", "LNURLVerify/LnurlRailsCheckout");
        services.AddUIExtension("store-invoices-payments", "LNURLVerify/LnurlRailPayments");
        services.AddUIExtension("store-integrations-nav", "LNURLVerify/LnurlRailsNav");
        base.Execute(services);
    }
}
