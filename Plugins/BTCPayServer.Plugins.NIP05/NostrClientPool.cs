using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using NNostr.Client;
using NNostr.Client.Protocols;

namespace BTCPayServer.Plugins.NIP05;

public class NostrClientPool
{
    private static readonly ConcurrentDictionary<string, NostrClientWrapper> _clientPool = new();

    private static readonly Timer _cleanupTimer;

    static NostrClientPool()
    {
        _cleanupTimer = new Timer(CleanupExpiredClients, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    public static (INostrClient, IDisposable) GetClient(string connstring)
    {
        var connParams = NIP47.ParseUri(new Uri(connstring));

        var clientWrapper = _clientPool.GetOrAdd(connstring.ToString(),
            k => new NostrClientWrapper(new CompositeNostrClient(connParams.relays)));

        clientWrapper.IncrementUsage();

        return (clientWrapper.Client, new UsageDisposable(clientWrapper));
    }
    public static async Task<(INostrClient, IDisposable)> GetClientAndConnect(string connstring, CancellationToken token)
    {
        var result = GetClient(connstring);
        
        await result.Item1.ConnectAndWaitUntilConnected(token, CancellationToken.None);
        
        return result;
    }

    public static void KillClient(string connstring)
    {
        if (_clientPool.TryRemove(connstring, out var clientWrapper))
        {
            clientWrapper.Dispose();
        }
    }

    private static void CleanupExpiredClients(object state)
    {
        foreach (var key in _clientPool.Keys)
        {
            if (_clientPool[key].IsExpired())
            {
                if (_clientPool.TryRemove(key, out var clientWrapper))
                {
                    clientWrapper.Dispose();
                }
            }
        }
    }

    private class UsageDisposable : IDisposable
    {
        private readonly NostrClientWrapper _clientWrapper;

        public UsageDisposable(NostrClientWrapper clientWrapper)
        {
            _clientWrapper = clientWrapper;
        }

        public void Dispose()
        {
            _clientWrapper.DecrementUsage();
        }
    }
}