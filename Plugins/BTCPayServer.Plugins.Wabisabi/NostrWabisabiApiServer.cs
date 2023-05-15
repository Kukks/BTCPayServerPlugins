using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using NBitcoin;
using NBitcoin.Secp256k1;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NNostr.Client;
using WabiSabi.Crypto;
using WalletWasabi.Logging;
using WalletWasabi.WabiSabi;
using WalletWasabi.WabiSabi.Backend.Models;
using WalletWasabi.WabiSabi.Backend.Rounds;
using WalletWasabi.WabiSabi.Crypto;
using WalletWasabi.WabiSabi.Models;
using WalletWasabi.WabiSabi.Models.Serialization;

namespace BTCPayServer.Plugins.Wabisabi;

public class NostrWabisabiApiServer: IHostedService
{
    public static int RoundStateKind = 15750;
    public static int CommunicationKind = 25750;
    private readonly Arena _arena;
    private readonly NostrClient _client;
    private readonly ECPrivKey _coordinatorKey;
    private string _coordinatorKeyHex => _coordinatorKey.CreateXOnlyPubKey().ToHex();
    private readonly string _coordinatorFilterId;

    private Channel<NostrEvent> PendingEvents { get; } = Channel.CreateUnbounded<NostrEvent>();
    public NostrWabisabiApiServer(Arena arena,NostrClient client, ECPrivKey coordinatorKey)
    {
        _arena = arena;
        _client = client;
        _coordinatorKey = coordinatorKey;
        _coordinatorFilterId = new Guid().ToString();
        _serializer = JsonSerializer.Create(JsonSerializationOptions.Default.Settings);
    }


    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _ = _client.ListenForMessages();
        var filter = new NostrSubscriptionFilter()
        {
            ReferencedPublicKeys = new[] {_coordinatorKey.ToHex()},
            Kinds = new[] { CommunicationKind},
            Since = DateTimeOffset.UtcNow.Subtract(TimeSpan.FromHours(1))
        };
        await _client.CreateSubscription(_coordinatorFilterId, new[] {filter}, cancellationToken);
        _client.EventsReceived += EventsReceived;

        await _client.ConnectAndWaitUntilConnected(cancellationToken);
        _ = RoutinelyUpdateRoundEvent(cancellationToken);
        _ = ProcessRequests(cancellationToken);
        
    }

    private void EventsReceived(object sender, (string subscriptionId, NostrEvent[] events) e)
    {
            if (e.subscriptionId != _coordinatorFilterId) return;
            var requests = e.events.Where(evt =>
                evt.Kind == CommunicationKind &&
                evt.GetTaggedData("p").Any(s => s == _coordinatorKeyHex) && evt.Verify());
            foreach (var request in requests)
                PendingEvents.Writer.TryWrite(request); 
    }


    private async Task RoutinelyUpdateRoundEvent(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var response  = await _arena.GetStatusAsync(RoundStateRequest.Empty, cancellationToken);
            var nostrEvent = new NostrEvent()
            {
                Kind = RoundStateKind,
                PublicKey = _coordinatorKeyHex,
                CreatedAt = DateTimeOffset.Now,
                Content = Serialize(response)
            };
            await _client.PublishEvent(nostrEvent, cancellationToken);
            await Task.Delay(1000, cancellationToken);
        }
    }

    private async Task ProcessRequests(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested &&
               await PendingEvents.Reader.WaitToReadAsync(cancellationToken) &&
               PendingEvents.Reader.TryRead(out var evt))
        {
            evt.Kind = 4;
            var content = JObject.Parse(await evt.DecryptNip04EventAsync(_coordinatorKey));
            if (content.TryGetValue("action", out var actionJson) &&
                actionJson.Value<string>(actionJson) is { } actionString &&
                Enum.TryParse<RemoteAction>(actionString, out var action) &&
                content.ContainsKey("request"))
            {
                try
                {
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
    }

    private readonly JsonSerializer _serializer;



    private async Task Reply<TResponse>(NostrEvent originaltEvent,TResponse response, 
        CancellationToken cancellationToken)
    {
        var evt = new NostrEvent()
        {
            Content = Serialize(response),
            PublicKey = _coordinatorKeyHex,
            Kind = 4,
            CreatedAt = DateTimeOffset.Now
        };
        evt.SetTag("p", originaltEvent.PublicKey);
        evt.SetTag("e", originaltEvent.Id);

        await evt.EncryptNip04EventAsync(_coordinatorKey);
        evt.Kind = CommunicationKind;
        await evt.ComputeIdAndSignAsync(_coordinatorKey);
        await _client.PublishEvent(evt, cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _client.CloseSubscription(_coordinatorFilterId, cancellationToken);
        _client.EventsReceived -= EventsReceived;
    }

    private static string Serialize<T>(T obj)
        => JsonConvert.SerializeObject(obj, JsonSerializationOptions.Default.Settings);
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
}