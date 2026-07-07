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
        // A single atomic UPDATE ... CASE WHEN, so two concurrent writers can't race a
        // Find -> in-memory-max -> SaveChanges round trip and regress the high-water.
        await using var ctx = _dbFactory.CreateContext();
        if (isChange)
        {
            await ctx.TrackedWallets
                .Where(w => w.Id == walletId)
                .ExecuteUpdateAsync(s => s.SetProperty(
                    w => w.ReservedChangeIndex,
                    w => w.ReservedChangeIndex < index ? index : w.ReservedChangeIndex), ct);
        }
        else
        {
            await ctx.TrackedWallets
                .Where(w => w.Id == walletId)
                .ExecuteUpdateAsync(s => s.SetProperty(
                    w => w.ReservedReceiveIndex,
                    w => w.ReservedReceiveIndex < index ? index : w.ReservedReceiveIndex), ct);
        }
    }

    public async Task<int> GetReservedAsync(string walletId, bool isChange, CancellationToken ct)
    {
        await using var ctx = _dbFactory.CreateContext();
        var w = await ctx.TrackedWallets.FindAsync(new object[] { walletId }, ct);
        return w == null ? -1 : (isChange ? w.ReservedChangeIndex : w.ReservedReceiveIndex);
    }
}
