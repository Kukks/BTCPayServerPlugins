using System;
using System.Linq;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Configuration;
using BTCPayServer.Hosting;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Altcoins;
using BTCPayServer.Plugins.LiquidPlus.Services;
using BTCPayServer.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;
using NBXplorer;

namespace BTCPayServer.Plugins.LiquidPlus
{
    public class LiquidPlusPlugin : BaseBTCPayServerPlugin
    {
        public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
        {
            new() {Identifier = nameof(BTCPayServer), Condition = ">=1.12.0"}
        };

        public override void Execute(IServiceCollection applicationBuilder)
        {
            var services = (PluginServiceCollection) applicationBuilder;
            if (services.BootstrapServices.GetRequiredService<NBXplorerNetworkProvider>()
                    .GetFromCryptoCode("LBTC") is null || !services.BootstrapServices.GetRequiredService<SelectedChains>().Contains("LBTC"))
                return;
            services.AddSingleton<IUIExtension>(new UIExtension("LiquidNav", "store-integrations-nav"));
            services.AddSingleton<IUIExtension>(new UIExtension("OnChainWalletSetupLiquidExtension",
                "onchain-wallet-setup-post-body"));
            services.AddSingleton<IUIExtension>(new UIExtension("CustomLiquidAssetsNavExtension", "server-nav"));
            services.AddSingleton<IUIExtension>(new UIExtension("StoreNavLiquidExtension", "store-nav"));
            services.AddSingleton<CustomLiquidAssetsRepository>();


            var config = services.BootstrapServices.GetRequiredService<IConfiguration>();
            DataDirectories dataDirectories = new DataDirectories();
            dataDirectories.Configure(config);

            var repo = new CustomLiquidAssetsRepository(new NullLogger<CustomLiquidAssetsRepository>(),
                new OptionsWrapper<DataDirectories>(dataDirectories));
            var settings = repo.Get();
            var template = (ElementsBTCPayNetwork) services.Single(descriptor =>
                    descriptor.ServiceType == typeof(BTCPayNetworkBase) &&
                    descriptor.ImplementationInstance is ElementsBTCPayNetwork
                    {
                        CryptoCode: "LBTC"
                    })
                .ImplementationInstance;
            var tlProvider = (TransactionLinkProviders.Entry) services.Single(descriptor =>
                    descriptor.ServiceType == typeof(TransactionLinkProviders.Entry) &&
                    descriptor.ImplementationInstance is TransactionLinkProviders.Entry
                    {
                        PaymentMethodId: {CryptoCode: "LBTC"}
                    })
                .ImplementationInstance;
            settings.Items.ForEach(configuration =>

            {
                var code = configuration.CryptoCode
                    .Replace("-", "")
                    .Replace("_", "");
                services.AddBTCPayNetwork(new ElementsBTCPayNetwork()
                {
                    CryptoCode = code,
                    DefaultRateRules = configuration.DefaultRateRules ?? Array.Empty<string>(),
                    AssetId = uint256.Parse(configuration.AssetId),
                    Divisibility = configuration.Divisibility,
                    DisplayName = configuration.DisplayName,
                    CryptoImagePath = configuration.CryptoImagePath,
                    NetworkCryptoCode = template.NetworkCryptoCode,
                    DefaultSettings = template.DefaultSettings,
                    ElectrumMapping = template.ElectrumMapping,
                    BlockExplorerLink = template.BlockExplorerLink,
                    ReadonlyWallet = template.ReadonlyWallet,
                    SupportLightning = false,
                    SupportPayJoin = false,
                    ShowSyncSummary = false,
                    WalletSupported = template.WalletSupported,
                    LightningImagePath = "",
                    NBXplorerNetwork = template.NBXplorerNetwork,
                    CoinType = template.CoinType,
                    VaultSupported = template.VaultSupported,
                    MaxTrackedConfirmation = template.MaxTrackedConfirmation,
                    SupportRBF = template.SupportRBF
                }).AddTransactionLinkProvider(new PaymentMethodId(code, PaymentTypes.BTCLike), tlProvider.Provider);
            });
        }
    }
}