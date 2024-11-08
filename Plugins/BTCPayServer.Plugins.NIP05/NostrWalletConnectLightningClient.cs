#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using BTCPayServer.Lightning;
using NBitcoin;
using NBitcoin.Secp256k1;
using NNostr.Client;
using NNostr.Client.Protocols;
using SHA256 = System.Security.Cryptography.SHA256;

namespace BTCPayServer.Plugins.NIP05;

public class NostrWalletConnectLightningClient : ILightningClient
{

    [Display] public string DisplayLabel => $"Nostr Wallet Connect {_connectParams.lud16} {_connectParams.relays.First()} ";
    private readonly NostrClientPool _nostrClientPool;
    private readonly Uri _uri;
    private readonly Network _network;
    private readonly (string[] Commands, string[] Notifications) _commands;
    private readonly (ECXOnlyPubKey pubkey, ECPrivKey secret, Uri[] relays, string lud16) _connectParams;

    public NostrWalletConnectLightningClient(NostrClientPool nostrClientPool, Uri uri, Network network,
        (string[] Commands, string[] Notifications) commands)
    {
        _nostrClientPool = nostrClientPool;
        _uri = uri;
        _network = network;
        _commands = commands;
        _connectParams = NIP47.ParseUri(uri);
    }

    public override string ToString()
    {
        return $"type=nwc;key={_uri}";
    }


    public async Task<LightningInvoice> GetInvoice(string invoiceId,
        CancellationToken cancellation = new())
    {
        return await GetInvoice(uint256.Parse(invoiceId), cancellation);
    }

    private static LightningInvoice? ToLightningInvoice(NIP47.Nip47Transaction tx, Network network)
    {
        if (tx.Type != "incoming")
        {
            return null;
        }

        var isPaid = tx.SettledAt.HasValue;
        var invoice = BOLT11PaymentRequest.Parse(tx.Invoice, network);
        var expiresAt = tx.ExpiresAt is not null
            ? DateTimeOffset.FromUnixTimeSeconds(tx.ExpiresAt.Value)
            : invoice.ExpiryDate;
        var expired = !isPaid && expiresAt < DateTimeOffset.UtcNow;
        var s = tx.SettledAt is not null
            ? DateTimeOffset.FromUnixTimeSeconds(tx.SettledAt.Value)
            : (DateTimeOffset?)null;
        return new LightningInvoice()
        {
            PaymentHash = tx.PaymentHash,
            Amount = LightMoney.MilliSatoshis(tx.AmountMsats),
            Preimage = tx.Preimage,
            Id = tx.PaymentHash,
            Status = isPaid ? LightningInvoiceStatus.Paid :
                expired ? LightningInvoiceStatus.Expired : LightningInvoiceStatus.Unpaid,
            PaidAt = s,
            ExpiresAt = expiresAt,
            AmountReceived = isPaid ? LightMoney.MilliSatoshis(tx.AmountMsats) : LightMoney.Zero,
            BOLT11 = tx.Invoice
        };
    }


    public async Task<LightningInvoice> GetInvoice(uint256 paymentHash,
        CancellationToken cancellation = new())
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        var (nostrClient, usage) = await _nostrClientPool.GetClientAndConnect(_connectParams.relays, cts.Token);

