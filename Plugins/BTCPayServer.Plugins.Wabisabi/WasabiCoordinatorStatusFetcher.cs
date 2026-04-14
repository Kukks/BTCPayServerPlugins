using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WalletWasabi.Bases;
using WalletWasabi.WabiSabi.Backend.PostRequests;
using WalletWasabi.WabiSabi.Client;
using WalletWasabi.WabiSabi.Models;

namespace BTCPayServer.Plugins.Wabisabi;

public class WasabiCoordinatorStatusFetcher : PeriodicRunner, IWasabiBackendStatusProvider
{
    private readonly IWabiSabiApiRequestHandler _wasabiClient;
    private readonly ILogger _logger;
    private readonly Action _onRemove;
    public bool Connected { get; set; } = false;
    public bool? OverrideConnected { get; set; }

    public WasabiCoordinatorStatusFetcher(IWabiSabiApiRequestHandler wasabiClient, ILogger logger, Action onRemove) :
        base(TimeSpan.FromSeconds(30))
    {
        _wasabiClient = wasabiClient;
        _logger = logger;
        _onRemove = onRemove;
    }

    private int _retries = 0;

    protected override async Task ActionAsync(CancellationToken cancel)
    {
        try
        {
            if (OverrideConnected is { })
            {
                Connected = OverrideConnected.Value;
            }
            else
            {
                var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                cts.CancelAfter(30000);
                await _wasabiClient.GetStatusAsync(new RoundStateRequest(ImmutableList<RoundStateCheckpoint>.Empty),
                    cts.Token);
                if (!Connected)
                {
                    _logger.LogInformation("Connected to coordinator");
                }

                Connected = true;
                _retries = 0;
            }
        }
        catch (Exception e)
        {
            Connected = false;
            _retries++;
            if (_retries > 5)
            {
                _logger.LogError(e, "Could not connect to the coordinator after 5 retries, removing from the system");
                _onRemove.Invoke();
            }

            await Task.Delay(Period * _retries, cancel);
            throw new Exception("Could not connect to the coordinator", e);
        }
    }
}