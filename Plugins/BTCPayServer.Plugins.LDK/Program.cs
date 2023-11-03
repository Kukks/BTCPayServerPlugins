using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Runtime.Internal.Util;
using BTCPayServer;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Configuration;
using BTCPayServer.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;
using NBXplorer;
using org.ldk.enums;
using org.ldk.structs;
using enums_Network = org.ldk.enums.Network;
using ILogger = Microsoft.Extensions.Logging.ILogger;
using Logger = org.ldk.structs.Logger;
using Network = NBitcoin.Network;
using OutPoint = org.ldk.structs.OutPoint;
using Path = System.IO.Path;

public class LDKService : IHostedService, PersistInterface, BroadcasterInterfaceInterface, FeeEstimatorInterface, EventHandlerInterface,  LoggerInterface, FilterInterface
{
    private readonly ILogger<LDKService> _logger;
    private readonly IFeeProviderFactory _feeProviderFactory;
    private readonly IOptions<DataDirectories> _dataDirectories;
    private readonly BTCPayNetwork _network;
    private readonly ExplorerClient _explorerClient;
    private readonly string _workDir;
    private readonly enums_Network _ldkNetwork;
    private readonly Logger _ldklogger;
    private readonly FeeEstimator _ldkfeeEstimator;
    private readonly BroadcasterInterface _ldkbroadcaster;
    private readonly Persist _ldkpersist;
    private readonly Filter _ldkfilter;
    private readonly NetworkGraph _ldkNetworkGraph;
    private readonly ChainMonitor _ldkChainMonitor;

    public LDKService(BTCPayNetworkProvider btcPayNetworkProvider,
        ExplorerClientProvider explorerClientProvider,
        ILogger<LDKService> logger,
        IFeeProviderFactory feeProviderFactory,
        IOptions<DataDirectories> dataDirectories)
    {
        _logger = logger;
        _feeProviderFactory = feeProviderFactory;
        _dataDirectories = dataDirectories;

        _network = btcPayNetworkProvider.GetNetwork<BTCPayNetwork>("BTC");
        _explorerClient = explorerClientProvider.GetExplorerClient(_network);
        _workDir = GetWorkDir();
        Directory.CreateDirectory(_workDir);

        _ldkNetwork = GetLdkNetwork(_network.NBitcoinNetwork);
        _ldklogger = Logger.new_impl(this);
        _ldkfeeEstimator = FeeEstimator.new_impl(this);
        _ldkbroadcaster = BroadcasterInterface.new_impl(this);
        _ldkpersist = Persist.new_impl(this);
        _ldkfilter = Filter.new_impl(this);
       
        _ldkNetworkGraph = NetworkGraph.of(_ldkNetwork, _ldklogger);
        _ldkChainMonitor = ChainMonitor.of( Option_FilterZ.Option_FilterZ_Some.some(_ldkfilter), _ldkbroadcaster, _ldklogger, _ldkfeeEstimator, _ldkpersist);
    }


    private static enums_Network GetLdkNetwork(Network network)
    {
        enums_Network? ldkNetwork = null;
        if (network.ChainName == ChainName.Mainnet)
            ldkNetwork = org.ldk.enums.Network.LDKNetwork_Bitcoin;
        else if (network.ChainName == ChainName.Testnet)
            ldkNetwork = org.ldk.enums.Network.LDKNetwork_Testnet;
        else if (network.ChainName == ChainName.Regtest)
            ldkNetwork = org.ldk.enums.Network.LDKNetwork_Regtest;

        return ldkNetwork ?? throw new NotSupportedException();
    }


