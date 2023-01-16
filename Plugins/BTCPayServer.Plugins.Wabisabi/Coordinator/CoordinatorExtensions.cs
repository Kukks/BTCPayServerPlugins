using System;
using System.Net.Http;
using System.Reflection;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Services;
using Microsoft.Extensions.DependencyInjection;
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
            return new WabiSabiController(s.IdempotencyRequestCache, s.WabiSabiCoordinator.Arena,
                s.WabiSabiCoordinator.CoinJoinFeeRateStatStore);
        });
        services.AddHostedService((sp) => sp.GetRequiredService<WabisabiCoordinatorService>());

        services.AddSingleton<IUIExtension>(new UIExtension("Wabisabi/WabisabiServerNavvExtension", "server-nav"));

        Type t = Assembly.GetEntryAssembly().GetType("BTCPayServer.Services.Socks5HttpClientHandler");

        services.AddHttpClient("wabisabi-coordinator-scripts-no-redirect.onion")
            .ConfigurePrimaryHttpMessageHandler(provider =>
            {
                var handler = (HttpClientHandler)ActivatorUtilities.CreateInstance(provider, t);
                handler.AllowAutoRedirect = false;
                return handler;
            });
        
         services.AddHttpClient("wabisabi-coordinator-scripts.onion")
            .ConfigurePrimaryHttpMessageHandler(provider =>
            {
                var handler = (HttpClientHandler)ActivatorUtilities.CreateInstance(provider, t);
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
