// #nullable enable
// using System;
// using System.Collections.Generic;
// using System.ComponentModel.DataAnnotations;
// using System.Linq;
// using System.Net;
// using System.Net.Http;
// using System.Net.Sockets;
// using System.Threading;
// using System.Threading.Tasks;
// using BTCPayServer.Abstractions.Custodians;
// using BTCPayServer.Abstractions.Form;
// using BTCPayServer.Client.Models;
// using BTCPayServer.Forms;
// using BTCPayServer.Payments;
// using NBitcoin;
// using Newtonsoft.Json.Linq;
//
// namespace BTCPayServer.Plugins.Bringin;
//
// public class BringinApiKeyFormComponentProvider:FormComponentProviderBase
// {
//     private readonly IHttpClientFactory _httpClientFactory;
//     public override string View => "Bringin/ApiKeyElement";
//
//     public BringinApiKeyFormComponentProvider(IHttpClientFactory httpClientFactory)
//     {
//         _httpClientFactory = httpClientFactory;
//     }
//     
//     public override void Validate(Form form, Field field)
//     {
//         if (field.Required)
//         {
//             ValidateField<RequiredAttribute>(field);
//         }
//         if(field.ValidationErrors.Any())
//             return;
//         var httpClient = _httpClientFactory.CreateClient("bringin");
//         httpClient.BaseAddress = new Uri("https://dev.bringin.xyz");
//         httpClient.DefaultRequestHeaders.TryAddWithoutValidation("api-key", GetValue(form, field));
//         try
//         {
//            var userId =  new BringinClient(GetValue(form, field), httpClient).GetUserId().GetAwaiter().GetResult();
//            if(userId is null)
//                field.ValidationErrors.Add("Invalid API Key");
//         }
//         catch (Exception e)
//         {
//             field.ValidationErrors.Add("Invalid API Key");
//         }
//     }
//
//     public override void Register(Dictionary<string, IFormComponentProvider> typeToComponentProvider)
//     {
//         typeToComponentProvider.Add("bringin-apikey", this);
//     }
// }
//
//
// public class BringinCustodian : ICustodian, ICanDeposit
// {
//     private readonly IHttpClientFactory _httpClientFactory;
//     private readonly FormDataService _formDataService;
//     public string Code => "bringin";
//
//     public string Name => "Bringin";
//
//     public BringinCustodian(IHttpClientFactory httpClientFactory, FormDataService formDataService)
//     {
//         _httpClientFactory = httpClientFactory;
//         _formDataService = formDataService;
//     }
//
//     public async Task<Dictionary<string, decimal>> GetAssetBalancesAsync(JObject config,
//         CancellationToken cancellationToken)
//     {
//         var bringinClient = FromConfig(config);
//         if (bringinClient is null)
//             throw new BadConfigException(Array.Empty<string>());
//             
//         var balance = await bringinClient.GetFiatBalance();
//         return new Dictionary<string, decimal>()
//         {
//             {"EUR",  balance}
//         };
//     }
//
//     public async Task<Form> GetConfigForm(JObject config, CancellationToken cancellationToken = default)
//     {
//
//         var f = new Form();
//         
//         var fieldset = Field.CreateFieldset();
//         fieldset.Label = "Connection details";
//         fieldset.Fields.Add(new Field()
//         {
//             Name = "apiKey",
//             Label = "API Key",
//             Type = "bringin-apikey",
//             Value = config["apiKey"]?.Value<string>(),
//             Required = true,
//             HelpText = "Enter your Bringin API Key which can be obtained from <a href=\"https://dev-app.bringin.xyz\" target=\"_blank\">here</a>"
//         });
//         
//         var fieldset2 = Field.CreateFieldset();
//         fieldset.Label = "Conversion details";
//         
//         // fieldset.Fields.Add(new Field()
//         // {
//         //     Name = "server",
//         //     Label = "Bringin Server (optional)",
//         //     Type = "password",
//         //     Value = config["server"]?.Value<string>(),
//         //     Required = false,
//         //     OriginalValue = "https://dev.bringin.xyz",
//         //     HelpText = "Enter the Bringin server URL. This is optional and defaults to https://dev.bringin.xyz"
//         // });
//         //
//         f.Fields.Add(fieldset);
//         return f;
//     }
//
//     public async Task<DepositAddressData> GetDepositAddressAsync(string paymentMethod, JObject config,
//         CancellationToken cancellationToken)
//     {
//         var amount = config["depositAddressConfig"]?.Value<decimal>();
//
//         var bringinClient = FromConfig(config);
//         if (bringinClient is null)
//             throw new Exception("Invalid API Key");
//         if (amount is null or <= 0)
//         {
//             var rate = await bringinClient.GetRate();
//             //rate.bringinRate is the price for 1 BTC in EUR
//             //100eur = 1/rate.bringinRate BTC
//             amount = 100m / rate.BringinPrice;
//         }
//         // var rate = await bringinClient.GetRate();
//         var host = await Dns.GetHostEntryAsync(Dns.GetHostName(), cancellationToken);
//         var ipToUse = host.AddressList.FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork)?.ToString();
//         var request = new BringinClient.CreateOrderRequest()
//         {
//             SourceAmount = Money.Coins(amount.Value).Satoshi,
//             IP = ipToUse,
//             PaymentMethod = "LIGHTNING"
//         };
//         var order = await bringinClient.PlaceOrder(request);
//         return new DepositAddressData()
//         {
//             Address = order.Invoice
//         };
//     }
//
//     public string[] GetDepositablePaymentMethods()
//     {
//         return new[] {new PaymentMethodId("BTC", LightningPaymentType.Instance).ToString()};
//     }
//
//     private BringinClient? FromConfig(JObject config)
//     {
//         Uri backend = new Uri("https://dev.bringin.xyz");
//         if (config.TryGetValue("apiKey", out var apiKey))
//         {
//             // if (config.TryGetValue("server", out var serverToken) && serverToken.Value<string>() is { } server &&
//             //     !string.IsNullOrEmpty(server))
//             // {
//             //     backend = new Uri(server);
//             // }
//
//             Uri backend = new Uri("https://dev.bringin.xyz");
//             var httpClient = _httpClientFactory.CreateClient("bringin");
//             httpClient.BaseAddress = backend;
//             
//             httpClient.DefaultRequestHeaders.TryAddWithoutValidation("api-key", apiKey.Value<string>());
//             return new BringinClient(apiKey.Value<string>(), httpClient);
//         }
//
//         return null;
//     }
// }