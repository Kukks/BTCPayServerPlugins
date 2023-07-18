using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;

namespace BTCPayServer.Plugins.SideShift;

public class PrismEditFilter : IPluginHookFilter
{
    public string Hook => "prism-edit-buttons";

    public Task<object> Execute(object args)
    {
        return  Task.FromResult<object>(( args??"") + "<button type='button' class=\"btn btn-primary\" data-bs-toggle=\"modal\" data-bs-target=\"#sideshiftModal\">Generate SideShift destination</button>");

    }
}