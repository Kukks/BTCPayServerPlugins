using Laraue.EfCoreTriggers.Common.Extensions;
using Laraue.EfCoreTriggers.PostgreSql.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BTCPayServer.Plugins.MicroNode;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<MicroNodeContext>
{
    public MicroNodeContext CreateDbContext(string[] args)
    {
        DbContextOptionsBuilder<MicroNodeContext> builder = new DbContextOptionsBuilder<MicroNodeContext>();

        // FIXME: Somehow the DateTimeOffset column types get messed up when not using Postgres
        // https://docs.microsoft.com/en-us/ef/core/managing-schemas/migrations/providers?tabs=dotnet-core-cli
        builder.UseNpgsql("User ID=postgres;Host=127.0.0.1;Port=39372;Database=designtimebtcpay");
        builder.UsePostgreSqlTriggers();
        return new MicroNodeContext(builder.Options);
    }
}

public class MicroNodeContext : DbContext
{
    public DbSet<MicroTransaction> MicroTransactions { get; set; }
    public DbSet<MicroAccount> MicroAccounts { get; set; }


    public MicroNodeContext()
    {
    }

    public MicroNodeContext(DbContextOptions<MicroNodeContext> builderOptions) : base(builderOptions)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema("BTCPayServer.Plugins.MicroNode");
        modelBuilder.Entity<MicroTransaction>()
            .HasKey(t => new {t.Id, t.AccountId});
        modelBuilder.Entity<MicroAccount>()
            .HasKey(t => t.Key);
        modelBuilder.Entity<MicroAccount>()
            .HasMany<MicroTransaction>(account => account.Transactions)
            .WithOne(transaction => transaction.Account)
            .HasForeignKey(transaction => transaction.AccountId);

        modelBuilder.Entity<MicroTransaction>()
            .HasOne<MicroTransaction>().WithMany(transaction => transaction.Dependents)
            .HasForeignKey(transaction => new {transaction.DependentId, transaction.AccountId})
            .HasPrincipalKey(transaction => new {transaction.Id, transaction.AccountId})
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<MicroTransaction>()
            .AfterInsert(trigger => trigger
                .Action(action =>
                {
                    action.Condition(@ref => @ref.New.Accounted)
                        .Update<MicroAccount>(
                            (tableRefs, microAccount) =>
                                microAccount.Key ==
                                tableRefs.New.AccountId, // Will be updated entities with matched condition
                            (tableRefs, oldBalance) => new MicroAccount
                                {Balance = oldBalance.Balance + tableRefs.New.Amount});
                })
                .Action(action =>
                {
                    action.Update<MicroAccount>(
                        (tableRefs, microAccount) =>
                            microAccount.Key ==
                            tableRefs.New.AccountId, // Will be updated entities with matched condition
                        (tableRefs, oldBalance) => new MicroAccount
                            {BalanceCheckpoint = oldBalance.BalanceCheckpoint + 1});
                })
                .Action(action =>
                {
                    action.Update<MicroTransaction>(
                        (tableRefs, tx) =>
                            tableRefs.New.Id == tx.DependentId && tx.AccountId ==
                            tableRefs.New.AccountId, // Will be updated entities with matched condition
                        (tableRefs, tx) => new MicroTransaction()
                            {Accounted = tableRefs.New.Accounted, Active = tableRefs.New.Active});
                }));

        modelBuilder.Entity<MicroTransaction>()
            .AfterDelete(trigger => trigger
                .Action(action =>
                {
                    action.Condition(@ref => @ref.Old.Accounted)
                        .Update<MicroAccount>(
                            (tableRefs, microAccount) =>
                                microAccount.Key ==
                                tableRefs.Old.AccountId, // Will be updated entities with matched condition
                            (tableRefs, oldBalance) => new MicroAccount
                                {Balance = oldBalance.Balance - tableRefs.Old.Amount});
                })
                .Action(action =>
                {
                    action.Update<MicroAccount>(
                        (tableRefs, microAccount) =>
                            microAccount.Key ==
                            tableRefs.Old.AccountId, // Will be updated entities with matched condition
                        (tableRefs, oldBalance) => new MicroAccount
                            {BalanceCheckpoint = oldBalance.BalanceCheckpoint + 1});
                }));

