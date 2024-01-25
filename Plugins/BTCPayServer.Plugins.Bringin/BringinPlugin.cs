using System;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Custodians;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.Bringin;

public class BringinPlugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    {
        new() {Identifier = nameof(BTCPayServer), Condition = ">=1.12.0"}
    };

    public override void Execute(IServiceCollection applicationBuilder)
    {
        applicationBuilder.AddStartupTask<CustodianEnablerTask>();
        applicationBuilder.AddSingleton<ICustodian, BringinCustodian>();
        applicationBuilder.AddSingleton<IFormComponentProvider, BringinApiKeyFormComponentProvider>();
        
    }

}