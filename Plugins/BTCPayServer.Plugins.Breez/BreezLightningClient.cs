using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Breez.Sdk;
using BTCPayServer.Lightning;
using NBitcoin;
using Network = Breez.Sdk.Network;

namespace BTCPayServer.Plugins.Breez;

public class BreezLightningClient : ILightningClient, IDisposable, EventListener
{
    public override string ToString()
    {
        return $"type=breez;key={PaymentKey}";
    }

    private readonly NBitcoin.Network _network;
    public readonly string PaymentKey;

    public ConcurrentQueue<(DateTimeOffset timestamp, string log)> Events { get; set; } = new();

    public BreezLightningClient(string inviteCode, string apiKey, string workingDir, NBitcoin.Network network,
        Mnemonic mnemonic, string paymentKey)
    {
        apiKey??= "99010c6f84541bf582899db6728f6098ba98ca95ea569f4c63f2c2c9205ace57";
        _network = network;
        PaymentKey = paymentKey;
        GreenlightCredentials glCreds = null;
        if (File.Exists(Path.Combine(workingDir, "client.crt")) && File.Exists(Path.Combine(workingDir, "client-key.pem")))
        {
            var deviceCert = File.ReadAllBytes(Path.Combine(workingDir, "client.crt"));
            var deviceKey = File.ReadAllBytes(Path.Combine(workingDir, "client-key.pem"));
            
            glCreds = new GreenlightCredentials(deviceKey.ToList(), deviceCert.ToList());
        }
        var nodeConfig = new NodeConfig.Greenlight(
            new GreenlightNodeConfig(glCreds, inviteCode)
        );
        var config = BreezSdkMethods.DefaultConfig(
                network == NBitcoin.Network.Main ? EnvironmentType.PRODUCTION : EnvironmentType.STAGING,
                apiKey,
                nodeConfig
            ) with
            {
                workingDir = workingDir,
                network = network == NBitcoin.Network.Main ? Network.BITCOIN :
                network == NBitcoin.Network.TestNet ? Network.TESTNET :
                network == NBitcoin.Network.RegTest ? Network.REGTEST : Network.SIGNET
            };
        var seed = mnemonic.DeriveSeed();
        Sdk = BreezSdkMethods.Connect(new ConnectRequest(config, seed.ToList()), this);
    }

    public BlockingBreezServices Sdk { get; }

    public event EventHandler<BreezEvent> EventReceived;

    public void OnEvent(BreezEvent e)
    {
        var msg = e switch
        {
            BreezEvent.BackupFailed backupFailed => $"{e.GetType().Name}: {backupFailed.details.error}",
            BreezEvent.InvoicePaid invoicePaid => $"{e.GetType().Name}: {invoicePaid.details.paymentHash}",
            BreezEvent.PaymentFailed paymentFailed => $"{e.GetType().Name}: {paymentFailed.details.error} {paymentFailed.details.invoice?.paymentHash}",
            BreezEvent.PaymentSucceed paymentSucceed => $"{e.GetType().Name}: {paymentSucceed.details.id}",
            BreezEvent.SwapUpdated swapUpdated => $"{e.GetType().Name}: {swapUpdated.details.status} {ConvertHelper.ToHexString(swapUpdated.details.paymentHash.ToArray())} {swapUpdated.details.bitcoinAddress}",
            _ => e.GetType().Name
        };

        Events.Enqueue((DateTimeOffset.Now, msg));
        if(Events.Count > 100)
            Events.TryDequeue(out _);
        EventReceived?.Invoke(this, e);
    }

    public Task<LightningInvoice> GetInvoice(string invoiceId, CancellationToken cancellation = default)
    {
        return GetInvoice(uint256.Parse(invoiceId), cancellation);
    }

    private LightningPayment ToLightningPayment(Payment payment)
    {
        if (payment?.details is not PaymentDetails.Ln lnPaymentDetails)
        {
            return null;
        }

        return new LightningPayment()
        {
            Amount = LightMoney.MilliSatoshis(payment.amountMsat),
            Id = lnPaymentDetails.data.paymentHash,
            Preimage = lnPaymentDetails.data.paymentPreimage,
            PaymentHash = lnPaymentDetails.data.paymentHash,
            BOLT11 = lnPaymentDetails.data.bolt11,
            Status = payment.status switch
            {
                PaymentStatus.FAILED => LightningPaymentStatus.Failed,
                PaymentStatus.COMPLETE => LightningPaymentStatus.Complete,
                PaymentStatus.PENDING => LightningPaymentStatus.Pending,
                _ => throw new ArgumentOutOfRangeException()
            },
            CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(payment.paymentTime),
            Fee = LightMoney.MilliSatoshis(payment.feeMsat),
            AmountSent = LightMoney.MilliSatoshis(payment.amountMsat)
        };
    }

