#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;

namespace BTCPayServer.Plugins.LNURLVerify;

public class LnurlRailSettings
{
    public const string Key = "LNURLVerify.Rails";
    public bool ActivateAllRailsOnOpen { get; set; } = true;

    public static bool ApplyEnabledRails(StoreData store, IEnumerable<PaymentMethodId> managed, IReadOnlyCollection<string> shown,
        IReadOnlyCollection<string> enabled)
    {
        var blob = store.GetStoreBlob();
        // Only configured rails the page showed are touched, so a posted id for another method or an unseen rail keeps its exclusion.
        foreach (var pmi in managed)
        {
            var id = pmi.ToString();
            if (shown.Contains(id) && store.GetPaymentMethodConfig(pmi) is not null)
                blob.SetExcluded(pmi, !enabled.Contains(id));
        }
        return store.SetStoreBlob(blob);
    }
}

/// <summary>An invoice's Bitcoin rails, and which of them an activation request wakes.</summary>
public static class CheckoutRails
{
    public static bool Applies(InvoiceEntity invoice) =>
        invoice.GetPaymentPrompts().Any(p => LnurlRails.IsRail(p.PaymentMethodId));

    public static IReadOnlyList<PaymentPrompt> Rails(InvoiceEntity invoice)
    {
        var prompts = invoice.GetPaymentPrompts();
        var chain = prompts.TryGet(LnurlRailProvisioning.OnChain);
        var rails = new List<PaymentPrompt>();
        if (prompts.TryGet(LnurlRailProvisioning.Lightning) is { } ln) rails.Add(ln);
        if (chain is not null) rails.Add(chain);
        foreach (var rail in LnurlRails.All)
            if (!(rail.OnChain && chain is not null) && prompts.TryGet(rail.PaymentMethodId) is { } p)
                rails.Add(p);
        return rails;
    }

    public static (IReadOnlyList<PaymentMethodId> Activate, IReadOnlyList<PaymentMethodId> Skipped) Plan(
        IReadOnlyList<PaymentPrompt> rails, string? rail, bool activateAllOnOpen, Func<PaymentMethodId, bool> failedRecently)
    {
        var inactive = rails.Where(p => !p.Activated).Select(p => p.PaymentMethodId).ToList();
        if (rail is not null)
            return (inactive.Where(p => p.ToString().Equals(rail, StringComparison.OrdinalIgnoreCase)).ToList(), Array.Empty<PaymentMethodId>());
        if (!activateAllOnOpen)
            return (Array.Empty<PaymentMethodId>(), Array.Empty<PaymentMethodId>());
        return (inactive.Where(p => !failedRecently(p)).ToList(), inactive.Where(failedRecently).ToList());
    }

    /// <summary>The networks a token activation asks the LNURL for: the tapped one, or on open each one not yet quoted.</summary>
    public static IReadOnlyCollection<string> PlanTokens(LnurlTokenPromptDetails? details, string? network, bool activateAllOnOpen)
    {
        var open = details?.Networks.Where(n => !n.Refused).ToList() ?? new List<TokenNetworkState>();
        if (network is not null) return open.Any(n => n.OptionId == network) ? new[] { network } : Array.Empty<string>();
        return activateAllOnOpen ? open.Where(n => n.Quote is null && n.FailedAt is null).Select(n => n.OptionId).ToArray() : Array.Empty<string>();
    }
}

/// <summary>Activations the LNURL refused, so reopening a checkout within the hour does not repeat the callback.</summary>
public class RailActivationFailures
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);
    private readonly ConcurrentDictionary<(string InvoiceId, PaymentMethodId Rail), DateTimeOffset> _until = new();

    public void Record(string invoiceId, PaymentMethodId rail, DateTimeOffset now)
    {
        foreach (var kv in _until)
            if (kv.Value <= now) _until.TryRemove(kv.Key, out _);
        _until[(invoiceId, rail)] = now + Window;
    }

    public bool Recent(string invoiceId, PaymentMethodId rail, DateTimeOffset now) =>
        _until.TryGetValue((invoiceId, rail), out var until) && until > now;
}

/// <summary>Joins concurrent activations of one invoice's rail, so overlapping checkout requests cost the LNURL one callback.</summary>
public class RailActivationGate
{
    private readonly ConcurrentDictionary<(string InvoiceId, PaymentMethodId Rail), Lazy<Task<bool>>> _inFlight = new();

    public async Task<bool> Run(string invoiceId, PaymentMethodId rail, Func<Task<bool>> activate)
    {
        var mine = new Lazy<Task<bool>>(activate);
        var run = _inFlight.GetOrAdd((invoiceId, rail), mine);
        try { return await run.Value; }
        finally
        {
            if (ReferenceEquals(run, mine))
                _inFlight.TryRemove(new KeyValuePair<(string, PaymentMethodId), Lazy<Task<bool>>>((invoiceId, rail), mine));
        }
    }
}
