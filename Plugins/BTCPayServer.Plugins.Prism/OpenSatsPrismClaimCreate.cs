using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.HostedServices;
using BTCPayServer.Payouts;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Prism;

public class OpenSatsPrismClaimCreate : IPluginHookFilter
{
    private readonly IServiceProvider _serviceProvider;
    public string Hook => "prism-claim-create";

    public OpenSatsPrismClaimCreate(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public async Task<object> Execute(object args)
    {
        if (args is not ClaimRequest claimRequest)
        {
            return args;
        }
        
        if (claimRequest.Destination?.ToString() is not { } args1 || !args1.StartsWith("opensats")) return args;

        try
        {
           
            var parts = args1.Split(":", StringSplitOptions.RemoveEmptyEntries);
            var project = "opensats";
            var paymentMethod = PayoutTypes.LN.GetPayoutMethodId("BTC");
            if (parts.Length > 1)
            {
                project = parts[1];
            }

            if (parts.Length > 2)
            {
                paymentMethod = PayoutMethodId.Parse(parts[2]);
            }
            
            
           _serviceProvider.GetService<PayoutMethodHandlerDictionary>().TryGetValue(paymentMethod, out var handler);
            if (handler is null)
            {
                return null;
            }

            var httpClientFactory = _serviceProvider.GetRequiredService<IHttpClientFactory>();
            var httpClient = httpClientFactory.CreateClient("opensats");
            
            var content = new StringContent(JObject.FromObject(new
            {
                btcpay = project,
                name = "kukks <3 you"
            }).ToString(), Encoding.UTF8, "application/json");
            var result = await httpClient.PostAsync("https://opensats.org/api/btcpay",content).ConfigureAwait(false);

            var rawInvoice = JObject.Parse(await result.Content.ReadAsStringAsync().ConfigureAwait(false));
            var invoiceUrl = $"{rawInvoice.Value<string>("checkoutLink").TrimEnd('/')}/{paymentMethod}/status";
            var invoiceBtcpayModel = JObject.Parse(await httpClient.GetStringAsync(invoiceUrl).ConfigureAwait(false));
            var destination = invoiceBtcpayModel.Value<string>("btcAddress");
            var receiptLink = invoiceBtcpayModel.Value<string>("receiptLink");
           
            var claimDestination = await handler.ParseClaimDestination(destination, CancellationToken.None);
            if (claimDestination.destination is null)
            {

                return null;
            }
            claimRequest.Metadata = JObject.FromObject(new
            {
                Source = $"Prism->OpenSats ({project}",
                SourceLink = receiptLink
            });

            claimRequest.Destination = claimDestination.destination;
            claimRequest.PayoutMethodId = paymentMethod;

            return claimRequest;

        }
        catch (Exception)
        {

            return null;
        }
    }
}