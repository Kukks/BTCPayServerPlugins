using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Lightning;
using NBitcoin;

namespace BTCPayServer.Plugins.MicroNode;

public class MicroLightningClient:ILightningClient
{
    private readonly ILightningClient _innerClient;
    private readonly MicroNodeService _microNodeService;
    private readonly Network _network;
    private readonly string _key;

    public MicroLightningClient(ILightningClient innerClient,MicroNodeService microNodeService, Network network ,string key)
    {
        _innerClient = innerClient;
        _microNodeService = microNodeService;
        _network = network;
        _key = key;
    }

    public override string ToString()
    {
        return $"type=micro;key={_key}";
    }

    public async Task<LightningInvoice> GetInvoice(string invoiceId, CancellationToken cancellation = new CancellationToken())
    {
        var result = await _microNodeService.MatchRecord(_key, invoiceId);
        if(result is null)
        {
            return null;
        }
        return await _innerClient.GetInvoice(invoiceId, cancellation);
    }

    public async Task<LightningInvoice> GetInvoice(uint256 paymentHash, CancellationToken cancellation = new CancellationToken())
    {
        return await GetInvoice(paymentHash.ToString(), cancellation);
    }

    public async Task<LightningInvoice[]> ListInvoices(CancellationToken cancellation = new CancellationToken())
    {
        var result = await _innerClient.ListInvoices(cancellation);
        var matchedRecords = await _microNodeService.MatchRecords(_key, result?.Select(r => r.Id).ToArray());
        var ids = matchedRecords.Select(r => r.Id).ToArray();
        return result.Where(r => ids.Contains(r.Id)).ToArray();
    }

    public async Task<LightningInvoice[]> ListInvoices(ListInvoicesParams request, CancellationToken cancellation = new CancellationToken())
    {
        var result = await _innerClient.ListInvoices(request, cancellation);
        var matchedRecords = await _microNodeService.MatchRecords(_key, result?.Select(r => r.Id).ToArray());
        var ids = matchedRecords.Select(r => r.Id).ToArray();
        return result.Where(r => ids.Contains(r.Id)).ToArray();
    }

    public async Task<LightningPayment> GetPayment(string paymentHash, CancellationToken cancellation = new CancellationToken())
    {
        var result = await _microNodeService.MatchRecord(_key, paymentHash);
        if(result is null)
        {
            return null;
        }
        //
        // if(result.Type == "InternalPayment")
        // {
        //     return FromInternalPayment(result);
        // }
        var payment =  await _innerClient.GetPayment(paymentHash, cancellation);
        await _microNodeService.UpsertRecord(_key, payment);
        return payment;
    }

    public async Task<LightningPayment[]> ListPayments(CancellationToken cancellation = new CancellationToken())
    {
        var result = await _innerClient.ListPayments(cancellation);
        var matchedRecords = await _microNodeService.MatchRecords(_key, result?.Select(r => r.Id).ToArray());
        
        // var internalPayments =  matchedRecords.Where(r => r.Type == "InternalPayment").Select(FromInternalPayment).ToArray();
        var ids = matchedRecords.Select(r => r.Id).ToArray();
        var payments= result.Where(r => ids.Contains(r.Id)).ToArray();
        await _microNodeService.UpsertRecords(_key, payments);
        return payments;
        // return payments.Concat(internalPayments).ToArray();
    }

    public async Task<LightningPayment[]> ListPayments(ListPaymentsParams request, CancellationToken cancellation = new CancellationToken())
    {
        var result = await _innerClient.ListPayments(request, cancellation);
        var matchedRecords = await _microNodeService.MatchRecords(_key, result?.Select(r => r.Id).ToArray());
        // var internalPayments =  matchedRecords.Where(r => r.Type == "InternalPayment").Select(FromInternalPayment).ToArray();
        var ids = matchedRecords.Select(r => r.Id).ToArray();
        var payments= result.Where(r => ids.Contains(r.Id)).ToArray();
        await _microNodeService.UpsertRecords(_key, payments);
        return payments;
        // return payments.Concat(internalPayments).ToArray();
    }
    
    // public LightningPayment FromInternalPayment(MicroTransaction transaction)
    // {
    //     if(transaction.Type != "InternalPayment")
    //         throw new InvalidOperationException();
    //     return new LightningPayment()
    //     {
    //         //balance is in - so convert it to positive
    //         Amount = new LightMoney(Math.Abs(transaction.Amount)),
    //         Fee = LightMoney.Zero,
    //         Id = transaction.Id,
    //         Status = !transaction.Active && transaction.Accounted ? LightningPaymentStatus.Complete :
    //             transaction.Active ? LightningPaymentStatus.Pending : LightningPaymentStatus.Failed,
    //         AmountSent = new LightMoney(Math.Abs(transaction.Amount)),
    //         PaymentHash = transaction.Id
    //     };
    // }

