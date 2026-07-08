#nullable enable
using System;
using System.Threading;

namespace BTCPayServer.Plugins.Electrum.Services;

/// <summary>
/// Shared, cached view of whether the real NBXplorer is currently reachable. Maintained by every
/// component that talks to real NBX (the coordinator's poll, the status monitor, and the routing
/// handler's proxy), so the handler can avoid routing Global reads / broadcasts to a dead NBX
/// (falling back to the Electrum engine instead) rather than only checking that a client object
/// exists. Fires <see cref="OnRecovered"/> once per unreachable→reachable transition so tracked
/// wallets can be re-scanned in NBX after an outage.
/// </summary>
public class NbxHealth
{
    // 1 = reachable, 0 = unreachable. Starts optimistic; the first real NBX interaction corrects it.
    private int _reachable = 1;

    public bool Reachable => Volatile.Read(ref _reachable) == 1;

    /// <summary>Raised (once per transition) when NBX goes from unreachable back to reachable.</summary>
    public event Action? OnRecovered;

    public void Record(bool reachable)
    {
        var previous = Interlocked.Exchange(ref _reachable, reachable ? 1 : 0);
        if (reachable && previous == 0)
            OnRecovered?.Invoke();
    }
}
