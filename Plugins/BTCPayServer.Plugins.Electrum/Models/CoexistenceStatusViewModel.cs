using System.Collections.Generic;
using BTCPayServer.Plugins.Electrum.Services;

namespace BTCPayServer.Plugins.Electrum.Models;

// Read-only view model for the Electrum/NBX coexistence panel on the Electrum settings page
// (P4 Task 3). Built fresh per-request in UIElectrumController; nothing here is persisted or
// bound from a form.
public class CoexistenceStatusViewModel
{
    public bool EffectiveReady { get; set; }
    public string ElectrumConnectedServer { get; set; }
    public string ElectrumConfiguredServer { get; set; }
    public string ElectrumServerVersion { get; set; }
    public int ElectrumTipHeight { get; set; }
    public List<WalletCoexistenceRow> Wallets { get; set; } = new();
}

public class WalletCoexistenceRow
{
    public string WalletId { get; set; }
    public WalletBackend Active { get; set; }
    public int ConsecutiveAgree { get; set; }
    public int ReservedReceiveIndex { get; set; }
    public int ReservedChangeIndex { get; set; }
}
