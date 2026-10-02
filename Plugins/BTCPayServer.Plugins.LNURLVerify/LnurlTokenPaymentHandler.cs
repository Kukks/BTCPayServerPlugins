#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Network = NBitcoin.Network;

namespace BTCPayServer.Plugins.LNURLVerify;

public class TokenQuoteState
{
    public string Destination { get; set; } = "";
    public string Amount { get; set; } = "";
    public long? ExpiresAt { get; set; }
    public string Verify { get; set; } = "";
    public string? VerifyBatch { get; set; }
    public long AmountMsat { get; set; }
}

public class TokenNetworkState
{
    public string OptionId { get; set; } = "";
    public string Asset { get; set; } = "";
    public bool Refused { get; set; }
    public TokenQuoteState? Quote { get; set; }
}

public class LnurlTokenPromptDetails
{
    public string Unit { get; set; } = "";
    public int Decimals { get; set; }
    public string? UnitName { get; set; }
    public List<TokenNetworkState> Networks { get; set; } = new();
    public List<SupersededRailDestination> Superseded { get; set; } = new();
}

/// <summary>
/// One configured asset as a BTC-priced payment method. Its prompt holds a quote per network the checkout asked for;
/// the prompt's single destination is the most recently requested one.
/// </summary>
public class LnurlTokenPaymentHandler : ILnurlRailHandler
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Network _network;
    private readonly TokenActivations _activations;

    public LnurlTokenPaymentHandler(string code, IHttpClientFactory httpClientFactory, Network network, TokenActivations activations)
    {
        Code = code;
        PaymentMethodId = TokenAssets.PaymentMethodIdOf(code);
        _httpClientFactory = httpClientFactory;
        _network = network;
        _activations = activations;
    }

    public string Code { get; }
    public PaymentMethodId PaymentMethodId { get; }
    public string Label => Code;
    public JsonSerializer Serializer { get; } = BlobSerializer.CreateSerializer().Serializer;

    public Task BeforeFetchingRates(PaymentMethodContext context)
    {
        context.Prompt.Currency = "BTC";
        context.Prompt.Divisibility = 8;
        context.Prompt.PaymentMethodFee = 0m;
        if (context.InvoiceEntity.GetPaymentPrompt(PaymentMethodId) is null)
        {
            context.Prompt.Inactive = true;
            if (context.Status != PaymentMethodContext.ContextStatus.Excluded)
                LnurlRailPaymentHandler.PreferCoreDefault(context);
        }
        return Task.CompletedTask;
    }

    public async Task ConfigurePrompt(PaymentMethodContext context)
    {
        var invoice = context.InvoiceEntity;
        if (invoice.Type == InvoiceType.TopUp)
            throw new PaymentMethodUnavailableException("top-up invoices have no amount to request");
        var lnurl = LnurlRailProvisioning.LnurlValue(context.Store)
                    ?? throw new PaymentMethodUnavailableException("the store's Lightning backend is not an LNURL");
        var due = context.Prompt.Calculate().Due;
        if (due <= 0m) throw new PaymentMethodUnavailableException("nothing is due");
        var msat = LnurlRailPaymentHandler.MsatOf(due);
        var requested = _activations.Take(invoice.Id, PaymentMethodId);

        var http = _httpClientFactory.CreateClient(nameof(LnurlTokenPaymentHandler));
        http.Timeout = TimeSpan.FromSeconds(15);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        JObject pay;
        try
        {
            var resolved = await LNURLVerifyConnectionStringHandler.ResolveCached(lnurl, _network, http, cts.Token);
            pay = await LNURLResolver.GetJson(http, resolved.PayEndpoint, cts.Token);
        }
        catch (Exception e) { throw new PaymentMethodUnavailableException($"the LNURL could not be read ({e.Message})"); }
        var offered = TokenOption.Parse(pay).Where(o => o.Unit.Code == Code && o.Verifiable != false).ToList();
        if (offered.Count == 0) throw new PaymentMethodUnavailableException($"the LNURL offers no network for {Code}");

        var previous = invoice.GetPaymentPrompt(PaymentMethodId) is { Activated: true, Details: { } json } ? ParsePaymentPromptDetails(json) : null;
        var details = new LnurlTokenPromptDetails
        {
            Unit = Code, Decimals = offered[0].Unit.Decimals, UnitName = offered[0].Unit.Name,
            Superseded = previous?.Superseded ?? new List<SupersededRailDestination>()
        };
        var now = DateTimeOffset.UtcNow;
        string? newest = null;
        foreach (var option in offered)
        {
            var old = previous?.Networks.FirstOrDefault(n => n.OptionId == option.Id);
            var network = new TokenNetworkState { OptionId = option.Id, Asset = option.Asset.ToString(), Refused = old?.Refused ?? false, Quote = old?.Quote };
            details.Networks.Add(network);
            var reissue = network.Quote is { } held && held.AmountMsat != msat;
            if (network.Refused || !(reissue || requested.Contains(option.Id))) continue;
            try
            {
                var quote = await Request(http, pay, option, msat, lnurl, invoice.ExpirationTime, now, cts.Token);
                if (network.Quote is { } replaced) Retire(details, replaced, network.Asset);
                network.Quote = new TokenQuoteState
                {
                    Destination = quote.Destination, Amount = quote.Amount, ExpiresAt = quote.ExpiresAt,
                    Verify = quote.Verify, VerifyBatch = quote.VerifyBatch, AmountMsat = quote.AmountMsat
                };
                newest = quote.Destination;
                context.TrackedDestinations.Add(quote.Destination);
            }
            catch (PaymentMethodUnavailableException e)
            {
                network.Refused = true;
                context.Logs.Write($"{option.Id}: {e.Message}", InvoiceEventData.EventSeverity.Warning);
            }
        }
        foreach (var gone in previous?.Networks ?? new List<TokenNetworkState>())
            if (gone.Quote is { } q && details.Networks.All(n => n.OptionId != gone.OptionId))
                Retire(details, q, gone.Asset);
        context.Prompt.Details = JObject.FromObject(details, Serializer);
        context.Prompt.Destination = (newest ?? invoice.GetPaymentPrompt(PaymentMethodId)?.Destination)!;
    }

    // A replaced or withdrawn destination stays tracked: the LNURL still settles what a payer already sent to it.
    private static void Retire(LnurlTokenPromptDetails details, TokenQuoteState quote, string asset) =>
        details.Superseded.Add(new SupersededRailDestination
        {
            Destination = quote.Destination, Verify = quote.Verify, VerifyBatch = quote.VerifyBatch, AmountMsat = quote.AmountMsat, Asset = asset
        });

    private async Task<TokenQuote> Request(HttpClient http, JObject pay, TokenOption option, long msat, string lnurl,
        DateTimeOffset invoiceExpiry, DateTimeOffset now, CancellationToken ct)
    {
        var key = PaymentMethodId + "/" + option.Id;
        if (UnverifiableRails.Knows(lnurl, key, now))
            throw new UnverifiableRailException("settlement cannot be detected: the LNURL refused this network as unverifiable within the last day");
        try { return await LnurlTokenRequester.Request(http, pay, option, msat, invoiceExpiry, now, ct); }
        catch (UnverifiableRailException)
        {
            UnverifiableRails.Remember(lnurl, key, now);
            throw;
        }
    }

    public Task AfterSavingInvoice(PaymentMethodContext context)
    {
        if (context.Prompt is { Activated: true, Details: not null })
            foreach (var d in Tracked(context.InvoiceEntity, context.Prompt))
                TrackedDestinationRegistry.Add(d);
        return Task.CompletedTask;
    }

    public IEnumerable<TrackedDestination> Tracked(InvoiceEntity invoice, PaymentPrompt prompt)
    {
        var details = ParsePaymentPromptDetails(prompt.Details);
        var pmi = PaymentMethodId.ToString();
        foreach (var n in details.Networks)
            if (n.Quote is { } q)
                yield return new TrackedDestination(invoice.Id, pmi, q.Destination, q.Verify, q.VerifyBatch, q.AmountMsat, invoice.MonitoringExpiration, n.Asset);
        foreach (var s in details.Superseded)
            yield return new TrackedDestination(invoice.Id, pmi, s.Destination, s.Verify, s.VerifyBatch, s.AmountMsat, invoice.MonitoringExpiration, s.Asset);
    }

    public bool NeedsReissue(PaymentPrompt prompt)
    {
        var due = prompt.Calculate().Due;
        return ParsePaymentPromptDetails(prompt.Details).Networks.Any(n => n.Quote is { } q && LnurlRailRecorder.NeedsReissue(due, q.AmountMsat));
    }

    public LnurlTokenPromptDetails ParsePaymentPromptDetails(JToken details) =>
        details.ToObject<LnurlTokenPromptDetails>(Serializer) ?? throw new FormatException($"Invalid {nameof(LnurlTokenPromptDetails)}");

    object IPaymentMethodHandler.ParsePaymentPromptDetails(JToken details) => ParsePaymentPromptDetails(details);

    public object ParsePaymentMethodConfig(JToken config) => config.ToObject<LnurlRailConfig>(Serializer) ?? new LnurlRailConfig();

    public LnurlRailPaymentData ParsePaymentDetails(JToken details) =>
        details.ToObject<LnurlRailPaymentData>(Serializer) ?? throw new FormatException($"Invalid {nameof(LnurlRailPaymentData)}");

    object IPaymentMethodHandler.ParsePaymentDetails(JToken details) => ParsePaymentDetails(details);
}
