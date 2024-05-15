using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBitcoin.Secp256k1;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NNostr.Client;
using WabiSabi.Crypto;
using WalletWasabi.Backend.Controllers;
using WalletWasabi.Logging;
using WalletWasabi.WabiSabi;
using WalletWasabi.WabiSabi.Backend.Models;
using WalletWasabi.WabiSabi.Backend.Rounds;
using WalletWasabi.WabiSabi.Crypto;
using WalletWasabi.WabiSabi.Models;
using WalletWasabi.WabiSabi.Models.Serialization;
using JsonSerializer = Newtonsoft.Json.JsonSerializer;

namespace BTCPayServer.Plugins.Wabisabi;

public class NostrWabisabiApiServer : IHostedService
{
    public static int RoundStateKind = 15751;
    public static int CommunicationKind = 25750;
    private readonly Arena _arena;
    private WabisabiCoordinatorSettings _coordinatorSettings;
    private NostrClient _client;
    private readonly ILogger _logger;
    private readonly string _coordinatorFilterId;

    // private Channel<NostrEvent> PendingEvents { get; } = Channel.CreateUnbounded<NostrEvent>();
    public NostrWabisabiApiServer(Arena arena, WabisabiCoordinatorSettings coordinatorSettings,
        ILogger logger)
    {
        _arena = arena;
        _coordinatorSettings = coordinatorSettings;
        _logger = logger;
        _coordinatorFilterId = new Guid().ToString();
        _serializer = JsonSerializer.Create(JsonSerializationOptions.Default.Settings);
    }

