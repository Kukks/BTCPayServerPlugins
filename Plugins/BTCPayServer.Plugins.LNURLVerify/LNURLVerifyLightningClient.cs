#nullable enable
using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Lightning;
using BTCPayServer.Payments.Lightning;
using Microsoft.Extensions.Logging;
using NBitcoin;
using Network = NBitcoin.Network;

namespace BTCPayServer.Plugins.LNURLVerify;

/// <summary>
/// The per-connection BTCPay Lightning backend. Receive methods always work; send/balance work only
/// when the connection was built from an LNURL-withdraw (Capability == SendAndReceive). Node/channel
/// operations are unsupported (this client holds no node).
/// </summary>
public sealed class LNURLVerifyLightningClient : IExtendedLightningClient
{
    private readonly ResolvedLnurl _resolved;
    private readonly HttpClient _http;
    private readonly LNURLReceiver _receiver;
    private readonly LNURLSendExecutor? _sender;

    private const string ReceiveOnlyMsg =
        "This LNURL connection is receive-only. Sending, balance and channel operations require an LNURL-withdraw connection.";

    public LNURLVerifyLightningClient(ResolvedLnurl resolved, Network network, HttpClient http, ILoggerFactory lf)
    {
        _resolved = resolved;
        _http = http;
        _receiver = new LNURLReceiver(resolved, network, http, lf.CreateLogger(nameof(LNURLReceiver)));
        _sender = resolved.Capability == LnurlCapability.SendAndReceive
            ? new LNURLSendExecutor(resolved, http, lf.CreateLogger(nameof(LNURLSendExecutor)))
            : null;
    }

    // ---- Receive ----
    public Task<LightningInvoice> CreateInvoice(LightMoney amount, string description, TimeSpan expiry, CancellationToken cancellation = default)
        => _receiver.CreateInvoice(amount, description, null, cancellation);

    public Task<LightningInvoice> CreateInvoice(CreateInvoiceParams createInvoiceRequest, CancellationToken cancellation = default)
        => _receiver.CreateInvoice(createInvoiceRequest.Amount, createInvoiceRequest.Description, createInvoiceRequest, cancellation);

    public Task<LightningInvoice?> GetInvoice(string invoiceId, CancellationToken cancellation = default)
        => _receiver.GetInvoice(invoiceId, cancellation);

    public Task<LightningInvoice?> GetInvoice(uint256 paymentHash, CancellationToken cancellation = default)
        => _receiver.GetInvoice(paymentHash.ToString(), cancellation);

    public Task<LightningInvoice[]> ListInvoices(CancellationToken cancellation = default)
        => ListInvoices(new ListInvoicesParams(), cancellation);

    public Task<LightningInvoice[]> ListInvoices(ListInvoicesParams request, CancellationToken cancellation = default)
    {
        var mine = TrackedInvoiceRegistry.All()
            .Where(t => t.PayEndpoint == _resolved.PayEndpoint.ToString())
            .Select(t => new LightningInvoice
            {
                Id = t.PaymentHash,
                PaymentHash = t.PaymentHash,
                BOLT11 = t.Bolt11,
                Status = LightningInvoiceStatus.Unpaid,
                ExpiresAt = t.ExpiresAt
            })
            .Where(i => request?.PendingOnly is not true || i.Status == LightningInvoiceStatus.Unpaid)
            .ToArray();
        return Task.FromResult(mine);
    }

    public Task<ILightningInvoiceListener> Listen(CancellationToken cancellation = default)
        => Task.FromResult<ILightningInvoiceListener>(
            new LNURLVerifyListener(t => t.PayEndpoint == _resolved.PayEndpoint.ToString()));

    public Task CancelInvoice(string invoiceId, CancellationToken cancellation = default)
    {
        TrackedInvoiceRegistry.Remove(invoiceId);
        return Task.CompletedTask;
    }

    // ---- Send ----
    public Task<PayResponse> Pay(string bolt11, PayInvoiceParams payParams, CancellationToken cancellation = default)
        => _sender is null ? throw new NotSupportedException(ReceiveOnlyMsg) : _sender.Pay(bolt11, payParams?.Amount, cancellation);

    public Task<PayResponse> Pay(string bolt11, CancellationToken cancellation = default)
        => _sender is null ? throw new NotSupportedException(ReceiveOnlyMsg) : _sender.Pay(bolt11, null, cancellation);

    public Task<PayResponse> Pay(PayInvoiceParams payParams, CancellationToken cancellation = default)
        => _sender is null
            ? throw new NotSupportedException(ReceiveOnlyMsg)
            : Task.FromResult(new PayResponse(PayResult.Error, "A bolt11 invoice is required to pay via LNURL-withdraw."));

    public async Task<LightningNodeBalance> GetBalance(CancellationToken cancellation = default)
    {
        if (_sender is null) throw new NotSupportedException(ReceiveOnlyMsg);
        var bal = await _sender.GetBalance(cancellation)
                  ?? throw new NotSupportedException("This LNURL-withdraw does not expose a balance.");
        return new LightningNodeBalance { OffchainBalance = new OffchainBalance { Local = bal } };
    }

    // ---- Payments (best-effort / empty) ----
    public Task<LightningPayment?> GetPayment(string paymentHash, CancellationToken cancellation = default)
        => Task.FromResult<LightningPayment?>(null);

    public Task<LightningPayment[]> ListPayments(CancellationToken cancellation = default)
        => Task.FromResult(Array.Empty<LightningPayment>());

    public Task<LightningPayment[]> ListPayments(ListPaymentsParams request, CancellationToken cancellation = default)
        => Task.FromResult(Array.Empty<LightningPayment>());

    // ---- Unsupported (nodeless) ----
    public Task<LightningNodeInformation> GetInfo(CancellationToken cancellation = default)
        => throw new NotSupportedException(ReceiveOnlyMsg);

    public Task<BitcoinAddress> GetDepositAddress(CancellationToken cancellation = default)
        => throw new NotSupportedException(ReceiveOnlyMsg);

    public Task<OpenChannelResponse> OpenChannel(OpenChannelRequest openChannelRequest, CancellationToken cancellation = default)
        => throw new NotSupportedException(ReceiveOnlyMsg);

    public Task<ConnectionResult> ConnectTo(NodeInfo nodeInfo, CancellationToken cancellation = default)
        => throw new NotSupportedException(ReceiveOnlyMsg);

    public Task<LightningChannel[]> ListChannels(CancellationToken cancellation = default)
        => throw new NotSupportedException(ReceiveOnlyMsg);

    // ---- IExtendedLightningClient ----
    public async Task<ValidationResult?> Validate()
    {
        try { await LNURLResolver.GetJson(_http, _resolved.PayEndpoint, CancellationToken.None); }
        catch (Exception e) { return new ValidationResult(e.Message); }
        return ValidationResult.Success;
    }

    public string? DisplayName => "LNURL";
    public Uri? ServerUri => new($"https://{_resolved.DisplayHost}");
}
