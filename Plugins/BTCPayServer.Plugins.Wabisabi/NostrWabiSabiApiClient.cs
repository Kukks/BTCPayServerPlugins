using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using NBitcoin;
using NBitcoin.Secp256k1;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NNostr.Client;
using WalletWasabi.Logging;
using WalletWasabi.WabiSabi;
using WalletWasabi.WabiSabi.Backend.Models;
using WalletWasabi.WabiSabi.Backend.PostRequests;
using WalletWasabi.WabiSabi.Models;
using WalletWasabi.WabiSabi.Models.Serialization;

namespace BTCPayServer.Plugins.Wabisabi;

public class NostrWabiSabiApiClient : IWabiSabiApiRequestHandler, IHostedService
{
    public static int RoundStateKind = 15750;
    public static int CommunicationKind = 25750;
    private readonly NostrClient _client;
    private readonly ECXOnlyPubKey _coordinatorKey;
    private string _coordinatorKeyHex => _coordinatorKey.ToHex();
    private readonly string _coordinatorFilterId;

    public NostrWabiSabiApiClient(NostrClient client, ECXOnlyPubKey coordinatorKey)
    {
        _client = client;
        _coordinatorKey = coordinatorKey;
        _coordinatorFilterId = new Guid().ToString();
    }


    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _ = _client.ListenForMessages();
        var filter = new NostrSubscriptionFilter()
        {
            Authors = new[] {_coordinatorKey.ToHex()},
            Kinds = new[] {RoundStateKind, CommunicationKind},
            Since = DateTimeOffset.UtcNow.Subtract(TimeSpan.FromHours(1))
        };
        await _client.CreateSubscription(_coordinatorFilterId, new[] {filter}, cancellationToken);
        _client.EventsReceived += EventsReceived;

