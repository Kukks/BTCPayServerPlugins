using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
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
    public bool Connected { get; set; } = false;
    public bool? OverrideConnected { get; set; }
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
            if (OverrideConnected is { })
            {
                Connected = OverrideConnected.Value;
            }
            else
            {
                await _wasabiClient.GetStatusAsync(new RoundStateRequest(ImmutableList<RoundStateCheckpoint>.Empty), cancel);
                if (!Connected)
                {
                    _logger.LogInformation("Connected to coordinator"  );
                }
                Connected = true;
            }
            

        }
        catch (Exception e)
        {
            Connected = false;
            throw new Exception("Could not connect to the coordinator", e);
        }
    }
}
