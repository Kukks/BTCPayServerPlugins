using System;
using System.Net.Http;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Data.Payouts.LightningLike;
using BTCPayServer.HostedServices;
using BTCPayServer.Lightning;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.SideShift;

public class PrismClaimCreate : IPluginHookFilter
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly BTCPayNetworkProvider _networkProvider;
    public string Hook => "prism-claim-create";

    public PrismClaimCreate(IHttpClientFactory httpClientFactory, BTCPayNetworkProvider networkProvider)
    {
        _httpClientFactory = httpClientFactory;
        _networkProvider = networkProvider;
    }
    public async Task<object> Execute(object args)
    {
        var network = _networkProvider.GetNetwork<BTCPayNetwork>("BTC");
        if (args is not ClaimRequest claimRequest || network is null)
        {
            return Task.FromResult(args);
        }
        
        if (claimRequest.Destination?.ToString() is not { } args1 || !args1.StartsWith("sideshift:")) return args;
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
            LNURL.LNURL.Parse(shift.depositAddress, out _);
            claimRequest.Destination = new LNURLPayClaimDestinaton(shift.depositAddress);
            claimRequest.Metadata = JObject.FromObject(new
            {
                Source = $"Prism->Sideshift",
                SourceLink = $"https://sideshift.ai/orders/{shift.id}?openSupport=true",
            });
            return claimRequest;
        }
        catch (Exception e)
        {
            if (BOLT11PaymentRequest.TryParse(shift.depositAddress, out var bolt11, network.NBitcoinNetwork))
            {
                claimRequest.Destination =  new BoltInvoiceClaimDestination(shift.depositAddress, bolt11);
                claimRequest.Metadata = JObject.FromObject(new
                {
                    Source = $"Prism->Sideshift",
                    SourceLink = $"https://sideshift.ai/orders/{shift.id}?openSupport=true",
                });
                return claimRequest;
            }
        }

        return null;

    }
}