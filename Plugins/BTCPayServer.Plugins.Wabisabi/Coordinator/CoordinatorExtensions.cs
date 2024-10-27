using System;
using System.Buffers;
using BTCPayServer;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Configuration;
using BTCPayServer.Plugins.Wabisabi.Coordinator;
using BTCPayServer.Services;
using Microsoft.AspNetCore.JsonPatch;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using Microsoft.Extensions.Options;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WalletWasabi.WabiSabi.Backend;
using WalletWasabi.WabiSabi.Models.Serialization;

namespace WalletWasabi.Backend.Controllers;

public static class CoordinatorExtensions
{
    public static void AddWabisabiCoordinator(this IServiceCollection services)
    {
        services.AddSingleton<WabisabiCoordinatorService>();
        services.AddSingleton<WabiSabiConfig.CoordinatorScriptResolver, WabisabiScriptResolver>();
        services.AddTransient(provider =>
        {
            var s = provider.GetRequiredService<WabisabiCoordinatorService>();
            if (!s.Started)
            {
                return null;
            }

            return new WabiSabiController(s.IdempotencyRequestCache, s.WabiSabiCoordinator.Arena,
                s.WabiSabiCoordinator.CoinJoinFeeRateStatStore);
        });
        services.AddHostedService((sp) => sp.GetRequiredService<WabisabiCoordinatorService>());

        services.AddUIExtension("server-nav", "Wabisabi/WabisabiServerNavvExtension");


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

        services.ConfigureOptions<WasabiInputFormatterJsonMvcOptionsSetup>();
    }
}

public class WasabiInputFormatter : NewtonsoftJsonInputFormatter
{
    public WasabiInputFormatter(ILogger logger, JsonSerializerSettings serializerSettings, ArrayPool<char> charPool,
        ObjectPoolProvider objectPoolProvider, MvcOptions options, MvcNewtonsoftJsonOptions jsonOptions) : base(logger,
        serializerSettings, charPool, objectPoolProvider, options, jsonOptions)
    {
    }

    public override bool CanRead(InputFormatterContext context)
    {
        var controllerName = context.HttpContext.Request.RouteValues["controller"]?.ToString();
        return controllerName == "WabiSabi";
    }
}

public class WasabiOutputFormatter : NewtonsoftJsonOutputFormatter
{
    public WasabiOutputFormatter(JsonSerializerSettings serializerSettings, ArrayPool<char> charPool,
        MvcOptions options, MvcNewtonsoftJsonOptions jsonOptions) : base(serializerSettings, charPool, options,
        jsonOptions)
    {
    }

    public override bool CanWriteResult(OutputFormatterCanWriteContext context)
    {
        var controllerName = context.HttpContext.Request.RouteValues["controller"]?.ToString();
        return controllerName == "WabiSabi";
    }
}

internal sealed class WasabiInputFormatterJsonMvcOptionsSetup : IConfigureOptions<MvcOptions>
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly MvcNewtonsoftJsonOptions _jsonOptions;
    private readonly ArrayPool<char> _charPool;
    private readonly ObjectPoolProvider _objectPoolProvider;
    private readonly JsonSerializerSettings _settings;

    public WasabiInputFormatterJsonMvcOptionsSetup(
        ILoggerFactory loggerFactory,
        IOptions<MvcNewtonsoftJsonOptions> jsonOptions,
        ArrayPool<char> charPool,
        ObjectPoolProvider objectPoolProvider)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(jsonOptions);
        ArgumentNullException.ThrowIfNull(charPool);
        ArgumentNullException.ThrowIfNull(objectPoolProvider);

        _loggerFactory = loggerFactory;
        _jsonOptions = jsonOptions.Value;
        _charPool = charPool;
        _objectPoolProvider = objectPoolProvider;

        _settings = JsonSerializationOptions.Default.Settings;
    }

    public void Configure(MvcOptions options)
    {
        options.ModelMetadataDetailsProviders.Add(new SuppressChildValidationMetadataProvider(typeof(BitcoinAddress)));
        options.ModelMetadataDetailsProviders.Add(new SuppressChildValidationMetadataProvider(typeof(Script)));

        options.InputFormatters.Insert(0, new WasabiInputFormatter(
            _loggerFactory.CreateLogger<WasabiInputFormatter>(), 
            _settings,
            _charPool,
            _objectPoolProvider,
            options,
            _jsonOptions));
        options.OutputFormatters.Insert(0, new WasabiOutputFormatter(
            _settings,
            _charPool,
            options,
            _jsonOptions));
    }
}