#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Payments;

namespace BTCPayServer.Plugins.LNURLVerify;

/// <summary>
/// Hands the networks a checkout asked for to the token handler, which core's activator has no way to pass, and runs one
/// activation per invoice and asset at a time: two overlapping activations would each save a prompt missing the other's quote.
/// </summary>
public class TokenActivations
{
    // ponytail: 64 striped locks keep memory bounded; invoices sharing a stripe only queue. Per-key locks if that ever shows.
    private readonly SemaphoreSlim[] _stripes = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private readonly ConcurrentDictionary<(string InvoiceId, PaymentMethodId Pmi), string[]> _pending = new();

    public async Task<bool> Run(string invoiceId, PaymentMethodId pmi, IReadOnlyCollection<string> networks, Func<Task<bool>> activate)
    {
        var stripe = _stripes[(uint)HashCode.Combine(invoiceId, pmi) % (uint)_stripes.Length];
        await stripe.WaitAsync();
        try
        {
            _pending[(invoiceId, pmi)] = networks.ToArray();
            return await activate();
        }
        finally
        {
            _pending.TryRemove((invoiceId, pmi), out _);
            stripe.Release();
        }
    }

    public IReadOnlyCollection<string> Take(string invoiceId, PaymentMethodId pmi) =>
        _pending.TryRemove((invoiceId, pmi), out var networks) ? networks : Array.Empty<string>();
}