        await _client.ConnectAndWaitUntilConnected(cancellationToken);
    }

    private RoundStateResponse _lastRoundState { get; set; }
    private TaskCompletionSource _lastRoundStateTask = new();


    private void EventsReceived(object sender, (string subscriptionId, NostrEvent[] events) e)
    {
        if (e.subscriptionId == _coordinatorFilterId)
        {
            var roundState = e.events.Where(evt => evt.Kind == RoundStateKind).MaxBy(@event => @event.CreatedAt);
            if (roundState != null)
            {
                _lastRoundState = Deserialize<RoundStateResponse>(roundState.Content);
                _lastRoundStateTask.TrySetResult();
            }
        }
    }

    private async Task SendAndWaitForReply<TRequest>(RemoteAction action, TRequest request,
        CancellationToken cancellationToken)
    {
        await SendAndWaitForReply<TRequest, JObject>(action, request, cancellationToken);
    }


    private async Task<TResponse> SendAndWaitForReply<TRequest, TResponse>(RemoteAction action, TRequest request,
        CancellationToken cancellationToken)
    {
        var newKey = ECPrivKey.Create(RandomUtils.GetBytes(32));
        var pubkey = newKey.CreateXOnlyPubKey();
        var evt = new NostrEvent()
        {
            Content = Serialize(new
            {
                Action = action,
                Request = request
            }),
            PublicKey = pubkey.ToHex(),
            Kind = 4,
            CreatedAt = DateTimeOffset.Now
        };
        evt.SetTag("p", _coordinatorKeyHex);

        await evt.EncryptNip04EventAsync(newKey);
        evt.Kind = CommunicationKind;
        await evt.ComputeIdAndSignAsync(newKey);
        var tcs = new TaskCompletionSource<NostrEvent>(cancellationToken);

        void OnClientEventsReceived(object sender, (string subscriptionId, NostrEvent[] events) e)
        {
            foreach (var nostrEvent in e.events)
            {
                if (nostrEvent.PublicKey != _coordinatorKeyHex) continue;
                var replyToEvent = evt.GetTaggedData("e");
                var replyToUser = evt.GetTaggedData("p");
                if (replyToEvent.All(s => s != evt.Id) || replyToUser.All(s => s != evt.PublicKey)) continue;
                if (!nostrEvent.Verify()) continue;
                _client.EventsReceived -= OnClientEventsReceived;
                tcs.TrySetResult(nostrEvent);
                break;
            }
        }

        _client.EventsReceived += OnClientEventsReceived;
        try
        {
            var replyEvent = await tcs.Task;
            replyEvent.Kind = 4;
            var response = await replyEvent.DecryptNip04EventAsync(newKey);
            var jobj = JObject.Parse(response);
            if (jobj.TryGetValue("error", out var errorJson))
            {
                var contentString = errorJson.Value<string>();
                var error = JsonConvert.DeserializeObject<Error>(contentString, new JsonSerializerSettings()
                {
                    Converters = JsonSerializationOptions.Default.Settings.Converters,
                    Error = (_, e) => e.ErrorContext.Handled = true // Try to deserialize an Error object
                });
                var innerException = error switch
                {
                    {Type: ProtocolConstants.ProtocolViolationType} => Enum.TryParse<WabiSabiProtocolErrorCode>(
                        error.ErrorCode, out var code)
                        ? new WabiSabiProtocolException(code, error.Description, exceptionData: error.ExceptionData)
                        : new NotSupportedException(
                            $"Received WabiSabi protocol exception with unknown '{error.ErrorCode}' error code.\n\tDescription: '{error.Description}'."),
                    {Type: "unknown"} => new Exception(error.Description),
                    _ => null
                };

                if (innerException is not null)
                {
                    throw new HttpRequestException("Remote coordinator responded with an error.", innerException);
                }

                // Remove " from beginning and end to ensure backwards compatibility and it's kind of trash, too.
                if (contentString.Count(f => f == '"') <= 2)
                {
                    contentString = contentString.Trim('"');
                }

                var errorMessage = string.Empty;
                if (!string.IsNullOrWhiteSpace(contentString))
                {
                    errorMessage = $"\n{contentString}";
                }


                throw new HttpRequestException($"ERROR:{errorMessage}");
            }

            return jobj.ToObject<TResponse>(JsonSerializer.Create(JsonSerializationOptions.Default.Settings));
        }
        catch (OperationCanceledException e)
        {
            _client.EventsReceived -= OnClientEventsReceived;
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _client.CloseSubscription(_coordinatorFilterId, cancellationToken);
        _client.EventsReceived -= EventsReceived;
    }

    public async Task<RoundStateResponse> GetStatusAsync(RoundStateRequest request, CancellationToken cancellationToken)
    {
        await _lastRoundStateTask.Task;
        return _lastRoundState;
    }

    public Task<InputRegistrationResponse> RegisterInputAsync(InputRegistrationRequest request,
        CancellationToken cancellationToken) =>
        SendAndWaitForReply<InputRegistrationRequest, InputRegistrationResponse>(RemoteAction.RegisterInput, request,
            cancellationToken);

    public Task<ConnectionConfirmationResponse> ConfirmConnectionAsync(ConnectionConfirmationRequest request,
        CancellationToken cancellationToken) =>
        SendAndWaitForReply<ConnectionConfirmationRequest, ConnectionConfirmationResponse>(
            RemoteAction.ConfirmConnection, request, cancellationToken);

    public Task RegisterOutputAsync(OutputRegistrationRequest request, CancellationToken cancellationToken) =>
        SendAndWaitForReply(RemoteAction.RegisterOutput, request, cancellationToken);

    public Task<ReissueCredentialResponse> ReissuanceAsync(ReissueCredentialRequest request,
        CancellationToken cancellationToken) =>
        SendAndWaitForReply<ReissueCredentialRequest, ReissueCredentialResponse>(RemoteAction.ReissueCredential,
            request, cancellationToken);

    public Task RemoveInputAsync(InputsRemovalRequest request, CancellationToken cancellationToken) =>
        SendAndWaitForReply(RemoteAction.RemoveInput, request, cancellationToken);

    public virtual Task SignTransactionAsync(TransactionSignaturesRequest request,
        CancellationToken cancellationToken) =>
        SendAndWaitForReply(RemoteAction.SignTransaction, request, cancellationToken);

    public Task ReadyToSignAsync(ReadyToSignRequestRequest request, CancellationToken cancellationToken) =>
        SendAndWaitForReply(RemoteAction.ReadyToSign, request, cancellationToken);


    private static string Serialize<T>(T obj)
        => JsonConvert.SerializeObject(obj, JsonSerializationOptions.Default.Settings);

    private static TResponse Deserialize<TResponse>(string jsonString)
    {
        try
        {
            return JsonConvert.DeserializeObject<TResponse>(jsonString, JsonSerializationOptions.Default.Settings)
                   ?? throw new InvalidOperationException("Deserialization error");
        }
        catch
        {
            Logger.LogDebug($"Failed to deserialize {typeof(TResponse)} from JSON '{jsonString}'");
            throw;
        }
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
}