    private LightningInvoice FromPayment(Payment p)
    {
       
        if (p?.details is not PaymentDetails.Ln lnPaymentDetails)
        {
            return null;
        }

        var bolt11 = BOLT11PaymentRequest.Parse(lnPaymentDetails.data.bolt11, _network);

        return new LightningInvoice()
        {
            Amount = LightMoney.MilliSatoshis(p.amountMsat + p.feeMsat),
            Id = lnPaymentDetails.data.paymentHash,
            Preimage = lnPaymentDetails.data.paymentPreimage,
            PaymentHash = lnPaymentDetails.data.paymentHash,
            BOLT11 = lnPaymentDetails.data.bolt11,
            Status = p.status switch
            {
                PaymentStatus.PENDING => LightningInvoiceStatus.Unpaid,
                PaymentStatus.FAILED => LightningInvoiceStatus.Expired,
                PaymentStatus.COMPLETE => LightningInvoiceStatus.Paid,
                _ => LightningInvoiceStatus.Unpaid
            },
            PaidAt = DateTimeOffset.FromUnixTimeSeconds(p.paymentTime),
            ExpiresAt = bolt11.ExpiryDate
        };
    }

    public async Task<LightningInvoice> GetInvoice(uint256 paymentHash, CancellationToken cancellation = default)
    {
        var p = Sdk.PaymentByHash(paymentHash.ToString()!);

        if(p is null)
            return new LightningInvoice()
            {
                Id = paymentHash.ToString(),
                PaymentHash = paymentHash.ToString(),
                Status = LightningInvoiceStatus.Unpaid
            };
    
        return FromPayment(p);
    }

    public async Task<LightningInvoice[]> ListInvoices(CancellationToken cancellation = default)
    {
        return await ListInvoices(null, cancellation);
    }

    public async Task<LightningInvoice[]> ListInvoices(ListInvoicesParams request,
        CancellationToken cancellation = default)
    {
        return Sdk.ListPayments(new ListPaymentsRequest(new List<PaymentTypeFilter>(){PaymentTypeFilter.RECEIVED}, null, null,
                null, request?.PendingOnly is not true, (uint?) request?.OffsetIndex, null))
            .Select(FromPayment).ToArray();
    }

    public async Task<LightningPayment> GetPayment(string paymentHash, CancellationToken cancellation = default)
    {
        return ToLightningPayment(Sdk.PaymentByHash(paymentHash));
    }

    public async Task<LightningPayment[]> ListPayments(CancellationToken cancellation = default)
    {
        return await ListPayments(null, cancellation);
    }

    public async Task<LightningPayment[]> ListPayments(ListPaymentsParams request,
        CancellationToken cancellation = default)
    {
        return Sdk.ListPayments(new ListPaymentsRequest(new List<PaymentTypeFilter>(){PaymentTypeFilter.RECEIVED}, null, null, null,
          null,      (uint?) request?.OffsetIndex, null))
            .Select(ToLightningPayment).ToArray();
    }


    public async Task<LightningInvoice> CreateInvoice(LightMoney amount, string description, TimeSpan expiry,
        CancellationToken cancellation = default)
    {
        var expiryS = expiry == TimeSpan.Zero ? (uint?) null : Math.Max(0, (uint) expiry.TotalSeconds);
        description??= "Invoice";
        var p = Sdk.ReceivePayment(new ReceivePaymentRequest((ulong) amount.MilliSatoshi, description, null, null,
            false, expiryS));
        return FromPR(p);
    }

    public LightningInvoice FromPR(ReceivePaymentResponse response)
    {
        return new LightningInvoice()
        {
            Amount = LightMoney.MilliSatoshis(response.lnInvoice.amountMsat ?? 0),
            Id = response.lnInvoice.paymentHash,
            Preimage = ConvertHelper.ToHexString(response.lnInvoice.paymentSecret.ToArray()),
            PaymentHash = response.lnInvoice.paymentHash,
            BOLT11 = response.lnInvoice.bolt11,
            Status = LightningInvoiceStatus.Unpaid,
            ExpiresAt = DateTimeOffset.FromUnixTimeSeconds((long) response.lnInvoice.expiry)
        };
    }

    public async Task<LightningInvoice> CreateInvoice(CreateInvoiceParams createInvoiceRequest,
        CancellationToken cancellation = default)
    {
        var expiryS = createInvoiceRequest.Expiry == TimeSpan.Zero
            ? (uint?) null
            : Math.Max(0, (uint) createInvoiceRequest.Expiry.TotalSeconds);
        var p = Sdk.ReceivePayment(new ReceivePaymentRequest((ulong) createInvoiceRequest.Amount.MilliSatoshi,
            (createInvoiceRequest.Description ?? createInvoiceRequest.DescriptionHash.ToString())!, null, null,
            createInvoiceRequest.DescriptionHashOnly, expiryS));
        return FromPR(p);
    }

    public async Task<ILightningInvoiceListener> Listen(CancellationToken cancellation = default)
    {
        return new BreezInvoiceListener(this, cancellation);
    }

    public async Task<LightningNodeInformation> GetInfo(CancellationToken cancellation = default)
    {
        var ni = Sdk.NodeInfo();
        return new LightningNodeInformation()
        {
            PeersCount = ni.connectedPeers.Count,
            Alias = $"greenlight {ni.id}",
            BlockHeight = (int) ni.blockHeight
        };
    }