    private CancellationTokenSource _cts;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_coordinatorSettings.NostrRelay is null || string.IsNullOrEmpty(_coordinatorSettings.NostrIdentity))
        {
            _logger.LogInformation("NOSTR SERVER: No Nostr relay/identity configured, skipping");
            return;
        }

        _client = new NostrClient(_coordinatorSettings.NostrRelay);

        await _client.ConnectAndWaitUntilConnected(cancellationToken);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _logger.LogInformation($"NOSTR SERVER: CONNECTED TO {_coordinatorSettings.NostrRelay}");

        _evtSubscriptions = _client.SubscribeForEvents(new[]
        {
            new NostrSubscriptionFilter()
            {
                ReferencedPublicKeys = new[] {_coordinatorSettings.GetPubKey().ToHex()},
                Kinds = new[] {CommunicationKind},
                Limit = 0
            }
        }, false, _cts.Token);


        _ = RoutinelyUpdateRoundEvent(_cts.Token);
        _ = ProcessRequests(_cts.Token);
    }


    private async Task RoutinelyUpdateRoundEvent(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {

            var response = await _arena.GetStatusAsync(RoundStateRequest.Empty, cancellationToken);
            var nostrEvent = new NostrEvent()
            {
                Kind = RoundStateKind,
                Content =Serialize(response)
            };
            
            
            nostrEvent = await nostrEvent.ComputeIdAndSignAsync(_coordinatorSettings.GetKey());
            await _client.SendEventsAndWaitUntilReceived(new[] {nostrEvent}, cancellationToken);
            _logger.LogDebug($"NOSTR SERVER: PUBLISHED ROUND STATE {nostrEvent.Id}");
            await Task.Delay(1000, cancellationToken);
            
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
        }
    }

    private async Task ProcessRequests(CancellationToken cancellationToken)
    {
        await foreach (var evt in _evtSubscriptions.WithCancellation(cancellationToken))
        {
            try
            {
                var content =
                    JObject.Parse(await evt.DecryptNip04EventAsync(_coordinatorSettings.GetKey(), null, true));
                if (content.TryGetValue("action", out var actionJson) &&
                    actionJson.Value<string>(actionJson) is { } actionString &&
                    Enum.TryParse<RemoteAction>(actionString, out var action) &&
                    content.ContainsKey("request"))
                {
                    try
                    {
                        _logger.LogDebug($"NOSTR SERVER: Received request {evt.Id} {action}");
                        switch (action)
                        {
                            case RemoteAction.GetStatus:
                                // ignored as we use a dedicated public event for this to not spam
                                break;
                            case RemoteAction.RegisterInput:
                                var registerInputRequest =
                                    content["request"].ToObject<InputRegistrationRequest>(_serializer);
                                var registerInputResponse =
                                    await _arena.RegisterInputAsync(registerInputRequest, CancellationToken.None);
                                await Reply(evt, registerInputResponse, CancellationToken.None);
                                break;
                            case RemoteAction.RegisterOutput:
                                var registerOutputRequest =
                                    content["request"].ToObject<OutputRegistrationRequest>(_serializer);
                                await _arena.RegisterOutputAsync(registerOutputRequest, CancellationToken.None);
                                break;
                            case RemoteAction.RemoveInput:
                                var removeInputRequest = content["request"].ToObject<InputsRemovalRequest>(_serializer);
                                await _arena.RemoveInputAsync(removeInputRequest, CancellationToken.None);
                                break;
                            case RemoteAction.ConfirmConnection:
                                var connectionConfirmationRequest =
                                    content["request"].ToObject<ConnectionConfirmationRequest>(_serializer);
                                var connectionConfirmationResponse =
                                    await _arena.ConfirmConnectionAsync(connectionConfirmationRequest,
                                        CancellationToken.None);
                                await Reply(evt, connectionConfirmationResponse, CancellationToken.None);
                                break;
                            case RemoteAction.ReissueCredential:
                                var reissueCredentialRequest =
                                    content["request"].ToObject<ReissueCredentialRequest>(_serializer);
                                var reissueCredentialResponse =
                                    await _arena.ReissuanceAsync(reissueCredentialRequest, CancellationToken.None);
                                await Reply(evt, reissueCredentialResponse, CancellationToken.None);
                                break;
                            case RemoteAction.SignTransaction:
                                var transactionSignaturesRequest =
                                    content["request"].ToObject<TransactionSignaturesRequest>(_serializer);
                                await _arena.SignTransactionAsync(transactionSignaturesRequest, CancellationToken.None);
                                break;
                            case RemoteAction.ReadyToSign:
                                var readyToSignRequest =
                                    content["request"].ToObject<ReadyToSignRequestRequest>(_serializer);
                                await _arena.ReadyToSignAsync(readyToSignRequest, CancellationToken.None);
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        object response = ex switch
                        {
                            WabiSabiProtocolException wabiSabiProtocolException => new
                            {
                                Error = new Error(Type: ProtocolConstants.ProtocolViolationType,
                                    ErrorCode: wabiSabiProtocolException.ErrorCode.ToString(),
                                    Description: wabiSabiProtocolException.Message,
                                    ExceptionData: wabiSabiProtocolException.ExceptionData ??
                                                   EmptyExceptionData.Instance)
                            },
                            WabiSabiCryptoException wabiSabiCryptoException => new
                            {
                                Error = new Error(Type: ProtocolConstants.ProtocolViolationType,
                                    ErrorCode: WabiSabiProtocolErrorCode.CryptoException.ToString(),
                                    Description: wabiSabiCryptoException.Message,
                                    ExceptionData: EmptyExceptionData.Instance)
                            },
                            _ => new
                            {
                                Error = new Error(Type: "unknown", ErrorCode: ex.GetType().Name,
                                    Description: ex.Message, ExceptionData: EmptyExceptionData.Instance)
                            }
                        };

                        await Reply(evt, response, CancellationToken.None);
                    }
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "NOSTR SERVER: Failed to process event {0}", evt.Id);
            }
        }
    }

    private readonly JsonSerializer _serializer;
    private IAsyncEnumerable<NostrEvent> _evtSubscriptions;


    private async Task Reply<TResponse>(NostrEvent originaltEvent, TResponse response,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug($"NOSTR SERVER: REPLYING TO {originaltEvent.Id} WITH {response}");
        var evt = new NostrEvent()
        {
            Content = Serialize(response),
            PublicKey = _coordinatorSettings.GetPubKey().ToHex(),
            Kind = CommunicationKind,
            CreatedAt = DateTimeOffset.Now
        };
        evt.SetTag("p", originaltEvent.PublicKey);
        evt.SetTag("e", originaltEvent.Id);

        await evt.EncryptNip04EventAsync(_coordinatorSettings.GetKey(), null, true);
        await evt.ComputeIdAndSignAsync(_coordinatorSettings.GetKey());
        await _client.PublishEvent(evt, cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if(_cts is not null)
            await _cts?.CancelAsync();
        
        _client?.Dispose();
    }

    private static string Serialize<T>(T obj)
    {
        var raw =  JsonConvert.SerializeObject(obj, JsonSerializationOptions.Default.Settings);
        return raw;

    }

    private enum RemoteAction
    {
        RegisterInput,
        RemoveInput,
        ConfirmConnection,
        RegisterOutput,
        ReissueCredential,
        SignTransaction,
        GetStatus,
        ReadyToSign
    }

    public void UpdateSettings(WabisabiCoordinatorSettings wabisabiCoordinatorSettings)
    {
        _coordinatorSettings = wabisabiCoordinatorSettings;
    }
}