using System;
using System.Net.Http;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Data;
using BTCPayServer.Data.Payouts.LightningLike;
using BTCPayServer.Lightning;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.SideShift
{
    public class SideShiftPlugin : BaseBTCPayServerPlugin
    {
        public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
        {
            new() {Identifier = nameof(BTCPayServer), Condition = ">=1.10.0"}
        };

        public override void Execute(IServiceCollection applicationBuilder)
        {
            applicationBuilder.AddSingleton<SideShiftService>();
            applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("SideShift/SideShiftNav",
                "store-integrations-nav"));
            applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("SideShift/PullPaymentViewInsert",
                "pullpayment-foot"));
            applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("SideShift/StoreIntegrationSideShiftOption",
                "store-integrations-list"));
            // Checkout v2
            applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("SideShift/CheckoutPaymentMethodExtension",
                "checkout-payment-method"));
            applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("SideShift/CheckoutPaymentExtension",
                "checkout-payment"));
            // Checkout Classic
            applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("SideShift/CheckoutContentExtension",
                "checkout-bitcoin-post-content"));
            applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("SideShift/CheckoutContentExtension",
                "checkout-lightning-post-content"));
            applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("SideShift/CheckoutTabExtension",
                "checkout-bitcoin-post-tabs"));
            applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("SideShift/CheckoutTabExtension",
                "checkout-lightning-post-tabs"));
            applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("SideShift/CheckoutEnd",
                "checkout-end"));
            applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("SideShift/PrismEnhance",
                "prism-edit"));
            applicationBuilder.AddSingleton<IPluginHookFilter, PrismDestinationValidate>();
            applicationBuilder.AddSingleton<IPluginHookFilter, PrismClaimDestination>();
            base.Execute(applicationBuilder);
        }
    }


    public class PrismSideshiftDestination
    {
        public string ShiftCoin { get; set; }
        public string ShiftNetwork { get; set; }
        public string ShiftDestination { get; set; }
        public string ShiftMemo { get; set; }

        public bool Valid()
        {
            return !string.IsNullOrEmpty(ShiftCoin) && !string.IsNullOrEmpty(ShiftNetwork) &&
                   !string.IsNullOrEmpty(ShiftDestination);
        }
    }

    public class PrismDestinationValidate : IPluginHookFilter
    {
        public string Hook => "prism-destination-validate";
        public async Task<object> Execute(object args)
        {
            if (args is not string args1 || !args1.StartsWith("sideshift:")) return args;
            var json  = JObject.Parse(args1.Substring("sideshift:".Length)).ToObject<PrismSideshiftDestination>();
            return json.Valid();
        }

    }

    public class PrismClaimDestination : IPluginHookFilter
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly BTCPayNetworkProvider _networkProvider;
        public string Hook => "prism-claim-destination";

        public PrismClaimDestination(IHttpClientFactory httpClientFactory, BTCPayNetworkProvider networkProvider)
        {
            _httpClientFactory = httpClientFactory;
            _networkProvider = networkProvider;
        }
        public async Task<object> Execute(object args)
        {
            var network = _networkProvider.GetNetwork<BTCPayNetwork>("BTC");
            if (args is  not string s || network is null)
            {
                return Task.FromResult(args);
            }
            if (args is not string args1 || !args1.StartsWith("sideshift:")) return args;
            var request  = JObject.Parse(args1.Substring("sideshift:".Length)).ToObject<PrismSideshiftDestination>();
            if (!request.Valid())
            {
                return null;
            }
            var client = _httpClientFactory.CreateClient("sideshift");
            
            
            var shiftResponse = await client.PostAsJsonAsync("https://sideshift.ai/api/v2/shifts/variable", new
                {
                    settleAddress = request.ShiftDestination,
                    affiliateId = "qg0OrfHJV",
                    settleMemo = request.ShiftMemo,
                    depositCoin = "BTC",
                    depositNetwork = "lightning",
                    settleCoin = request.ShiftCoin,
                    settleNetwork = request.ShiftNetwork,
                }
            );
            if (!shiftResponse.IsSuccessStatusCode)
            {
                return null;
            }
            var shift = await shiftResponse.Content.ReadAsAsync<SideShiftController.ShiftResponse>();
            try
            {
                LNURL.LNURL.Parse(shift.depositAddress, out var lnurl);
                return new LNURLPayClaimDestinaton(shift.depositAddress);
            }
            catch (Exception e)
            {
                if (BOLT11PaymentRequest.TryParse(shift.depositAddress, out var bolt11,  network.NBitcoinNetwork))
                {
                    return new BoltInvoiceClaimDestination(shift.depositAddress, bolt11);
                }
            }

            return null;

        }
    }

    public class SideShiftAvailableCoin
    {
        public string coin { get; set; }
        public string[] networks { get; set; }
        public string name { get; set; }
        public bool hasMemo { get; set; }
        public JToken fixedOnly { get; set; }
        public JToken variableOnly { get; set; }
        public JToken settleOffline { get; set; }
    }
}