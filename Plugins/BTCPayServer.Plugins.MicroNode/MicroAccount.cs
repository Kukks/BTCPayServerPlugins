using System.Collections.Generic;

namespace BTCPayServer.Plugins.MicroNode;

public class MicroAccount
{
    public string Key { get; set; }
    public long Balance { get; set; }
    public long BalanceCheckpoint { get; set; }
    public string MasterStoreId { get; set; }
    
    public List<MicroTransaction> Transactions { get; set; } = new List<MicroTransaction>();
}