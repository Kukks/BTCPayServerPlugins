﻿using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Breez.Sdk;
using BTCPayServer.Lightning;
using NBitcoin;
using Network = Breez.Sdk.Network;

namespace BTCPayServer.Plugins.Breez;


public class BreezLightningClient: ILightningClient, IDisposable, EventListener
{
    private readonly NBitcoin.Network _network;

    public BreezLightningClient(string inviteCode, string apiKey, string workingDir, NBitcoin.Network network,
        string mnemonic)
    {
        _network = network;
        var nodeConfig = new NodeConfig.Greenlight(
            new GreenlightNodeConfig(null, inviteCode)
        );
        var config = BreezSdkMethods.DefaultConfig(
            network ==NBitcoin.Network.Main ? EnvironmentType.PRODUCTION: EnvironmentType.STAGING, 
            apiKey, 
            nodeConfig
        ) with {
            workingDir= workingDir,
            network = network == NBitcoin.Network.Main ? Network.BITCOIN : network == NBitcoin.Network.TestNet ? Network.TESTNET: network == NBitcoin.Network.RegTest? Network.REGTEST: Network.SIGNET
        };
        var seed = BreezSdkMethods.MnemonicToSeed(mnemonic);
        Sdk = BreezSdkMethods.Connect(config, seed, this);  
        
    }

     public BlockingBreezServices Sdk { get; }

     public event EventHandler<BreezEvent> EventReceived;
     public void OnEvent(BreezEvent e)
     {
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
            CreatedAt =  DateTimeOffset.FromUnixTimeMilliseconds(payment.paymentTime),
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
            Amount = LightMoney.MilliSatoshis(p.amountMsat),
            Id = lnPaymentDetails.data.paymentHash,
            Preimage = lnPaymentDetails.data.paymentPreimage,
            PaymentHash = lnPaymentDetails.data.paymentHash,
            BOLT11 = lnPaymentDetails.data.bolt11,
            Status = p.status switch
            {
                PaymentStatus.FAILED => LightningInvoiceStatus.Expired,
                PaymentStatus.COMPLETE => LightningInvoiceStatus.Paid,
                _ => LightningInvoiceStatus.Unpaid
            },
            PaidAt = DateTimeOffset.FromUnixTimeMilliseconds(p.paymentTime),
            ExpiresAt = bolt11.ExpiryDate
        };
    }

    public async Task<LightningInvoice> GetInvoice(uint256 paymentHash, CancellationToken cancellation = default)
    {
        var p = Sdk.PaymentByHash(paymentHash.ToString()!);
        return FromPayment(p);
    }

    public async Task<LightningInvoice[]> ListInvoices(CancellationToken cancellation = default)
    {

        return await ListInvoices(null, cancellation);
    }

    public async Task<LightningInvoice[]> ListInvoices(ListInvoicesParams request, CancellationToken cancellation = default)
    {
        return Sdk.ListPayments(new ListPaymentsRequest(PaymentTypeFilter.RECEIVED, null, null, request?.PendingOnly is not true, (uint?) request?.OffsetIndex, null))
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

    public async Task<LightningPayment[]> ListPayments(ListPaymentsParams request, CancellationToken cancellation = default)
    {
        return Sdk.ListPayments(new ListPaymentsRequest(PaymentTypeFilter.RECEIVED, null, null, null, (uint?) request?.OffsetIndex, null))
            .Select(ToLightningPayment).ToArray();
    }


    public async Task<LightningInvoice> CreateInvoice(LightMoney amount, string description, TimeSpan expiry, CancellationToken cancellation = default)
    {

        var expiryS =expiry == TimeSpan.Zero? (uint?) null: Math.Max(0, (uint)expiry.TotalSeconds);
       var p =  Sdk.ReceivePayment(new ReceivePaymentRequest((ulong)amount.MilliSatoshi, description, null, null, false,expiryS ));
       return await GetInvoice(p.lnInvoice.paymentHash, cancellation);
    }

    public async Task<LightningInvoice> CreateInvoice(CreateInvoiceParams createInvoiceRequest, CancellationToken cancellation = default)
    {
        var expiryS =createInvoiceRequest.Expiry == TimeSpan.Zero? (uint?) null: Math.Max(0, (uint)createInvoiceRequest.Expiry.TotalSeconds);
        var p =  Sdk.ReceivePayment(new ReceivePaymentRequest((ulong)createInvoiceRequest.Amount.MilliSatoshi, (createInvoiceRequest.Description??createInvoiceRequest.DescriptionHash.ToString())!, null, null, createInvoiceRequest.DescriptionHashOnly,expiryS ));
        return await GetInvoice(p.lnInvoice.paymentHash, cancellation);
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
            NodeInfoList = {new NodeInfo(new PubKey(ni.id), "blockstrean.com", 69)},//we have to fake this as btcpay currently requires this to enable the payment method
            BlockHeight = (int)ni.blockHeight
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
                Remote = LightMoney.MilliSatoshis(ni.inboundLiquidityMsats),
            }
        };
    }

    public async Task<PayResponse> Pay(PayInvoiceParams payParams, CancellationToken cancellation = default)
    {
        return await Pay(null, payParams, cancellation);
    }

    public async Task<PayResponse> Pay(string bolt11, PayInvoiceParams payParams, CancellationToken cancellation = default)
    {
        throw new NotImplementedException();
    }

    public async Task<PayResponse> Pay(string bolt11, CancellationToken cancellation = default)
    {
        return await Pay(bolt11, null, cancellation);
    }

    public async Task<OpenChannelResponse> OpenChannel(OpenChannelRequest openChannelRequest, CancellationToken cancellation = default)
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
    
    public class BreezInvoiceListener: ILightningInvoiceListener
    {
        private readonly BreezLightningClient _breezLightningClient;
        private readonly CancellationToken _cancellationToken;

        public BreezInvoiceListener(BreezLightningClient breezLightningClient, CancellationToken cancellationToken)
        {
            _breezLightningClient = breezLightningClient;
            _cancellationToken = cancellationToken;
            
            breezLightningClient.EventReceived += BreezLightningClientOnEventReceived;
        }

        private readonly ConcurrentQueue<Task<LightningInvoice>> _invoices = new();

        private void BreezLightningClientOnEventReceived(object sender, BreezEvent e)
        {
            if (e is BreezEvent.InvoicePaid pre)
            {
                _invoices.Enqueue(_breezLightningClient.GetInvoice(pre.details.paymentHash, _cancellationToken));
            }
        }

        public void Dispose()
        {
            _breezLightningClient.EventReceived -= BreezLightningClientOnEventReceived;
        }

        public async Task<LightningInvoice> WaitInvoice(CancellationToken cancellation)
        {
            while(cancellation.IsCancellationRequested is not true)
            {
                if (_invoices.TryDequeue(out var task))
                {
                    return await task.WithCancellation(cancellation);
                }
                await Task.Delay(100, cancellation);
            }
            cancellation.ThrowIfCancellationRequested();
            return null;
        }
    }
}