    private string GetWorkDir()
    {
        var dir = _dataDirectories.Value.DataDir;
        return Path.Combine(dir, "Plugins", "LDK");
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public int get_est_sat_per_1000_weight(ConfirmationTarget confirmation_target)
    {
        var feeProvider = _feeProviderFactory.CreateFeeProvider(_network);
        var targetBlocks = confirmation_target switch
        {
            ConfirmationTarget.LDKConfirmationTarget_OnChainSweep => 30, // High priority (10-50 blocks)
            ConfirmationTarget
                    .LDKConfirmationTarget_MaxAllowedNonAnchorChannelRemoteFee =>
                20, // Moderate to high priority (small multiple of high-priority estimate)
            ConfirmationTarget
                    .LDKConfirmationTarget_MinAllowedAnchorChannelRemoteFee =>
                12, // Moderate priority (long-term mempool minimum or medium-priority)
            ConfirmationTarget
                    .LDKConfirmationTarget_MinAllowedNonAnchorChannelRemoteFee =>
                12, // Moderate priority (medium-priority feerate)
            ConfirmationTarget.LDKConfirmationTarget_AnchorChannelFee => 6, // Lower priority (can be bumped later)
            ConfirmationTarget
                .LDKConfirmationTarget_NonAnchorChannelFee => 20, // Moderate to high priority (high-priority feerate)
            ConfirmationTarget.LDKConfirmationTarget_ChannelCloseMinimum => 144, // Within a day or so (144-250 blocks)
            _ => throw new ArgumentOutOfRangeException(nameof(confirmation_target), confirmation_target, null)
        };
        return (int) Math.Max(253, feeProvider.GetFeeRateAsync(targetBlocks).GetAwaiter().GetResult().FeePerK.Satoshi);
    }

    public void log(Record record)
    {
        var level = record.get_level() switch
        {
            Level.LDKLevel_Trace => LogLevel.Trace,
            Level.LDKLevel_Debug => LogLevel.Debug,
            Level.LDKLevel_Info => LogLevel.Information,
            Level.LDKLevel_Warn => LogLevel.Warning,
            Level.LDKLevel_Error => LogLevel.Error,
            Level.LDKLevel_Gossip => LogLevel.Trace,
        };
        _logger.Log(level, $"[{record.get_module_path()}] {record.get_args()}");
    }

    public void broadcast_transactions(byte[][] txs)
    {
        foreach (var tx in txs)
        {
            var loadedTx = Transaction.Load(tx, _explorerClient.Network.NBitcoinNetwork);

            _explorerClient.Broadcast(loadedTx);
        }
    }


    public ChannelMonitorUpdateStatus persist_new_channel(OutPoint channel_id, ChannelMonitor data,
        MonitorUpdateId update_id)
    {
        var name = Convert.ToHexString(channel_id.write());
        File.WriteAllBytes(Path.Combine(_workDir, name), data.write());
        return ChannelMonitorUpdateStatus.LDKChannelMonitorUpdateStatus_Completed;
    }

    public ChannelMonitorUpdateStatus update_persisted_channel(OutPoint channel_id, ChannelMonitorUpdate update,
        ChannelMonitor data, MonitorUpdateId update_id)
    {
        var name = Convert.ToHexString(channel_id.write());
        File.WriteAllBytes(Path.Combine(_workDir, name), data.write());
        return ChannelMonitorUpdateStatus.LDKChannelMonitorUpdateStatus_Completed;
    }
    
    public void handle_event(Event _event)
    {
        switch (_event)
        {
            case Event.Event_BumpTransaction eventBumpTransaction:
                switch (eventBumpTransaction.bump_transaction)
                {
                    case BumpTransactionEvent.BumpTransactionEvent_ChannelClose bumpTransactionEventChannelClose:
                        break;
                    case BumpTransactionEvent.BumpTransactionEvent_HTLCResolution bumpTransactionEventHtlcResolution:
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                break;
            case Event.Event_ChannelClosed eventChannelClosed:
                break;
            case Event.Event_ChannelPending eventChannelPending:
                break;
            case Event.Event_ChannelReady eventChannelReady:
                break;
            case Event.Event_DiscardFunding eventDiscardFunding:
                break;
            case Event.Event_FundingGenerationReady eventFundingGenerationReady:
                break;
            case Event.Event_HTLCHandlingFailed eventHtlcHandlingFailed:
                break;
            case Event.Event_HTLCIntercepted eventHtlcIntercepted:
                break;
            case Event.Event_InvoiceRequestFailed eventInvoiceRequestFailed:
                break;
            case Event.Event_OpenChannelRequest eventOpenChannelRequest:
                break;
            case Event.Event_PaymentClaimable eventPaymentClaimable:
                break;
            case Event.Event_PaymentClaimed eventPaymentClaimed:
                break;
            case Event.Event_PaymentFailed eventPaymentFailed:
                break;
            case Event.Event_PaymentForwarded eventPaymentForwarded:
                break;
            case Event.Event_PaymentPathFailed eventPaymentPathFailed:
                break;
            case Event.Event_PaymentPathSuccessful eventPaymentPathSuccessful:
                break;
            case Event.Event_PaymentSent eventPaymentSent:
                break;
            case Event.Event_PendingHTLCsForwardable eventPendingHtlCsForwardable:
                break;
            case Event.Event_ProbeFailed eventProbeFailed:
                break;
            case Event.Event_ProbeSuccessful eventProbeSuccessful:
                break;
            case Event.Event_SpendableOutputs eventSpendableOutputs:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(_event));
        }
    }

    public void register_tx(byte[] txid, byte[] script_pubkey)
    {
        throw new NotImplementedException();
    }

    public void register_output(WatchedOutput output)
    {
        throw new NotImplementedException();
    }
}