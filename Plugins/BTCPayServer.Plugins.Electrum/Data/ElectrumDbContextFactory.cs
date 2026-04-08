using System;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Options;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace BTCPayServer.Plugins.Electrum.Data;

public class ElectrumDbContextFactory : BaseDbContextFactory<ElectrumDbContext>
{
    public ElectrumDbContextFactory(IOptions<DatabaseOptions> options)
        : base(options, "BTCPayServer.Plugins.Electrum")
    {
    }

    public override ElectrumDbContext CreateContext(Action<NpgsqlDbContextOptionsBuilder> npgsqlOptionsAction = null)
    {
        var builder = new DbContextOptionsBuilder<ElectrumDbContext>();
        builder.AddInterceptors(MigrationInterceptor.Instance);
        ConfigureBuilder(builder, npgsqlOptionsAction);
        return new ElectrumDbContext(builder.Options);
    }
}

public class DesignTimeElectrumDbContextFactory : IDesignTimeDbContextFactory<ElectrumDbContext>
{
    public ElectrumDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<ElectrumDbContext>();
        builder.UseNpgsql("Host=localhost;Database=btcpayserver");
        return new ElectrumDbContext(builder.Options);
    }
}
