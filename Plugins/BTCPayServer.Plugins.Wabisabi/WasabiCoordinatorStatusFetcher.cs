using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NBitcoin;
using WalletWasabi.Backend.Models.Responses;
using WalletWasabi.Bases;
using WalletWasabi.WabiSabi.Backend.PostRequests;
using WalletWasabi.WabiSabi.Client;
using WalletWasabi.WabiSabi.Models;
using WalletWasabi.WebClients.Wasabi;

namespace BTCPayServer.Plugins.Wabisabi;

public class WasabiCoordinatorStatusFetcher : PeriodicRunner, IWasabiBackendStatusProvider
{
    private readonly IWabiSabiApiRequestHandler _wasabiClient;
    private readonly ILogger _logger;
    public bool Connected { get; set; } = false;
    public WasabiCoordinatorStatusFetcher(IWabiSabiApiRequestHandler wasabiClient, ILogger logger) :
        base(TimeSpan.FromSeconds(30))
    {
        _wasabiClient = wasabiClient;
        _logger = logger;
    }

    protected override async Task ActionAsync(CancellationToken cancel)
    {
        try
        {
             await _wasabiClient.GetStatusAsync(new RoundStateRequest(ImmutableList<RoundStateCheckpoint>.Empty), cancel);
            if (!Connected)
            {
                _logger.LogInformation("Connected to coordinator"  );
            }

            Connected = true;
        }
        catch (Exception e)
        {
            Connected = false;
            _logger.LogError(e, "Could not connect to the coordinator ");
            throw;
        }
    }
}
