using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Rating;
using BTCPayServer.Services.Rates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BTCPayServer.Plugins.Bringin;

public class BringinPlugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    {
        new() {Identifier = nameof(BTCPayServer), Condition = ">=1.12.0"}
    };

    public override void Execute(IServiceCollection applicationBuilder)
    {
        applicationBuilder.AddSingleton<BringinService>();
        applicationBuilder.AddSingleton<IHostedService, BringinService>(s => s.GetService<BringinService>());
        applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("Bringin/BringinDashboardWidget", "dashboard"));
        applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("Bringin/Nav",
            "store-integrations-nav"));
        
        applicationBuilder.AddSingleton<IDynamicRateProvider>(provider =>
        {
            
            return new DynamicRateProvider(BringinRateProvider.BringinRateSourceInfo,
                async (context, _) =>
                {
                    var bringinService = provider.GetRequiredService<BringinService>();
                    var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
                    
                    var settings = await bringinService.Get(context);
                    return new BringinRateProvider(settings, httpClientFactory);
                });
        });
    }
    
    public class BringinRateProvider: IRateProvider
    {
        private readonly BringinService.BringinStoreSettings _settings;
        private readonly IHttpClientFactory _httpClientFactory;

        public BringinRateProvider(BringinService.BringinStoreSettings settings, IHttpClientFactory httpClientFactory)
        {
            _settings = settings;
            _httpClientFactory = httpClientFactory;
        }

        public static readonly RateSourceInfo BringinRateSourceInfo = new("Bringin", "Bringin", "bringin.xyz");
        public RateSourceInfo RateSourceInfo => BringinRateSourceInfo;

        public async Task<PairRate[]> GetRatesAsync(CancellationToken cancellationToken)
        {

            var rate = await _settings.CreateClient(_httpClientFactory).GetRate("BTCEUR",cancellationToken);
           
            return new[]
            {
                new PairRate(new CurrencyPair("BTC", "EUR"), new BidAsk(rate.BringinPrice))
            };
        }
    }
}