    public async Task<LightningInvoice> CreateInvoice(LightMoney amount, string description, TimeSpan expiry,
        CancellationToken cancellation = new CancellationToken())
    {
        var invoice = await _innerClient.CreateInvoice(amount, description, expiry, cancellation);
        await _microNodeService.UpsertRecord(_key, invoice);
        return invoice;
    }

    public async Task<LightningInvoice> CreateInvoice(CreateInvoiceParams createInvoiceRequest, CancellationToken cancellation = new CancellationToken())
    {
        var invoice = await _innerClient.CreateInvoice(createInvoiceRequest, cancellation);
        await _microNodeService.UpsertRecord(_key, invoice);
        return invoice;
    }

    
    public class MicroListener: ILightningInvoiceListener
    {
        private readonly ILightningInvoiceListener _inner;
        private readonly MicroNodeService _microNodeService;
        private readonly string _key;

        public MicroListener(ILightningInvoiceListener inner, MicroNodeService microNodeService, string key)
        {
            _inner = inner;
            _microNodeService = microNodeService;
            _key = key;
        }

        public async Task<LightningInvoice> WaitInvoice(CancellationToken cancellation)
        {
            while (true)
            {
                var invoice = await _inner.WaitInvoice(cancellation);
                var record = await _microNodeService.MatchRecord(_key, invoice.Id);
                if(record is null)
                {
                    continue;
                } 
                await _microNodeService.UpsertRecord(_key, invoice);
                return invoice;
            }
        }

        public void Dispose()
        {
            _inner.Dispose();
        }
    }


    public async Task<ILightningInvoiceListener> Listen(CancellationToken cancellation = new CancellationToken())
    {
        return new MicroListener(await _innerClient.Listen(cancellation), _microNodeService, _key);
    }

    public async Task<LightningNodeInformation> GetInfo(CancellationToken cancellation = new CancellationToken())
    {
        var info = await _innerClient.GetInfo(cancellation);
        return info;
    }

    public async Task<LightningNodeBalance> GetBalance(CancellationToken cancellation = new CancellationToken())
    {
        return new LightningNodeBalance(null, new OffchainBalance()
        {
            Local = await _microNodeService.GetBalance(_key, cancellation)
        });

    }

    public async Task<PayResponse> Pay(PayInvoiceParams payParams, CancellationToken cancellation = new CancellationToken())
    {
        return await Pay(null, payParams, cancellation);
    }

    public async Task<PayResponse> Pay(string bolt11, PayInvoiceParams payParams, CancellationToken cancellation = new CancellationToken())
    {
        var invoice = BOLT11PaymentRequest.Parse(bolt11, _network);
        var id = payParams.PaymentHash?.ToString() ?? invoice.PaymentHash.ToString();
        var amount = payParams?.Amount?? invoice.MinimumAmount;
        var record = await _microNodeService.InitiatePayment(_key, id,amount,
            cancellation);
        var result = await _innerClient.Pay(bolt11,payParams, cancellation);
        await _microNodeService.UpsertRecord(_key, FromPayResponse(result, record));
        return result;
    }

    public async Task<PayResponse> Pay(string bolt11, CancellationToken cancellation = new CancellationToken())
    {
        return await Pay(bolt11, null, cancellation);
    }

    private LightningPayment FromPayResponse(PayResponse payResponse, MicroTransaction transaction)
    {
        return new LightningPayment()
        {
            Id = transaction.Id,
            PaymentHash = transaction.Id,
            Preimage = payResponse.Details?.Preimage?.ToString(),
            Fee = payResponse.Details?.FeeAmount,
            Amount = LightMoney.MilliSatoshis(Math.Abs(transaction.Amount)),
            CreatedAt = null,
            Status = payResponse.Result switch
            {
                PayResult.Ok => LightningPaymentStatus.Complete,
                PayResult.Unknown => LightningPaymentStatus.Unknown,
                PayResult.CouldNotFindRoute => LightningPaymentStatus.Failed,
                PayResult.Error => LightningPaymentStatus.Failed,
                _ => LightningPaymentStatus.Unknown
            },
            AmountSent = transaction.Amount
        };
    }

    public Task<OpenChannelResponse> OpenChannel(OpenChannelRequest openChannelRequest, CancellationToken cancellation = new CancellationToken())
    {
        throw new NotSupportedException();
    }

    public Task<BitcoinAddress> GetDepositAddress(CancellationToken cancellation = new CancellationToken())
    {
        throw new NotSupportedException();
    }

    public Task<ConnectionResult> ConnectTo(NodeInfo nodeInfo, CancellationToken cancellation = new CancellationToken())
    {
        throw new NotSupportedException();
    }

    public async Task CancelInvoice(string invoiceId, CancellationToken cancellation = new CancellationToken())
    {
        var result = await _microNodeService.MatchRecord(_key, invoiceId);
        if(result is null)
        {
            return;
        }
        await _innerClient.CancelInvoice(invoiceId, cancellation);
    }

    public Task<LightningChannel[]> ListChannels(CancellationToken cancellation = new CancellationToken())
    {
        throw new NotSupportedException();
    }
}