using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.Electrum.Data;

public class ElectrumDbContext : DbContext
{
    public ElectrumDbContext(DbContextOptions<ElectrumDbContext> options) : base(options) { }

    public DbSet<TrackedWallet> TrackedWallets { get; set; }
    public DbSet<TrackedAddress> TrackedAddresses { get; set; }
    public DbSet<TrackedUtxo> Utxos { get; set; }
    public DbSet<TrackedTransaction> Transactions { get; set; }
    public DbSet<SyncState> SyncStates { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("electrum");

        modelBuilder.Entity<TrackedWallet>(b =>
        {
            b.HasKey(e => e.Id);
            b.ToTable("tracked_wallets");
        });

        modelBuilder.Entity<TrackedAddress>(b =>
        {
            b.HasKey(e => e.Scripthash);
            b.ToTable("tracked_addresses");
            b.HasOne(e => e.Wallet).WithMany().HasForeignKey(e => e.WalletId);
            b.HasIndex(e => e.WalletId);
        });

        modelBuilder.Entity<TrackedUtxo>(b =>
        {
            b.HasKey(e => e.Outpoint);
            b.ToTable("utxos");
            b.HasOne(e => e.Wallet).WithMany().HasForeignKey(e => e.WalletId);
            b.HasOne(e => e.TrackedAddress).WithMany().HasForeignKey(e => e.Scripthash);
            b.HasIndex(e => e.WalletId).HasFilter("NOT \"IsSpent\"");
            b.HasIndex(e => e.Scripthash);
        });

        modelBuilder.Entity<TrackedTransaction>(b =>
        {
            b.HasKey(e => new { e.Txid, e.WalletId });
            b.ToTable("transactions");
            b.HasOne(e => e.Wallet).WithMany().HasForeignKey(e => e.WalletId);
            b.HasIndex(e => e.WalletId);
        });

        modelBuilder.Entity<SyncState>(b =>
        {
            b.HasKey(e => e.Key);
            b.ToTable("sync_state");
        });
    }
}
