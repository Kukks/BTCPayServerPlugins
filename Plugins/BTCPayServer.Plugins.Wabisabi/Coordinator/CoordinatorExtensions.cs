using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Configuration;
using BTCPayServer.Services;
using Microsoft.Extensions.DependencyInjection;
using WalletWasabi.Affiliation;
using WalletWasabi.WabiSabi.Models.Serialization;

namespace WalletWasabi.Backend.Controllers;

public static class CoordinatorExtensions
{
    public static void AddWabisabiCoordinator(this IServiceCollection services)
    {

        services.AddSingleton<WabisabiCoordinatorService>();
        services.AddTransient(provider =>
        {
            var s = provider.GetRequiredService<WabisabiCoordinatorService>();
            if (!s.Started)
            {
                return null;
            }
            return new WabiSabiController(s.IdempotencyRequestCache, s.WabiSabiCoordinator.Arena,
                s.WabiSabiCoordinator.CoinJoinFeeRateStatStore, s.WabiSabiCoordinator.AffiliationManager);
        });
        services.AddHostedService((sp) => sp.GetRequiredService<WabisabiCoordinatorService>());

        services.AddSingleton<IUIExtension>(new UIExtension("Wabisabi/WabisabiServerNavvExtension", "server-nav"));


        services.AddHttpClient("wabisabi-coordinator-scripts-no-redirect.onion")
            .ConfigurePrimaryHttpMessageHandler(provider =>
            {

                var handler = new Socks5HttpClientHandler(provider.GetRequiredService<BTCPayServerOptions>());
                handler.AllowAutoRedirect = false;
                return handler;
            });
         services.AddHttpClient("wabisabi-coordinator-scripts.onion")
            .ConfigurePrimaryHttpMessageHandler(provider =>
            {
                var handler = new Socks5HttpClientHandler(provider.GetRequiredService<BTCPayServerOptions>());
                handler.AllowAutoRedirect = false;
                return handler;
            });
         
         
         //inside Startup.ConfigureServices
         services.AddControllerBasedJsonInputFormatter(formatter => {
             formatter.ForControllersWithAttribute<UseWasabiJsonInputFormatterAttribute>()
                 .ForActionsWithAttribute<UseWasabiJsonInputFormatterAttribute>()
                 .WithSerializerSettingsConfigurer(settings =>
                 {
                     settings.Converters = JsonSerializationOptions.Default.Settings.Converters;
                 });
         });
    }
}