        modelBuilder.Entity<MicroTransaction>()
            .AfterUpdate(trigger => trigger
                .Action(action =>
                {
                    action.Update<MicroAccount>(
                        (tableRefs, microAccount) =>
                            microAccount.Key ==
                            tableRefs.Old.AccountId, // Will be updated entities with matched condition
                        (tableRefs, oldBalance) => new MicroAccount
                            {BalanceCheckpoint = oldBalance.BalanceCheckpoint + 1});
                })
                .Action(action =>
                {
                    action.Update<MicroTransaction>(
                        (tableRefs, tx) =>
                            tableRefs.Old.Id == tx.DependentId && tx.AccountId ==
                            tableRefs.New.AccountId, // Will be updated entities with matched condition
                        (tableRefs, tx) => new MicroTransaction()
                        {
                            Accounted = tableRefs.New.Accounted, Active = tableRefs.New.Active,
                            DependentId = tableRefs.New.Id
                        });
                })

                // Scenario 1: Transaction is newly accounted (not previously accounted)
                .Action(action =>
                {
                    action.Condition(@ref => @ref.New.Accounted && !@ref.Old.Accounted)
                        .Update<MicroAccount>(
                            (tableRefs, microAccount) => microAccount.Key == tableRefs.Old.AccountId,
                            (tableRefs, oldBalance) => new MicroAccount
                            {
                                Balance = oldBalance.Balance + tableRefs.New.Amount
                            });
                })

                // Scenario 2: Transaction was previously accounted and remains accounted (with a potential change in amount)
                .Action(action =>
                {
                    action.Condition(@ref =>
                            @ref.New.Accounted && @ref.Old.Accounted && @ref.Old.Amount != @ref.New.Amount)
                        .Update<MicroAccount>(
                            (tableRefs, microAccount) => microAccount.Key == tableRefs.Old.AccountId,
                            (tableRefs, oldBalance) => new MicroAccount
                            {
                                Balance = oldBalance.Balance - tableRefs.Old.Amount + tableRefs.New.Amount
                            });
                })

                // Scenario 3: Transaction is unaccounted (previously accounted)
                .Action(action =>
                {
                    action.Condition(@ref => !@ref.New.Accounted && @ref.Old.Accounted)
                        .Update<MicroAccount>(
                            (tableRefs, microAccount) => microAccount.Key == tableRefs.Old.AccountId,
                            (tableRefs, oldBalance) => new MicroAccount
                            {
                                Balance = oldBalance.Balance - tableRefs.Old.Amount
                            });
                })
            );

// Scenario 4: Transaction state remains unchanged (neither accounted nor unaccounted)
// Assuming no update is required in this case


        //unfortunately setting the balance this way is too complicated to generate the query
        // action.Condition(@ref => @ref.Old.Accounted != @ref.New.Accounted || @ref.Old.Amount != @ref.New.Amount)
        //     .Update<MicroAccount>(
        //         (tableRefs, microAccount) =>
        //             microAccount.Id ==
        //             tableRefs.Old.AccountId, // Will be updated entities with matched condition
        //
        //         // we update the balance with a few dimensions:
        //         // if the transaction was just accounted, we add the amount
        //         // if the transaction was already accounted, we remove the old amount and add the new amount
        //         // if the transaction was just unaccounted, we remove the amount
        //         (tableRefs, oldBalance) => new MicroAccount
        //         {
        //             Balance =
        //                 tableRefs.New.Accounted && !tableRefs.Old.Accounted
        //                     ?
        //                     oldBalance.Balance + tableRefs.New.Amount
        //                     :
        //                     tableRefs.New.Accounted && tableRefs.Old.Accounted
        //                         ? oldBalance.Balance - tableRefs.Old.Amount + tableRefs.New.Amount
        //                         :
        //                         !tableRefs.New.Accounted && tableRefs.Old.Accounted
        //                             ? oldBalance.Balance - tableRefs.Old.Amount
        //                             : oldBalance.Balance
        //         });
        //  })); // New values for matched entities.
    }
}