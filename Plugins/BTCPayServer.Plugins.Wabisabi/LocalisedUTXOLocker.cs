using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Payments.PayJoin;
using NBitcoin;

namespace BTCPayServer.Plugins.Wabisabi;

public class LocalisedUTXOLocker: IUTXOLocker
{
    private HashSet<OutPoint> _locked = new();
    public Task<bool> TryLock(OutPoint outpoint)
    {
        return Task.FromResult(_locked.Add(outpoint));
    }

    public Task<bool> TryUnlock(params OutPoint[] outPoints)
    {
        return Task.FromResult(_locked.RemoveWhere( outPoints.Contains) > 0);
    }

    public Task<bool> TryLockInputs(OutPoint[] outPoints)
    {
        throw new NotImplementedException();
    }

    public Task<HashSet<OutPoint>> FindLocks(OutPoint[] outpoints)
    {
        return Task.FromResult(_locked.Where(point => outpoints.Contains(point)).ToHashSet());
    }
}
