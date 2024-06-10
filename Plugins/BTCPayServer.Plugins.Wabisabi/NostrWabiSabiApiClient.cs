using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using NBitcoin;
using NBitcoin.Secp256k1;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NNostr.Client;
using WalletWasabi.Extensions;
using WalletWasabi.Logging;
using WalletWasabi.Tor.Socks5.Pool.Circuits;
using WalletWasabi.WabiSabi;
using WalletWasabi.WabiSabi.Backend.Models;
using WalletWasabi.WabiSabi.Backend.PostRequests;
using WalletWasabi.WabiSabi.Models;
using WalletWasabi.WabiSabi.Models.Serialization;

namespace BTCPayServer.Plugins.Wabisabi;

public class NostrWabiSabiApiClient : IWabiSabiApiRequestHandler, IHostedService, IDisposable
{
    public static int RoundStateKind = 15751;
    public static int CommunicationKind = 25750;
    private NostrClient _client;
    private readonly Uri _relay;
    private readonly WebProxy _webProxy;
    private readonly ECXOnlyPubKey _coordinatorKey;
    private readonly INamedCircuit _circuit;
    private string _coordinatorKeyHex => _coordinatorKey.ToHex();
    // private readonly string _coordinatorFilterId;

    public NostrWabiSabiApiClient(Uri relay, WebProxy webProxy , ECXOnlyPubKey coordinatorKey, INamedCircuit? circuit)
    {
        _relay = relay;
        _webProxy = webProxy;
        _coordinatorKey = coordinatorKey;
        _circuit = circuit;
        // _coordinatorFilterId = new Guid().ToString();
    }


    private CancellationTokenSource _cts;
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_circuit.IsActive)
        {
            Dispose();
        }
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_circuit is OneOffCircuit)
        {
            return;
            // we dont bootstrap, we do it on demand for a request instead
        }
        
        _ = Init(_cts.Token, _circuit);
    }

    
    private async Task Init(CancellationToken cancellationToken, INamedCircuit circuit)
    {
        while (cancellationToken.IsCancellationRequested == false)
        {
            _client = CreateClient(_relay, _webProxy, circuit);

            await _client.ConnectAndWaitUntilConnected(cancellationToken);

            _circuit.IsolationIdChanged += (_, _) =>
            {
                Dispose();
                _ = StartAsync(CancellationToken.None);
            };


            var subscriptions = _client.SubscribeForEvents(new[]
            {
                new NostrSubscriptionFilter()
                {
                    Authors = new[] {_coordinatorKey.ToHex()},
                    Kinds = new[] {RoundStateKind},
                    Limit = 1
                }
            }, false, cancellationToken);

            await HandleStateEvents(subscriptions);

        }
    }

    private async Task HandleStateEvents(IAsyncEnumerable<NostrEvent> subscriptions)
    {
        await foreach (var evt in subscriptions)
        {
            try
            {
                if (evt.Kind != RoundStateKind)
                    continue;
                if (_lastRoundStateEvent is not null && evt.CreatedAt <= _lastRoundStateEvent.CreatedAt)
                    continue;
                _lastRoundStateEvent = evt;

                _lastRoundState = Deserialize<RoundStateResponse>(evt.Content);
                _lastRoundStateTask.TrySetResult();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex);
            }
            

        }
    }

    private NostrEvent _lastRoundStateEvent { get; set; }
    private RoundStateResponse _lastRoundState { get; set; }
    private readonly TaskCompletionSource _lastRoundStateTask = new();
    

    private async Task SendAndWaitForReply<TRequest>(RemoteAction action, TRequest request,
        CancellationToken cancellationToken)
    {
        await SendAndWaitForReply<TRequest, JObject>(action, request, cancellationToken);
    }


    private async Task<TResponse> SendAndWaitForReply<TRequest, TResponse>(RemoteAction action, TRequest request,
        CancellationToken cancellationToken)
    {
        
        if(_circuit is OneOffCircuit)
        {
                using var subClient =
                    new NostrWabiSabiApiClient(_relay, _webProxy, _coordinatorKey, new PersonCircuit());
                await subClient.StartAsync(cancellationToken);
                return await subClient.SendAndWaitForReply<TRequest, TResponse>(action, request, cancellationToken);
        }
        
        
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
            Kind = CommunicationKind,
            CreatedAt = DateTimeOffset.Now
        };
        evt.SetTag("p", _coordinatorKeyHex);

        await evt.EncryptNip04EventAsync(newKey, null, true);
        evt = await evt.ComputeIdAndSignAsync(newKey, false);
        
        try
        {
            
            var replyEvent = await _client.SendEventAndWaitForReply(evt, cancellationToken);
            var response = await replyEvent.DecryptNip04EventAsync(newKey, null, true);
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
            _circuit.IncrementIsolationId();
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
    }

    public async Task<RoundStateResponse> GetStatusAsync(RoundStateRequest request, CancellationToken cancellationToken)
    {
        await _lastRoundStateTask.Task.WithCancellation(cancellationToken);
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

    public void Dispose()
    {
        _client?.Dispose();
        _cts?.Cancel();
        _client = null;
        _cts = null;
        
    }

    public static NostrClient CreateClient(Uri relay, WebProxy webProxy,  INamedCircuit namedCircuit )
    {
        return new NostrClient(relay, socket =>
        {
            if (socket is ClientWebSocket clientWebSocket && webProxy is { })
            {
                var proxy = new WebProxy(webProxy.Address, true, null,
                    new NetworkCredential(namedCircuit.Name,
                        namedCircuit.IsolationId.ToString()));
                clientWebSocket.Options.Proxy = proxy;
            }
        });
    }
}