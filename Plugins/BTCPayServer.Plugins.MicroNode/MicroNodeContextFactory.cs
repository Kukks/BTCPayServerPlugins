using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using Laraue.EfCoreTriggers.PostgreSql.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BTCPayServer.Plugins.MicroNode;

public class MicroNodeContextFactory : BaseDbContextFactory<MicroNodeContext>
{
    public MicroNodeContextFactory(IOptions<DatabaseOptions> options) : base(options, "BTCPayServer.Plugins.MicroNode")
    {
    }

    public override MicroNodeContext CreateContext()
    {
        var builder = new DbContextOptionsBuilder<MicroNodeContext>();
        ConfigureBuilder(builder);
        builder.UsePostgreSqlTriggers();
        
        return new MicroNodeContext(builder.Options);
    }
}