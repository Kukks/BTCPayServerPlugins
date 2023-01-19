using System;
using System.Collections.Generic;
using BTCPayServer.Models;

namespace BTCPayServer.Plugins.Wabisabi;

public class CoinjoinsViewModel : BasePagingViewModel
{
    public List<BTCPayWallet.CoinjoinData> Coinjoins { get; set; } = new();
    public override int CurrentPageCount => Coinjoins.Count;
}
