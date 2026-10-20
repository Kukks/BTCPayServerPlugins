#nullable enable
using System;
using System.Collections.Generic;
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

public class LnurlRailConfig { }

public class LnurlRailPromptDetails
{
    public string OptionId { get; set; } = "";
    public string? PaymentUri { get; set; }
    public string Verify { get; set; } = "";
    public string? VerifyBatch { get; set; }
    public long AmountMsat { get; set; }
    public List<SupersededRailDestination> Superseded { get; set; } = new();
}

/// <summary>A destination replaced after a partial payment; the LNURL service still accepts payment to it.</summary>
public class SupersededRailDestination
{
    public string Destination { get; set; } = "";
    public string Verify { get; set; } = "";
    public string? VerifyBatch { get; set; }
    public long AmountMsat { get; set; }
    public string? Asset { get; set; }
}

public class LnurlRailPaymentData
{
    public string PaymentReference { get; set; } = "";
    public string VerifyUrl { get; set; } = "";
    public string Destination { get; set; } = "";
    public string? Asset { get; set; }
}

/// <summary>
/// One LNURL paymentOptions rail as a BTCPay payment method. The prompt is inactive at invoice creation,
/// so the LNURL is asked for a destination only once a checkout wants it.
/// </summary>
public class LnurlRailPaymentHandler : ILnurlRailHandler
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Network _network;

    public LnurlRailPaymentHandler(LnurlRail rail, IHttpClientFactory httpClientFactory, Network network)
    {
        Rail = rail;
        _httpClientFactory = httpClientFactory;
        _network = network;
    }

    public LnurlRail Rail { get; }
    public PaymentMethodId PaymentMethodId => Rail.PaymentMethodId;

    public string Label => Rail.Label;

    public IEnumerable<TrackedDestination> Tracked(InvoiceEntity invoice, PaymentPrompt prompt) =>
        Destinations(invoice, PaymentMethodId, prompt.Destination, ParsePaymentPromptDetails(prompt.Details));

    public bool NeedsReissue(PaymentPrompt prompt) =>
        LnurlRailRecorder.NeedsReissue(prompt.Calculate().Due, ParsePaymentPromptDetails(prompt.Details).AmountMsat);

    public JsonSerializer Serializer { get; } = BlobSerializer.CreateSerializer().Serializer;

    public Task BeforeFetchingRates(PaymentMethodContext context)
    {
        context.Prompt.Currency = "BTC";
        context.Prompt.Divisibility = 8;
        context.Prompt.PaymentMethodFee = 0m;
        // Only creation lacks the prompt: InvoiceActivator re-runs this on the stored invoice, which holds it.
        if (context.InvoiceEntity.GetPaymentPrompt(PaymentMethodId) is null)
        {
            // Core lets only ConfigurePrompt refuse a method cleanly, so a remembered rail is activated now to be refused there.
            if (LnurlRailProvisioning.LnurlValue(context.Store) is { } lnurl && UnverifiableRails.Knows(lnurl, Rail.OptionType, DateTimeOffset.UtcNow))
            {
                context.Prompt.Inactive = false;
                return Task.CompletedTask;
            }
            context.Prompt.Inactive = true;
            if (context.Status != PaymentMethodContext.ContextStatus.Excluded)
                PreferCoreDefault(context);
        }
        return Task.CompletedTask;
    }

    // Core falls back to BTC-CHAIN, BTC-LNURL, then the first prompt: without a wallet that can be this rail,
    // which the checkout would then activate, calling the LNURL, on every view.
    internal static void PreferCoreDefault(PaymentMethodContext context)
    {
        if (context.InvoiceEntity.DefaultPaymentMethod is not null || context.Store.GetDefaultPaymentId() is not null) return;
        context.InvoiceEntity.DefaultPaymentMethod =
            context.Store.GetPaymentMethodConfig(LnurlRailProvisioning.OnChain, onlyEnabled: true) is not null
                ? LnurlRailProvisioning.OnChain
                : LnurlRailProvisioning.Lightning;
    }

    public async Task ConfigurePrompt(PaymentMethodContext context)
    {
        var invoice = context.InvoiceEntity;
        if (invoice.Type == InvoiceType.TopUp)
            throw new PaymentMethodUnavailableException("top-up invoices have no amount to request");
        var lnurl = LnurlRailProvisioning.LnurlValue(context.Store)
                    ?? throw new PaymentMethodUnavailableException("the store's Lightning backend is not an LNURL");
        if (UnverifiableRails.Knows(lnurl, Rail.OptionType, DateTimeOffset.UtcNow))
            throw new UnverifiableRailException("settlement cannot be detected: the LNURL refused this rail as unverifiable within the last day");
        var due = context.Prompt.Calculate().Due;
        if (due <= 0m) throw new PaymentMethodUnavailableException("nothing is due");

        var http = _httpClientFactory.CreateClient(nameof(LnurlRailPaymentHandler));
        http.Timeout = TimeSpan.FromSeconds(15);
        PayerIp.Forward(http);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        ResolvedLnurl resolved;
        try { resolved = await LNURLVerifyConnectionStringHandler.ResolveCached(lnurl, _network, http, cts.Token); }
        catch (Exception e) { throw new PaymentMethodUnavailableException($"the LNURL could not be resolved ({e.Message})"); }
        RailDestination d;
        try { d = await LnurlRailRequester.Request(http, resolved.PayEndpoint, Rail, MsatOf(due), invoice.ExpirationTime, _network, cts.Token); }
        catch (UnverifiableRailException)
        {
            UnverifiableRails.Remember(lnurl, Rail.OptionType, DateTimeOffset.UtcNow);
            throw;
        }

        context.Prompt.Details = JObject.FromObject(new LnurlRailPromptDetails
        {
            OptionId = d.OptionId, PaymentUri = d.PaymentUri, Verify = d.Verify, VerifyBatch = d.VerifyBatch,
            AmountMsat = d.AmountMsat, Superseded = SupersededBy(invoice.GetPaymentPrompt(PaymentMethodId))
        }, Serializer);
        context.Prompt.Destination = d.Destination;
        context.TrackedDestinations.Add(d.Destination);
    }

    public Task AfterSavingInvoice(PaymentMethodContext context)
    {
        if (context.Prompt is { Activated: true, Details: { } details })
            foreach (var d in Destinations(context.InvoiceEntity, PaymentMethodId, context.Prompt.Destination, ParsePaymentPromptDetails(details)))
                TrackedDestinationRegistry.Add(d);
        return Task.CompletedTask;
    }

    internal List<SupersededRailDestination> SupersededBy(PaymentPrompt? previous)
    {
        if (previous is not { Activated: true, Details: { } json }) return new List<SupersededRailDestination>();
        var old = ParsePaymentPromptDetails(json);
        old.Superseded.Add(new SupersededRailDestination
            { Destination = previous.Destination, Verify = old.Verify, VerifyBatch = old.VerifyBatch, AmountMsat = old.AmountMsat });
        return old.Superseded;
    }

    public static IEnumerable<TrackedDestination> Destinations(InvoiceEntity invoice, PaymentMethodId pmi, string destination,
        LnurlRailPromptDetails details)
    {
        yield return new TrackedDestination(invoice.Id, pmi.ToString(), destination, details.Verify, details.VerifyBatch,
            details.AmountMsat, invoice.MonitoringExpiration);
        foreach (var s in details.Superseded)
            yield return new TrackedDestination(invoice.Id, pmi.ToString(), s.Destination, s.Verify, s.VerifyBatch,
                s.AmountMsat, invoice.MonitoringExpiration);
    }

    public static long MsatOf(decimal btc) => decimal.ToInt64(decimal.Round(btc * 100_000_000m)) * 1000;

    public static decimal AmountBtc(long msat) => msat / 100_000_000_000m;

    public LnurlRailPromptDetails ParsePaymentPromptDetails(JToken details) =>
        details.ToObject<LnurlRailPromptDetails>(Serializer) ?? throw new FormatException($"Invalid {nameof(LnurlRailPromptDetails)}");

    object IPaymentMethodHandler.ParsePaymentPromptDetails(JToken details) => ParsePaymentPromptDetails(details);

    public object ParsePaymentMethodConfig(JToken config) => config.ToObject<LnurlRailConfig>(Serializer) ?? new LnurlRailConfig();

    public LnurlRailPaymentData ParsePaymentDetails(JToken details) =>
        details.ToObject<LnurlRailPaymentData>(Serializer) ?? throw new FormatException($"Invalid {nameof(LnurlRailPaymentData)}");

    object IPaymentMethodHandler.ParsePaymentDetails(JToken details) => ParsePaymentDetails(details);
}
