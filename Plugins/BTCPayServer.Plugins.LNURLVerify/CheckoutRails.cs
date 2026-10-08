#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Http;

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

    /// <summary>
    /// The network a token activation asks the LNURL for: the one the payer tapped, unless its quote is still live. Never any on
    /// open, whatever the rails' setting says, because a token quote can be a real order with a third party.
    /// </summary>
    public static IReadOnlyCollection<string> PlanTokens(LnurlTokenPromptDetails? details, string? network, long dueMsat, DateTimeOffset now) =>
        details?.Networks.FirstOrDefault(n => n.OptionId == network) is { Refused: false } tapped && tapped.State(dueMsat, now) != "live"
            ? new[] { tapped.OptionId }
            : Array.Empty<string>();
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

/// <summary>The checkout payer's IP, sent as X-Forwarded-For on the LNURL requests made on their behalf.</summary>
public static class PayerIp
{
    // Ambient because core's InvoiceActivator stands between the checkout request and the handlers, and passes nothing through.
    private static readonly AsyncLocal<IPAddress?> _current = new();

    public static IPAddress? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }

    /// <summary>BTCPay takes X-Forwarded-For from any sender, so it names the payer only behind a local proxy.</summary>
    public static IPAddress? Resolve(HttpContext context)
    {
        // ForwardedHeadersMiddleware overwrites this with the TCP peer it replaced; absent, nothing was forwarded.
        var peer = context.Request.Headers["X-Original-For"].ToString();
        if (peer.Length == 0) return context.Connection.RemoteIpAddress;
        return IPEndPoint.TryParse(peer, out var endpoint) && IsLocal(endpoint.Address) ? context.Connection.RemoteIpAddress : null;
    }

    public static void Forward(HttpClient http)
    {
        var ip = Current;
        if (ip is { IsIPv4MappedToIPv6: true }) ip = ip.MapToIPv4();
        if (ip is null || IsLocal(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return;
        http.DefaultRequestHeaders.Add("X-Forwarded-For", ip.ToString());
    }

    static bool IsLocal(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6) return ip.IsIPv6UniqueLocal || ip.IsIPv6LinkLocal;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && (b[1] & 0xF0) == 16) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
    }
}
