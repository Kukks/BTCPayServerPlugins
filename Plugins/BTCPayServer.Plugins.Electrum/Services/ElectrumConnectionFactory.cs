using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Logging;
using BTCPayServer.Services;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BTCPayServer.Plugins.Electrum.Services;

/// <summary>
/// Replaces NBXplorerConnectionFactory. Reports Available = false so BTCPayWallet
/// uses the ExplorerClient path (which we intercept) instead of raw SQL queries
/// against NBXplorer's schema.
/// </summary>
public class ElectrumConnectionFactory : NBXplorerConnectionFactory
{
    public ElectrumConnectionFactory()
        : base(Microsoft.Extensions.Options.Options.Create(
            new BTCPayServer.Configuration.NBXplorerOptions()), new Logs())
    {
        Available = false;
    }
}