    public async Task<LightningNodeBalance> GetBalance(CancellationToken cancellation = default)
    {
        var ni = Sdk.NodeInfo();
        return new LightningNodeBalance()
        {
            OnchainBalance =
                new OnchainBalance()
                {
                    Confirmed = Money.Coins(LightMoney.MilliSatoshis(ni.onchainBalanceMsat)
                        .ToUnit(LightMoneyUnit.BTC))
                },
            OffchainBalance = new OffchainBalance()
            {
                Local = LightMoney.MilliSatoshis(ni.channelsBalanceMsat),
                Remote = LightMoney.MilliSatoshis(ni.totalInboundLiquidityMsats),
            }
        };
    }

    public async Task<PayResponse> Pay(PayInvoiceParams payParams, CancellationToken cancellation = default)
    {
        return await Pay(null, payParams, cancellation);
    }

    public async Task<PayResponse> Pay(string bolt11, PayInvoiceParams payParams,
        CancellationToken cancellation = default)
    {
        SendPaymentResponse result;
        try
        {
            if (bolt11 is null)
            {
                result = Sdk.SendSpontaneousPayment(new SendSpontaneousPaymentRequest(payParams.Destination.ToString(),
                    (ulong) payParams.Amount.MilliSatoshi));
            }
            else
            {
                result = Sdk.SendPayment(new SendPaymentRequest(bolt11,false, (ulong?) payParams.Amount?.MilliSatoshi));
            }

            var details = result.payment.details as PaymentDetails.Ln;
            return new PayResponse()
            {
                Result = result.payment.status switch
                {
                    PaymentStatus.FAILED => PayResult.Error,
                    PaymentStatus.COMPLETE => PayResult.Ok,
                    PaymentStatus.PENDING => PayResult.Unknown,
                    _ => throw new ArgumentOutOfRangeException()
                },
                Details = new PayDetails()
                {
                    Status = result.payment.status switch
                    {
                        PaymentStatus.FAILED => LightningPaymentStatus.Failed,
                        PaymentStatus.COMPLETE => LightningPaymentStatus.Complete,
                        PaymentStatus.PENDING => LightningPaymentStatus.Pending,
                        _ => LightningPaymentStatus.Unknown
                    },
                    Preimage =
                        details.data.paymentPreimage is null ? null : uint256.Parse(details.data.paymentPreimage),
                    PaymentHash = details.data.paymentHash is null ? null : uint256.Parse(details.data.paymentHash),
                    FeeAmount = result.payment.feeMsat,
                    TotalAmount = LightMoney.MilliSatoshis(result.payment.amountMsat + result.payment.feeMsat),
                }
            };
        }
        catch (Exception e)
        {
            return new PayResponse(PayResult.Error, e.Message);
        }
    }

    public async Task<PayResponse> Pay(string bolt11, CancellationToken cancellation = default)
    {
        return await Pay(bolt11, null, cancellation);
    }

    public async Task<OpenChannelResponse> OpenChannel(OpenChannelRequest openChannelRequest,
        CancellationToken cancellation = default)
    {
        throw new NotImplementedException();
    }

    public async Task<BitcoinAddress> GetDepositAddress(CancellationToken cancellation = default)
    {
        throw new NotImplementedException();
    }

    public async Task<ConnectionResult> ConnectTo(NodeInfo nodeInfo, CancellationToken cancellation = default)
    {
        throw new NotImplementedException();
    }

    public async Task CancelInvoice(string invoiceId, CancellationToken cancellation = default)
    {
        throw new NotImplementedException();
    }

    public async Task<LightningChannel[]> ListChannels(CancellationToken cancellation = default)
    {
        throw new NotImplementedException();
    }

    public void Dispose()
    {
        Sdk.Dispose(); 
        Sdk.Dispose();
    }

    public class BreezInvoiceListener : ILightningInvoiceListener
    {
        private readonly BreezLightningClient _breezLightningClient;
        private readonly CancellationToken _cancellationToken;

        public BreezInvoiceListener(BreezLightningClient breezLightningClient, CancellationToken cancellationToken)
        {
            _breezLightningClient = breezLightningClient;
            _cancellationToken = cancellationToken;

            breezLightningClient.EventReceived += BreezLightningClientOnEventReceived;
        }

        private readonly ConcurrentQueue<Payment> _invoices = new();

        private void BreezLightningClientOnEventReceived(object sender, BreezEvent e)
        {
            if (e is BreezEvent.InvoicePaid pre && pre.details.payment is {})
            {
                
                _invoices.Enqueue(pre.details.payment);
            }
        }

        public void Dispose()
        {
            _breezLightningClient.EventReceived -= BreezLightningClientOnEventReceived;
        }

        public async Task<LightningInvoice> WaitInvoice(CancellationToken cancellation)
        {
            while (cancellation.IsCancellationRequested is not true)
            {
                if (_invoices.TryDequeue(out var payment))
                {
                    return _breezLightningClient.FromPayment(payment);
                }

                await Task.Delay(100, cancellation);
            }

            cancellation.ThrowIfCancellationRequested();
            return null;
        }
    }
}