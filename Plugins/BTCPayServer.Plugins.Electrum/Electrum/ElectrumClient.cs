using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Services;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Electrum;

public class ElectrumHeaderNotification
{
    public int Height { get; set; }
    public string Hex { get; set; }
}

public class ElectrumHistoryItem
{
    public string TxHash { get; set; }
    public int Height { get; set; }
    public long Fee { get; set; }
}

public class ElectrumUnspentItem
{
    public string TxHash { get; set; }
    public int TxPos { get; set; }
    public long Value { get; set; }
    public int Height { get; set; }
}

public class ElectrumBalance
{
    public long Confirmed { get; set; }
    public long Unconfirmed { get; set; }
}

public class ElectrumClient : IAsyncDisposable
{
    private readonly SettingsRepository _settingsRepository;
    private readonly ILogger<ElectrumClient> _logger;

    private ElectrumSettings _settings;
    private TcpClient _tcpClient;
    private Stream _stream;
    private StreamReader _reader;
    private StreamWriter _writer;
    private int _nextId;
    private Task _readLoop;
    private CancellationTokenSource _cts;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ConcurrentDictionary<string, string> _subscribedScripthashes = new();
    private bool _headersSubscribed;

    public bool IsConnected { get; private set; }

    public event Action<ElectrumHeaderNotification> OnNewBlock;
    public event Action<string, string> OnScripthashNotification;
    public event Func<Task> OnReconnected;

    public ElectrumClient(SettingsRepository settingsRepository, ILogger<ElectrumClient> logger)
    {
        _settingsRepository = settingsRepository;
        _logger = logger;
    }

