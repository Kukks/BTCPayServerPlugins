using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.SideShift;

public class SideShiftAvailableCoin
{
    public string coin { get; set; }
    public string[] networks { get; set; }
    public string name { get; set; }
    public bool hasMemo { get; set; }
    public JToken fixedOnly { get; set; }
    public JToken variableOnly { get; set; }
    public JToken settleOffline { get; set; }
}