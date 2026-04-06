using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.HostedServices;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Wallets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBXplorer.DerivationStrategy;

namespace BTCPayServer.Plugins.Electrum.Services;

/// <summary>
/// Replaces NBXplorerListener. Subscribes to Electrum notifications for
/// scripthash changes and new blocks, then matches incoming transactions
/// against open invoices.
/// </summary>
public class ElectrumListener : IHostedService
{
    private readonly ElectrumClient _electrumClient;
    private readonly ElectrumWalletTracker _tracker;
    private readonly ElectrumStatusMonitor _statusMonitor;
    private readonly BTCPayWalletProvider _walletProvider;
    private readonly InvoiceRepository _invoiceRepository;
    private readonly EventAggregator _eventAggregator;
    private readonly PaymentService _paymentService;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly ILogger<ElectrumListener> _logger;
    private CancellationTokenSource _cts;
    private Task _listenTask;

    public ElectrumListener(
        ElectrumClient electrumClient,
        ElectrumWalletTracker tracker,
        ElectrumStatusMonitor statusMonitor,
        BTCPayWalletProvider walletProvider,
        InvoiceRepository invoiceRepository,
        EventAggregator eventAggregator,
        PaymentService paymentService,
        PaymentMethodHandlerDictionary handlers,
        BTCPayNetworkProvider networkProvider,
        ILogger<ElectrumListener> logger)
    {
        _electrumClient = electrumClient;
        _tracker = tracker;
        _statusMonitor = statusMonitor;
        _walletProvider = walletProvider;
        _invoiceRepository = invoiceRepository;
        _eventAggregator = eventAggregator;
        _paymentService = paymentService;
        _handlers = handlers;
        _networkProvider = networkProvider;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _electrumClient.OnScripthashNotification += OnScripthashNotification;
        _electrumClient.OnNewBlock += OnNewBlock;
        _electrumClient.OnReconnected += OnReconnected;

        _listenTask = RunAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _electrumClient.OnScripthashNotification -= OnScripthashNotification;
        _electrumClient.OnNewBlock -= OnNewBlock;
        _electrumClient.OnReconnected -= OnReconnected;

        _cts?.Cancel();
        if (_listenTask != null)
        {
            try { await _listenTask; } catch (OperationCanceledException) { }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        // Wait for Electrum connection
        while (!ct.IsCancellationRequested)
        {
            if (_electrumClient.IsConnected)
                break;
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { return; }
        }

        try
        {
            await _tracker.InitializeAsync(ct);

            var header = await _electrumClient.HeadersSubscribeAsync(ct);
            _statusMonitor.UpdateTipHeight(header.Height);

            await FindPaymentsViaPolling(ct);

            _logger.LogInformation("Electrum listener initialized, tip height: {Height}", header.Height);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during Electrum listener initialization");
        }
    }

    private void OnScripthashNotification(string scripthash, string status)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var ct = _cts?.Token ?? CancellationToken.None;
                var newTxs = await _tracker.HandleScripthashNotificationAsync(scripthash, status, ct);

                if (newTxs == null || newTxs.Count == 0) return;

                var network = _networkProvider.GetNetwork<BTCPayNetwork>("BTC");
                if (network == null) return;

                var wallet = _walletProvider.GetWallet(network);
                if (wallet == null) return;

                var pmi = PaymentTypes.CHAIN.GetPaymentMethodId(network.CryptoCode);

                foreach (var newTx in newTxs)
                {
                    await ProcessNewTransaction(newTx, network, wallet, pmi);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing scripthash notification for {Scripthash}", scripthash);
            }
        });
    }

    private void OnNewBlock(ElectrumHeaderNotification header)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var ct = _cts?.Token ?? CancellationToken.None;
                _statusMonitor.UpdateTipHeight(header.Height);

                await _tracker.HandleNewBlockAsync(header.Height, ct);

                var network = _networkProvider.GetNetwork<BTCPayNetwork>("BTC");
                if (network != null)
                {
                    var pmi = PaymentTypes.CHAIN.GetPaymentMethodId(network.CryptoCode);
                    _eventAggregator.Publish(new BTCPayServer.Events.NewBlockEvent { PaymentMethodId = pmi });

                    await UpdatePaymentStates(network, pmi, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing new block at height {Height}", header.Height);
            }
        });
    }

    private async Task OnReconnected()
    {
        try
        {
            var ct = _cts?.Token ?? CancellationToken.None;
            _logger.LogInformation("Electrum reconnected, re-initializing tracker");
            await _tracker.InitializeAsync(ct);
            var header = await _electrumClient.HeadersSubscribeAsync(ct);
            _statusMonitor.UpdateTipHeight(header.Height);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during reconnection re-initialization");
        }
    }

    private async Task ProcessNewTransaction(
        ElectrumWalletTracker.NewTransactionInfo txInfo,
        BTCPayNetwork network, BTCPayWallet wallet, PaymentMethodId pmi)
    {
        foreach (var output in txInfo.Outputs)
        {
            var invoice = await _invoiceRepository.GetInvoiceFromAddress(pmi, output.Address);
            if (invoice == null)
                continue;

            wallet.InvalidateCache(txInfo.DerivationStrategy);

            var paymentData = new PaymentData
            {
                Id = $"{txInfo.TxId}-{output.Index}",
                InvoiceDataId = invoice.Id,
                PaymentMethodId = pmi.ToString(),
                Status = txInfo.Confirmations > 0 ? PaymentStatus.Settled : PaymentStatus.Processing,
                Amount = output.Value.ToDecimal(MoneyUnit.BTC),
                Currency = network.CryptoCode,
                Created = txInfo.SeenAt
            };

            var handler = _handlers.GetBitcoinHandler(network);
            if (handler == null) continue;

            var details = new BitcoinLikePaymentData(
                new OutPoint(uint256.Parse(txInfo.TxId), (uint)output.Index),
                txInfo.IsRbf,
                new KeyPath(output.KeyPath),
                output.KeyIndex);

            paymentData.Set(invoice, handler, details);

            var payment = await _paymentService.AddPayment(paymentData, [txInfo.TxId]);
            if (payment != null)
            {
                _logger.LogInformation("Recorded payment {PaymentId} for invoice {InvoiceId}",
                    payment.Id, invoice.Id);
                _eventAggregator.Publish(new InvoiceEvent(invoice, InvoiceEvent.ReceivedPayment)
                {
                    Payment = payment
                });
            }
        }
    }

    private async Task FindPaymentsViaPolling(CancellationToken ct)
    {
        var network = _networkProvider.GetNetwork<BTCPayNetwork>("BTC");
        if (network == null) return;

        var pmi = PaymentTypes.CHAIN.GetPaymentMethodId(network.CryptoCode);
        var wallet = _walletProvider.GetWallet(network);
        if (wallet == null) return;

        var handler = _handlers.GetBitcoinHandler(network);
        if (handler == null) return;

        var invoices = await _invoiceRepository.GetMonitoredInvoices(pmi);
        var paymentCount = 0;

        foreach (var invoice in invoices)
        {
            try
            {
                var prompt = invoice.GetPaymentPrompt(pmi);
                if (prompt?.Details == null) continue;

                var promptDetails = handler.ParsePaymentPromptDetails(prompt.Details);
                if (promptDetails?.AccountDerivation == null) continue;

                var strategy = promptDetails.AccountDerivation;
                var coins = await wallet.GetUnspentCoins(strategy, cancellation: ct);

                var alreadyAccounted = invoice.GetPayments(false)
                    .Select(p =>
                    {
                        var d = handler.ParsePaymentDetails(p.Details);
                        return d.Outpoint;
                    }).ToHashSet();

                foreach (var coin in coins)
                {
                    if (alreadyAccounted.Contains(coin.OutPoint))
                        continue;

                    var tx = await wallet.GetTransactionAsync(coin.OutPoint.Hash, cancellation: ct);
                    if (tx == null) continue;

                    wallet.InvalidateCache(strategy);

                    var paymentData = new PaymentData
                    {
                        Id = coin.OutPoint.ToString(),
                        InvoiceDataId = invoice.Id,
                        PaymentMethodId = pmi.ToString(),
                        Status = tx.Confirmations > 0 ? PaymentStatus.Settled : PaymentStatus.Processing,
                        Amount = ((Money)coin.Value).ToDecimal(MoneyUnit.BTC),
                        Currency = network.CryptoCode,
                        Created = tx.Timestamp
                    };

                    var details = new BitcoinLikePaymentData(
                        coin.OutPoint,
                        tx.Transaction?.RBF ?? false,
                        coin.KeyPath,
                        coin.KeyIndex);

                    paymentData.Set(invoice, handler, details);

                    var payment = await _paymentService.AddPayment(paymentData, [coin.OutPoint.Hash.ToString()]);
                    if (payment != null) paymentCount++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error polling payments for invoice {InvoiceId}", invoice.Id);
            }
        }

        if (paymentCount > 0)
            _logger.LogInformation("Found {Count} payments via polling", paymentCount);
    }

    private async Task UpdatePaymentStates(BTCPayNetwork network, PaymentMethodId pmi, CancellationToken ct)
    {
        var invoices = await _invoiceRepository.GetMonitoredInvoices(pmi);
        foreach (var invoice in invoices)
        {
            _eventAggregator.Publish(new InvoiceNeedUpdateEvent(invoice.Id));
        }
    }
}
