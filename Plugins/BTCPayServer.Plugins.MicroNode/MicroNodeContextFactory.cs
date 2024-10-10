using System;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace BTCPayServer.Plugins.MicroNode;

public class MicroNodeContextFactory : BaseDbContextFactory<MicroNodeContext>
{
    private readonly ILoggerFactory _loggerFactory;

    public MicroNodeContextFactory(IOptions<DatabaseOptions> options,  ILoggerFactory loggerFactory) : base(options, "BTCPayServer.Plugins.MicroNode")
    {
        _loggerFactory = loggerFactory;
    }

    public override MicroNodeContext CreateContext(Action<NpgsqlDbContextOptionsBuilder> npgsqlOptionsAction = null)
    {
        var builder = new DbContextOptionsBuilder<MicroNodeContext>();
        builder.UseLoggerFactory(_loggerFactory);
        builder.AddInterceptors(MigrationInterceptor.Instance);
        ConfigureBuilder(builder, npgsqlOptionsAction);
        return new MicroNodeContext(builder.Options);
    }
}