    /// <summary>
    /// Creates a client with explicit settings (for test connections).
    /// </summary>
    public ElectrumClient(ElectrumSettings settings, ILogger<ElectrumClient> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        if (IsConnected) return;

        // Load settings from DB if not provided via constructor
        _settings ??= await _settingsRepository.GetSettingAsync<ElectrumSettings>();

        if (string.IsNullOrEmpty(_settings?.Server))
            throw new InvalidOperationException("Electrum server not configured. Go to Server Settings > Electrum.");

        var parts = _settings.Server.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[1], out var port))
            throw new InvalidOperationException($"Invalid server format: {_settings.Server}. Expected host:port");

        var host = parts[0];

        _tcpClient = new TcpClient();
        await _tcpClient.ConnectAsync(host, port, ct);

        Stream stream = _tcpClient.GetStream();
        if (_settings.UseTls)
        {
            var sslStream = new SslStream(stream, false, (_, _, _, _) => true);
            await sslStream.AuthenticateAsClientAsync(host);
            stream = sslStream;
        }

        _stream = stream;
        _reader = new StreamReader(stream);
        _writer = new StreamWriter(stream) { AutoFlush = true, NewLine = "\n" };

        _cts = new CancellationTokenSource();
        IsConnected = true;
        _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token));

        _logger.LogInformation("Connected to Electrum server {Server}", _settings.Server);
    }

    public async Task DisconnectAsync()
    {
        IsConnected = false;
        _cts?.Cancel();

        foreach (var kvp in _pending)
        {
            kvp.Value.TrySetCanceled();
        }
        _pending.Clear();

        try { _reader?.Dispose(); } catch { }
        try { _writer?.Dispose(); } catch { }
        try { _stream?.Dispose(); } catch { }
        try { _tcpClient?.Dispose(); } catch { }

        if (_readLoop != null)
        {
            try { await _readLoop; } catch { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _writeLock.Dispose();
    }

    public async Task<(string serverSoftware, string protocolVersion)> ServerVersionAsync(
        string clientName, string protocolVersion, CancellationToken ct)
    {
        var result = await SendAsync("server.version", new object[] { clientName, protocolVersion }, ct);
        var arr = result.EnumerateArray().ToArray();
        return (arr[0].GetString(), arr[1].GetString());
    }

    public async Task<JsonElement> ServerFeaturesAsync(CancellationToken ct)
    {
        return await SendAsync("server.features", Array.Empty<object>(), ct);
    }

    public async Task PingAsync(CancellationToken ct)
    {
        await SendAsync("server.ping", Array.Empty<object>(), ct);
    }

    public async Task<ElectrumHeaderNotification> HeadersSubscribeAsync(CancellationToken ct)
    {
        var result = await SendAsync("blockchain.headers.subscribe", Array.Empty<object>(), ct);
        _headersSubscribed = true;
        return ParseHeaderNotification(result);
    }

    public async Task<string> ScripthashSubscribeAsync(string scripthash, CancellationToken ct)
    {
        var result = await SendAsync("blockchain.scripthash.subscribe", new object[] { scripthash }, ct);
        var status = result.ValueKind == JsonValueKind.Null ? null : result.GetString();
        _subscribedScripthashes[scripthash] = status;
        return status;
    }

    public async Task ScripthashUnsubscribeAsync(string scripthash, CancellationToken ct)
    {
        await SendAsync("blockchain.scripthash.unsubscribe", new object[] { scripthash }, ct);
        _subscribedScripthashes.TryRemove(scripthash, out _);
    }

    public async Task<ElectrumHistoryItem[]> ScripthashGetHistoryAsync(string scripthash, CancellationToken ct)
    {
        var result = await SendAsync("blockchain.scripthash.get_history", new object[] { scripthash }, ct);
        return result.EnumerateArray().Select(e => new ElectrumHistoryItem
        {
            TxHash = e.GetProperty("tx_hash").GetString(),
            Height = e.GetProperty("height").GetInt32(),
            Fee = e.TryGetProperty("fee", out var fee) ? fee.GetInt64() : 0
        }).ToArray();
    }

    public async Task<ElectrumUnspentItem[]> ScripthashListUnspentAsync(string scripthash, CancellationToken ct)
    {
        var result = await SendAsync("blockchain.scripthash.listunspent", new object[] { scripthash }, ct);
        return result.EnumerateArray().Select(e => new ElectrumUnspentItem
        {
            TxHash = e.GetProperty("tx_hash").GetString(),
            TxPos = e.GetProperty("tx_pos").GetInt32(),
            Value = e.GetProperty("value").GetInt64(),
            Height = e.GetProperty("height").GetInt32()
        }).ToArray();
    }

    public async Task<ElectrumBalance> ScripthashGetBalanceAsync(string scripthash, CancellationToken ct)
    {
        var result = await SendAsync("blockchain.scripthash.get_balance", new object[] { scripthash }, ct);
        return new ElectrumBalance
        {
            Confirmed = result.GetProperty("confirmed").GetInt64(),
            Unconfirmed = result.GetProperty("unconfirmed").GetInt64()
        };
    }

    public async Task<string> TransactionGetAsync(string txHash, CancellationToken ct)
    {
        var result = await SendAsync("blockchain.transaction.get", new object[] { txHash }, ct);
        return result.GetString();
    }

    public async Task<string> TransactionBroadcastAsync(string rawTx, CancellationToken ct)
    {
        var result = await SendAsync("blockchain.transaction.broadcast", new object[] { rawTx }, ct);
        return result.GetString();
    }

    public async Task<decimal> EstimateFeeAsync(int blockTarget, CancellationToken ct)
    {
        var result = await SendAsync("blockchain.estimatefee", new object[] { blockTarget }, ct);
        return result.GetDecimal();
    }

    private async Task<JsonElement> SendAsync(string method, object[] parameters, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var request = new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params = parameters
        };

        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

        var json = JsonSerializer.Serialize(request);

        await _writeLock.WaitAsync(ct);
        try
        {
            await _writer.WriteLineAsync(json);
        }
        finally
        {
            _writeLock.Release();
        }

        return await tcs.Task;
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await _reader.ReadLineAsync(ct);
                if (line == null)
                {
                    _logger.LogWarning("Electrum server closed connection");
                    break;
                }

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement.Clone();

                    if (root.TryGetProperty("id", out var idProp) && idProp.ValueKind != JsonValueKind.Null)
                    {
                        // Response to a request
                        var id = idProp.GetInt32();
                        if (_pending.TryRemove(id, out var tcs))
                        {
                            if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
                            {
                                var msg = error.TryGetProperty("message", out var m)
                                    ? m.GetString()
                                    : error.ToString();
                                tcs.TrySetException(new ElectrumException(msg));
                            }
                            else if (root.TryGetProperty("result", out var result))
                            {
                                tcs.TrySetResult(result.Clone());
                            }
                            else
                            {
                                tcs.TrySetResult(default);
                            }
                        }
                    }
                    else if (root.TryGetProperty("method", out var method))
                    {
                        // Notification
                        HandleNotification(method.GetString(), root);
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to parse Electrum response: {Line}", line);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Electrum connection lost");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in Electrum read loop");
        }
        finally
        {
            IsConnected = false;
            _ = Task.Run(() => ReconnectLoopAsync());
        }
    }

    private void HandleNotification(string method, JsonElement root)
    {
        var paramsArr = root.TryGetProperty("params", out var p) ? p : default;

        switch (method)
        {
            case "blockchain.headers.subscribe":
            {
                if (paramsArr.ValueKind == JsonValueKind.Array)
                {
                    var header = ParseHeaderNotification(paramsArr[0]);
                    OnNewBlock?.Invoke(header);
                }
                break;
            }
            case "blockchain.scripthash.subscribe":
            {
                if (paramsArr.ValueKind == JsonValueKind.Array && paramsArr.GetArrayLength() >= 2)
                {
                    var scripthash = paramsArr[0].GetString();
                    var status = paramsArr[1].ValueKind == JsonValueKind.Null ? null : paramsArr[1].GetString();
                    _subscribedScripthashes[scripthash] = status;
                    OnScripthashNotification?.Invoke(scripthash, status);
                }
                break;
            }
        }
    }

    private static ElectrumHeaderNotification ParseHeaderNotification(JsonElement element)
    {
        return new ElectrumHeaderNotification
        {
            Height = element.GetProperty("height").GetInt32(),
            Hex = element.TryGetProperty("hex", out var hex) ? hex.GetString() : null
        };
    }

    private async Task ReconnectLoopAsync()
    {
        var delay = TimeSpan.FromSeconds(1);
        var maxDelay = TimeSpan.FromSeconds(60);

        while (!IsConnected)
        {
            _logger.LogInformation("Attempting reconnect in {Delay}s...", delay.TotalSeconds);
            await Task.Delay(delay);

            try
            {
                await DisconnectAsync();
                await ConnectAsync(CancellationToken.None);

                // Re-subscribe
                if (_headersSubscribed)
                {
                    await HeadersSubscribeAsync(CancellationToken.None);
                }

                foreach (var scripthash in _subscribedScripthashes.Keys.ToArray())
                {
                    await ScripthashSubscribeAsync(scripthash, CancellationToken.None);
                }

                _logger.LogInformation("Reconnected to Electrum server");

                if (OnReconnected != null)
                    await OnReconnected();

                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Reconnection failed");
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, maxDelay.TotalSeconds));
            }
        }
    }

    // Keepalive
    internal async Task StartKeepaliveAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(60), ct);
                if (IsConnected)
                    await PingAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Keepalive ping failed");
            }
        }
    }
}

public class ElectrumException : Exception
{
    public ElectrumException(string message) : base(message) { }
}
