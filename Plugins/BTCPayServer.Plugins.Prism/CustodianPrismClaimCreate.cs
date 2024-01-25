using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Custodians;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Data;
using BTCPayServer.HostedServices;
using BTCPayServer.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Prism;

public class CustodianPrismClaimCreate : IPluginHookFilter
{
    private readonly IServiceProvider _serviceProvider;
    public string Hook => "prism-claim-create";

    public CustodianPrismClaimCreate(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public async Task<object> Execute(object args)
    {
        if (args is not ClaimRequest claimRequest)
        {
            return args;
        }
        
        if (claimRequest.Destination?.ToString() is not { } args1 || !args1.StartsWith("custodian:")) return args;

        try
        {

            var custodianDestination = JObject.Parse(args1.Substring("custodian:".Length))
                .ToObject<CustodianDestinationValidator.CustodianDestination>();
            var custodianPaymentMethod = custodianDestination.PaymentMethod is null
                ? new PaymentMethodId("BTC", PaymentTypes.LightningLike)
                : PaymentMethodId.Parse(custodianDestination.PaymentMethod);

            await using var ctx = _serviceProvider.GetRequiredService<ApplicationDbContextFactory>().CreateContext();
            var custodianAccountData = await ctx.CustodianAccount.SingleOrDefaultAsync(data => data.Id == custodianDestination.CustodianId);
            if (custodianAccountData is null)
            {
                return null;
            }

            var custdodian = _serviceProvider.GetServices<ICustodian>().GetCustodianByCode(custodianAccountData.CustodianCode);
            if (custdodian is null)
            {
                return null;
            }

            if (custdodian is not  ICanDeposit canDeposit ||
                canDeposit.GetDepositablePaymentMethods() is { } paymentMethods &&
                paymentMethods.Any(s => PaymentMethodId.TryParse(s) == custodianPaymentMethod))
            {
                return null;
            }

            var handler = _serviceProvider.GetServices<IPayoutHandler>().FindPayoutHandler(custodianPaymentMethod);
            if (handler is null)
            {
                return null;
            }

            var config = custodianAccountData.GetBlob();
            config["depositAddressConfig"] = JToken.FromObject(new
            {
                amount = claimRequest.Value
            });
            var depositAddressAsync =
                await canDeposit.GetDepositAddressAsync(custodianPaymentMethod.ToString(),config, CancellationToken.None);
            if (depositAddressAsync.Address is null)
            {
                return null;
            }

            var claimDestination = await handler.ParseClaimDestination(custodianPaymentMethod,
                depositAddressAsync.Address, CancellationToken.None);
            if (claimDestination.destination is null)
            {

                return null;
            }


            claimRequest.Destination = claimDestination.destination;
            claimRequest.PaymentMethodId = custodianPaymentMethod;

            return claimRequest;

        }
        catch (Exception e)
        {

            return null;
        }
    }
}