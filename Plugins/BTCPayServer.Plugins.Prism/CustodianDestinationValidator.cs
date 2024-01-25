using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Custodians;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Services.Custodian.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Prism;

public class CustodianDestinationValidator : IPluginHookFilter
{
    private readonly IServiceProvider _serviceProvider;
    public string Hook => "prism-destination-validate";

    public CustodianDestinationValidator(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public async Task<object> Execute(object args)
    {
        var result = new PrismDestinationValidationResult();
        if (args is not string args1 || !args1.StartsWith("custodian:")) return args;

        try
        {
            var custodianDestination =
                JObject.Parse(args1.Substring("custodian:".Length)).ToObject<CustodianDestination>();
            var custodianPaymentMethod = custodianDestination.PaymentMethod is null
                ? new PaymentMethodId("BTC", PaymentTypes.LightningLike)
                : PaymentMethodId.Parse(custodianDestination.PaymentMethod);

            result.PaymentMethod = custodianPaymentMethod;
            await using var ctx = _serviceProvider.GetService<ApplicationDbContextFactory>().CreateContext();
            var custodianAccountData = ctx.CustodianAccount.SingleOrDefault(data => data.Id == custodianDestination.CustodianId);
            if (custodianAccountData is null)
            {
                result.Success = false;
                return result;
            }

            var custdodian = _serviceProvider.GetServices<ICustodian>().GetCustodianByCode(custodianAccountData.CustodianCode);
            if (custdodian is null)
            {
                result.Success = false;
                return result;
            }

            if (custdodian is ICanDeposit canDeposit &&
                canDeposit.GetDepositablePaymentMethods() is { } paymentMethods &&
                paymentMethods.Any(s => PaymentMethodId.TryParse(s) == custodianPaymentMethod))
            {
                result.Success = true;
                return result;
            }

            result.Success = false;
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