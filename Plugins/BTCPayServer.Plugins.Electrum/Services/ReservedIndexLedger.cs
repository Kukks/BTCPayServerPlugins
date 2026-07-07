#nullable enable
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Electrum.Data;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.Electrum.Services;

public class ReservedIndexLedger
{
    private readonly ElectrumDbContextFactory _dbFactory;
    public ReservedIndexLedger(ElectrumDbContextFactory dbFactory) => _dbFactory = dbFactory;

    public static int Merge(int current, int observed) => observed > current ? observed : current;

    public async Task RecordReserveAsync(string walletId, bool isChange, int index, CancellationToken ct)
    {
        await using var ctx = _dbFactory.CreateContext();
        var w = await ctx.TrackedWallets.FindAsync(new object[] { walletId }, ct);
        if (w == null) return;
        if (isChange) w.ReservedChangeIndex = Merge(w.ReservedChangeIndex, index);
        else w.ReservedReceiveIndex = Merge(w.ReservedReceiveIndex, index);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<int> GetReservedAsync(string walletId, bool isChange, CancellationToken ct)
    {
        await using var ctx = _dbFactory.CreateContext();
        var w = await ctx.TrackedWallets.FindAsync(new object[] { walletId }, ct);
        return w == null ? -1 : (isChange ? w.ReservedChangeIndex : w.ReservedReceiveIndex);
    }
}
