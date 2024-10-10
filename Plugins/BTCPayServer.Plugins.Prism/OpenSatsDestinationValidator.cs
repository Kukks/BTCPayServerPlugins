using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Payments;
using BTCPayServer.Payouts;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Prism;

public class OpenSatsDestinationValidator : IPluginHookFilter
{
    private readonly IServiceProvider _serviceProvider;
    public string Hook => "prism-destination-validate";

    public OpenSatsDestinationValidator(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public async Task<object> Execute(object args)
    {
        var result = new PrismDestinationValidationResult();
        if (args is not string args1 || !args1.StartsWith("opensats", StringComparison.InvariantCultureIgnoreCase)) return args;

        try
        {
            
            var parts = args1.ToLowerInvariant().Split(":", StringSplitOptions.RemoveEmptyEntries);
            var project = "general_fund";
            var paymentMethod = PayoutTypes.LN.GetPayoutMethodId("BTC");
            if (parts.Length > 1)
            {
                project = parts[1];
            }

            if (parts.Length > 2)
            {
                paymentMethod = PayoutMethodId.Parse(parts[2]);
            }


            if (_serviceProvider.GetService<PayoutMethodHandlerDictionary>().TryGetValue(paymentMethod, out var handler))
            {
                result.Success = false;
            }

            var httpClientFactory = _serviceProvider.GetRequiredService<IHttpClientFactory>();
            var httpClient = httpClientFactory.CreateClient("opensats");
            
            var content = new StringContent(JObject.FromObject(new
            {
                btcpay = project,
                name = "kukks <3 you"
            }).ToString(), Encoding.UTF8, "application/json");
            var xResult = await httpClient.PostAsync("https://opensats.org/api/btcpay",content).ConfigureAwait(false);

            var rawInvoice = JObject.Parse(await xResult.Content.ReadAsStringAsync().ConfigureAwait(false));
            var invoiceUrl = $"{rawInvoice.Value<string>("checkoutLink").TrimEnd('/')}/{paymentMethod}/status";
            var invoiceBtcpayModel = JObject.Parse(await httpClient.GetStringAsync(invoiceUrl).ConfigureAwait(false));
            var destination = invoiceBtcpayModel.Value<string>("btcAddress");
           
            var claimDestination = await handler.ParseClaimDestination(destination, CancellationToken.None);
            if (claimDestination.destination is null)
            {

                result.Success = false;
            }
           

            result.Success = true;
            result.PayoutMethodId = paymentMethod;
            return result;
        }
        catch (Exception e)
        {
            result.Success = false;
            return result;
        }
    }


    public class CustodianDestination
    {
        public string CustodianId { get; set; }
        public string PaymentMethod { get; set; }

        override public string ToString()
        {
            return $"custodian:{JObject.FromObject(this)}";
        }
    }
}