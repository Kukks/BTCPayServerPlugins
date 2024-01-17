using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.MicroNode;

public class MicroNodeStartupTask : IStartupTask
{
    private readonly MicroNodeContextFactory _microNodeContextFactory;
    private readonly ILogger<MicroNodeStartupTask> _logger;

    public MicroNodeStartupTask(MicroNodeContextFactory microNodeContextFactory, ILogger<MicroNodeStartupTask> logger)
    {
        _microNodeContextFactory = microNodeContextFactory;
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken = new())
    {
        _logger.LogInformation("Migrating MicroNode database");
        await using var ctx = _microNodeContextFactory.CreateContext();
        var pendingMigrations = await ctx.Database.GetPendingMigrationsAsync(cancellationToken: cancellationToken);
        if (pendingMigrations.Any())
        {
            _logger.LogInformation("Applying {Count} migrations", pendingMigrations.Count());
            await ctx.Database.MigrateAsync(cancellationToken: cancellationToken);
        }
        else
        {
            _logger.LogInformation("No migrations to apply");
        }

    }
}