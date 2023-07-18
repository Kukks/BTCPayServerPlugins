using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.SideShift;

public class PrismDestinationValidate : IPluginHookFilter
{
    public string Hook => "prism-destination-validate";
    public async Task<object> Execute(object args)
    {
        if (args is not string args1 || !args1.StartsWith("sideshift:")) return args;
        var json  = JObject.Parse(args1.Substring("sideshift:".Length)).ToObject<PrismSideshiftDestination>();
        return json.Valid();
    }

}