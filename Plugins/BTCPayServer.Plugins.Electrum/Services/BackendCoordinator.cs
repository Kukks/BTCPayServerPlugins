using System.Collections.Concurrent;
using System.Collections.Generic;

namespace BTCPayServer.Plugins.Electrum.Services;

public class BackendCoordinator
{
    private readonly ConcurrentDictionary<string, WalletBackend> _active = new();

    public WalletBackend GetActiveBackend(string walletId) =>
        _active.TryGetValue(walletId, out var b) ? b : WalletBackend.Electrum;

    public void SetActiveBackend(string walletId, WalletBackend backend) =>
        _active[walletId] = backend;

    public IReadOnlyDictionary<string, WalletBackend> Snapshot() =>
        new Dictionary<string, WalletBackend>(_active);
}