        using (usage)
        {
            var tx = await nostrClient.SendNIP47Request<NIP47.Nip47Transaction>(_connectParams.pubkey,
                _connectParams.secret,
                new NIP47.LookupInvoiceRequest()
                {
                    PaymentHash = paymentHash.ToString()
                }, cts.Token);
            return ToLightningInvoice(tx, _network)!;
        }
    }

    public async Task<LightningInvoice[]> ListInvoices(CancellationToken cancellation = new())
    {
        return await ListInvoices(new ListInvoicesParams(), cancellation);
    }

    public async Task<LightningInvoice[]> ListInvoices(ListInvoicesParams request,
        CancellationToken cancellation = new CancellationToken())
    {

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        var (client, usage) = await _nostrClientPool.GetClientAndConnect(_connectParams.relays, cts.Token);
        using (usage)
        {
            var response = await client.SendNIP47Request<NIP47.ListTransactionsResponse>(_connectParams.pubkey,
                _connectParams.secret,
                new NIP47.ListTransactionsRequest()
                {
                    Type = "incoming",
                    Offset = (int)(request.OffsetIndex ?? 0),
                    Unpaid = request.PendingOnly ?? false,
                }, cts.Token);

            return response.Transactions.Select(transaction => ToLightningInvoice(transaction, _network))
                .Where(i => i is not null).ToArray()!;
        }
    }


    private LightningPayment? ToLightningPayment(NIP47.Nip47Transaction tx)
    {
        // if (tx.Type != "outgoing")
        // {
        //     return null;
        // }

        var isPaid = tx.SettledAt.HasValue || !string.IsNullOrEmpty(tx.Preimage);
        var invoice = BOLT11PaymentRequest.Parse(tx.Invoice, _network);
        var expiresAt = tx.ExpiresAt is not null
            ? DateTimeOffset.FromUnixTimeSeconds(tx.ExpiresAt.Value)
            : invoice.ExpiryDate;
        var created = DateTimeOffset.FromUnixTimeSeconds(tx.CreatedAt);
        var expired = !isPaid && expiresAt < DateTimeOffset.UtcNow;
        var s = tx.SettledAt is not null
            ? DateTimeOffset.FromUnixTimeSeconds(tx.SettledAt.Value)
            : (DateTimeOffset?)null;
        return new LightningPayment()
        {
            PaymentHash = tx.PaymentHash,
            Amount = LightMoney.MilliSatoshis(tx.AmountMsats),
            Preimage = tx.Preimage,
            Id = tx.PaymentHash,
            Status = isPaid ? LightningPaymentStatus.Complete :
                expired ? LightningPaymentStatus.Failed : LightningPaymentStatus.Unknown,
            BOLT11 = tx.Invoice,
            Fee = LightMoney.MilliSatoshis(tx.FeesPaidMsats),
            AmountSent = LightMoney.MilliSatoshis(tx.AmountMsats + tx.FeesPaidMsats),
            CreatedAt = created
        };
    }


    public async Task<LightningPayment> GetPayment(string paymentHash,
        CancellationToken cancellation = new())
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        var (nostrClient, usage) = await _nostrClientPool.GetClientAndConnect(_connectParams.relays, cts.Token);
        using (usage)
        {
            NIP47.Nip47Transaction tx;
            try
            {
                tx = await nostrClient.SendNIP47Request<NIP47.Nip47Transaction>(_connectParams.pubkey, _connectParams.secret,
                    new NIP47.LookupInvoiceRequest()
                    {
                        PaymentHash = paymentHash
                    }, cts.Token);
            }
            // The standard says it returns NOT_FOUND error, but
            // Alby returns INTERNAL error... Probably safer to catch all
            catch (Exception)
            {
                return null;
            }
            return ToLightningPayment(tx)!;
        }
    }

    public async Task<LightningPayment[]> ListPayments(CancellationToken cancellation = new())
    {
        return await ListPayments(new ListPaymentsParams(), cancellation);
    }

    public async Task<LightningPayment[]> ListPayments(ListPaymentsParams request,
        CancellationToken cancellation = new())
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        var (nostrClient, usage) = await _nostrClientPool.GetClientAndConnect(_connectParams.relays, cts.Token);

        using (usage)
        {
            var response = await nostrClient.SendNIP47Request<NIP47.ListTransactionsResponse>(_connectParams.pubkey,
                _connectParams.secret,
                new NIP47.ListTransactionsRequest()
                {
                    Type = "outgoing",
                    Offset = (int)(request.OffsetIndex ?? 0),
                    Unpaid = request.IncludePending ?? false,
                }, cts.Token);
            return response.Transactions.Select(ToLightningPayment).Where(i => i is not null).ToArray()!;
        }
    }

    public async Task<LightningInvoice> CreateInvoice(LightMoney amount, string description, TimeSpan expiry,
        CancellationToken cancellation = new())
    {
        return await CreateInvoice(new CreateInvoiceParams(amount, description, expiry), cancellation);
    }

    public async Task<LightningInvoice> CreateInvoice(CreateInvoiceParams createInvoiceRequest,
        CancellationToken cancellation = new())
    {

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        var (nostrClient, usage) = await _nostrClientPool.GetClientAndConnect(_connectParams.relays, cts.Token);

        using (usage)
        {
            var response = await nostrClient.SendNIP47Request<NIP47.Nip47Transaction>(_connectParams.pubkey,
                _connectParams.secret,
                new NIP47.MakeInvoiceRequest()
                {
                    AmountMsats = createInvoiceRequest.Amount.MilliSatoshi,
                    Description = createInvoiceRequest.Description is null || createInvoiceRequest.DescriptionHashOnly
                        ? null
                        : createInvoiceRequest.Description,
                    DescriptionHash = createInvoiceRequest.DescriptionHash?.ToString(),
                    ExpirySeconds = (int)createInvoiceRequest.Expiry.TotalSeconds,
                }, cts.Token);
            return ToLightningInvoice(response, _network)!;
        }
    }

    public async Task<ILightningInvoiceListener> Listen(CancellationToken cancellation = new())
    {
        var x = await _nostrClientPool.GetClientAndConnect(_connectParams.relays, cancellation);
        if (_commands.Notifications?.Contains("payment_received") is true)
        {
            return new NotificationListener(_network, x, _connectParams);
        }

        return new PollListener(_network, x, _connectParams);
    }


    public class NotificationListener : ILightningInvoiceListener
    {
        private readonly Network _network;
        private readonly INostrClient _client;

        private readonly CancellationTokenSource _cts;
        private readonly IAsyncEnumerable<NIP47.Nip47Notification> _notifications;
        private readonly IDisposable _disposable;

        public NotificationListener(Network network, (INostrClient, IDisposable) client,
            (ECXOnlyPubKey pubkey, ECPrivKey secret, Uri[] relays, string lud16) x)
        {
            _network = network;
            _client = client.Item1;
            _disposable = client.Item2;
            _cts = new CancellationTokenSource();
            _notifications = _client.SubscribeNip47Notifications(x.pubkey, x.secret, _cts.Token);
        }

        public void Dispose()
        {
            _cts.Cancel();
            _disposable.Dispose();
        }

        public async Task<LightningInvoice> WaitInvoice(CancellationToken cancellation)
        {
            var enumerator = _notifications.GetAsyncEnumerator(cancellation);
            while (await enumerator.MoveNextAsync(cancellation))
            {
                if (enumerator.Current.NotificationType == "payment_received")
                {
                    var tx = enumerator.Current.Deserialize<NIP47.Nip47Transaction>();
                    return ToLightningInvoice(tx, _network)!;
                }
            }

            throw new Exception("No notification received");
        }
    }

    public class PollListener : ILightningInvoiceListener
    {
        private readonly Network _network;
        private readonly (ECXOnlyPubKey pubkey, ECPrivKey secret, Uri[] relays, string lud16) _connectparams;
        private readonly INostrClient _client;

        private readonly CancellationTokenSource _cts;
        private readonly IAsyncEnumerable<NIP47.Nip47Notification> _notifications;
        private readonly IDisposable _disposable;
        private NIP47.ListTransactionsResponse? _lastPaid;
        private Channel<LightningInvoice> queue = Channel.CreateUnbounded<LightningInvoice>();

        public PollListener(Network network, (INostrClient, IDisposable) client,
            (ECXOnlyPubKey pubkey, ECPrivKey secret, Uri[] relays, string lud16) connectparams)
        {
            _network = network;
            _connectparams = connectparams;
            _client = client.Item1;
            _disposable = client.Item2;
            _cts = new CancellationTokenSource();
            _ = Poll();
        }


        private async Task Poll()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    cts.CancelAfter(TimeSpan.FromSeconds(10));
                    var paid = await _client.SendNIP47Request<NIP47.ListTransactionsResponse>(_connectparams.pubkey,
                        _connectparams.secret, new NIP47.ListTransactionsRequest()
                        {
                            Type = "incoming",
                            // Unpaid = true, //seems like this is ignored... so we only get paid ones 
                            Limit = 300
                        }, cancellationToken: cts.Token);
                    paid.Transactions = paid.Transactions.Where(i => i is { Type: "incoming", SettledAt: not null }).ToArray();
                    if (_lastPaid is not null)
                    {

                        var paidInvoicesSinceLastPoll = paid.Transactions.Where(i =>
                            _lastPaid.Transactions.All(j => j.PaymentHash != i.PaymentHash)).Select(i =>
                            ToLightningInvoice(i, _network)!);


                        //all invoices which  are no longer in the unpaid list are paid
                        // var paidInvoicesSinceLastPoll = _lastPaid.Transactions
                        //     .Where(i => paid.Transactions.All(j => j.PaymentHash != i.PaymentHash))
                        //     .Select(i => ToLightningInvoice(i, _network)!);
                        foreach (var invoice in paidInvoicesSinceLastPoll)
                        {
                            await queue.Writer.WriteAsync(invoice, _cts.Token);
                        }
                    }

                    _lastPaid = paid;
                    await Task.Delay(1000, _cts.Token);
                }
            }
            catch (Exception e)
            {
                Dispose();
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _disposable.Dispose();
            queue.Writer.Complete();
        }

        public async Task<LightningInvoice> WaitInvoice(CancellationToken cancellation)
        {
            return await queue.Reader.ReadAsync(CancellationTokenSource
                .CreateLinkedTokenSource(_cts.Token, cancellation).Token);
        }
    }

    public async Task<LightningNodeInformation> GetInfo(CancellationToken cancellation = new())
    {
        throw new NotSupportedException();
    }

    public async Task<LightningNodeBalance> GetBalance(CancellationToken cancellation = new())
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        var (client, usage) = await _nostrClientPool.GetClientAndConnect(_connectParams.relays, cts.Token);

        using (usage)
        {
            var response = await client.SendNIP47Request<NIP47.GetBalanceResponse>(_connectParams.pubkey,
                _connectParams.secret,
                new NIP47.NIP47Request("get_balance"), cts.Token);
            return new LightningNodeBalance()
            {
                OffchainBalance = new OffchainBalance()
                {
                    Local = LightMoney.MilliSatoshis(response.BalanceMsats),
                }
            };
        }
    }

    public async Task<PayResponse> Pay(PayInvoiceParams payParams,
        CancellationToken cancellation = new())
    {
        return await Pay(null, new PayInvoiceParams(), cancellation);
    }

    public async Task<PayResponse> Pay(string bolt11, PayInvoiceParams payParams,
        CancellationToken cancellation = new())
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        var (client, usage) = await _nostrClientPool.GetClientAndConnect(_connectParams.relays, cts.Token);

        using (usage)
        {
            NIP47.INIP47Request request;
            if (bolt11 is null)
            {
                request = new NIP47.PayKeysendRequest()
                {
                    Amount = Convert.ToDecimal(payParams.Amount.MilliSatoshi),
                    Pubkey = payParams.Destination.ToHex(),
                    TlvRecords = payParams.CustomRecords?.Select(kv => new NIP47.TlvRecord()
                    {
                        Type = kv.Key.ToString(),
                        Value = kv.Value
                    }).ToArray()
                };
                
            }
            else
            {
                request = new NIP47.PayInvoiceRequest()
                {
                    Invoice = bolt11,
                    Amount = payParams.Amount?.MilliSatoshi is not null
                            ? Convert.ToDecimal(payParams.Amount.MilliSatoshi)
                            : null,
                };

            }
            var response = await client.SendNIP47Request<NIP47.PayInvoiceResponse>(_connectParams.pubkey, _connectParams.secret, request, cts.Token);
            var payHash = ConvertHelper.ToHexString(SHA256.HashData(Convert.FromHexString(response.Preimage)));

            try
            {
                var tx = await client.SendNIP47Request<NIP47.Nip47Transaction>(_connectParams.pubkey, _connectParams.secret,
                    new NIP47.LookupInvoiceRequest()
                    {
                        PaymentHash = payHash
                    }, cts.Token);
                var lp = ToLightningPayment(tx)!;
                return new PayResponse(lp.Status == LightningPaymentStatus.Complete ? PayResult.Ok : PayResult.Error,
                    new PayDetails()
                    {
                        Preimage = lp.Preimage is null ? null : new uint256(lp.Preimage),
                        Status = lp.Status,
                        TotalAmount = lp.AmountSent,
                        PaymentHash = new uint256(lp.PaymentHash),
                        FeeAmount = lp.Fee
                    });
            }
            catch (Exception e)
            {
                return new PayResponse(PayResult.Ok, new PayDetails()
                {
                    Status = LightningPaymentStatus.Complete,
                    PaymentHash = new uint256(payHash),
                    Preimage = new uint256(response.Preimage),
                })
                { ErrorDetail = e.Message };
            }
        }

    }

    public async Task<PayResponse> Pay(string bolt11, CancellationToken cancellation = new())
    {
        return await Pay(bolt11, new PayInvoiceParams(), cancellation);
    }

    public async Task<OpenChannelResponse> OpenChannel(OpenChannelRequest openChannelRequest,
        CancellationToken cancellation = new())
    {
        throw new NotSupportedException();
    }

    public async Task<BitcoinAddress> GetDepositAddress(CancellationToken cancellation = new())
    {
        throw new NotSupportedException();
    }

    public async Task<ConnectionResult> ConnectTo(NodeInfo nodeInfo,
        CancellationToken cancellation = new())
    {
        throw new NotSupportedException();
    }

    public async Task CancelInvoice(string invoiceId, CancellationToken cancellation = new())
    {
        throw new NotSupportedException();
    }

    public async Task<LightningChannel[]> ListChannels(CancellationToken cancellation = new())
    {
        throw new NotSupportedException();
    }
}