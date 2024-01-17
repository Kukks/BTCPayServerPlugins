using System.Collections.Generic;

namespace BTCPayServer.Plugins.MicroNode;

public class MicroTransaction
{
    public string Id { get; set; }
    public string AccountId { get; set; }

    public long Amount { get; set; }
    public bool Accounted { get; set; }


    public string Type { get; set; }
    public bool Active { get; set; }
    public MicroAccount Account { get; set; }
    public string? DependentId { get; set; }
    public MicroTransaction? Dependent { get; set; }

    public List<MicroTransaction> Dependents { get; set; }
}