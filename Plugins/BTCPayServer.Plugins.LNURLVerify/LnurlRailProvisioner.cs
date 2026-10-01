#nullable enable
using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.HostedServices;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Network = NBitcoin.Network;

namespace BTCPayServer.Plugins.LNURLVerify;

/// <summary>Keeps every store's LNURL-* configs matching what its LNURL advertises.</summary>
public class LnurlRailProvisioner : EventHostedServiceBase
{
    private static readonly TimeSpan SweepEvery = TimeSpan.FromHours(1);
    private sealed class Sweep { }

    private readonly StoreRepository _stores;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Network _network;
    private readonly ILogger _logger;
    private Timer? _timer;

    public LnurlRailProvisioner(EventAggregator eventAggregator, ILogger<LnurlRailProvisioner> logger, StoreRepository stores,
        IHttpClientFactory httpClientFactory, BTCPayNetworkProvider networks) : base(eventAggregator, logger)
    {
        _stores = stores;
        _httpClientFactory = httpClientFactory;
        _network = networks.BTC.NBitcoinNetwork;
        _logger = logger;
    }

    protected override void SubscribeToEvents()
    {
        Subscribe<StoreEvent.Created>();
        Subscribe<StoreEvent.Updated>();
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await base.StartAsync(cancellationToken);
        _timer = new Timer(_ => PushEvent(new Sweep()), null, TimeSpan.Zero, SweepEvery);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_timer is not null) await _timer.DisposeAsync();
        await base.StopAsync(cancellationToken);
    }

    protected override async Task ProcessEvent(object evt, CancellationToken cancellationToken)
    {
        if (evt is Sweep)
        {
            foreach (var store in await _stores.GetStores())
                await Provision(store, cancellationToken);
        }
        else if (evt is StoreEvent e && await _stores.FindStore(e.StoreId) is { } store)
        {
            await Provision(store, cancellationToken);
        }
    }

    private async Task Provision(StoreData store, CancellationToken ct)
    {
        try
        {
            var lnurl = LnurlRailProvisioning.LnurlValue(store);
            var offered = lnurl is null ? Array.Empty<string>() : await OfferedTypes(lnurl, ct);
            if (offered is null) return;
            // Our copy may predate a merchant's save; writing it back would revert that save.
            if (await _stores.FindStore(store.Id) is not { } fresh || LnurlRailProvisioning.LnurlValue(fresh) != lnurl) return;
            if (LnurlRailProvisioning.Reconcile(fresh, LnurlRailProvisioning.Desired(fresh, offered)))
                await _stores.UpdateStore(fresh);
        }
        // A malformed config JSON or a failed UpdateStore on one store must not stop every later store from being reconciled.
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(e, "Could not provision LNURL rails for store {StoreId}", store.Id);
        }
    }

    // Null when the LNURL cannot be read: a transient outage must not strip a store's rails.
    private async Task<string[]?> OfferedTypes(string lnurl, CancellationToken ct)
    {
        try
        {
            var http = _httpClientFactory.CreateClient(nameof(LnurlRailProvisioner));
            http.Timeout = TimeSpan.FromSeconds(15);
            var resolved = await LNURLVerifyConnectionStringHandler.ResolveCached(lnurl, _network, http, ct);
            var pay = await LNURLResolver.GetJson(http, resolved.PayEndpoint, ct);
            if (!string.Equals(pay["tag"]?.Value<string>(), "payRequest", StringComparison.Ordinal)) return null;
            return PaymentOption.Parse(pay).Select(o => o.Type).ToArray();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            _logger.LogDebug(e, "Could not read the payment options of {Lnurl}", lnurl);
            return null;
        }
    }
}
