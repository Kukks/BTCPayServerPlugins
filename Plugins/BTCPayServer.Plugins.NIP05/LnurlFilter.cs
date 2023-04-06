using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Services;
using LNURL;

namespace BTCPayServer.Plugins.NIP05;

public class LnurlFilter : PluginHookFilter<LNURLPayRequest>
{
    private readonly Nip5Controller _nip5Controller;
    private readonly LightningAddressService _lightningAddressService;
    public override string Hook => "modify-lnurlp-request";

    public LnurlFilter(Nip5Controller nip5Controller, LightningAddressService lightningAddressService)
    {
        _nip5Controller = nip5Controller;
        _lightningAddressService = lightningAddressService;
    }

    public override async Task<LNURLPayRequest> Execute(LNURLPayRequest arg)
    {
        var name = arg.ParsedMetadata.FirstOrDefault(pair => pair.Key == "text/identifier").Value
            ?.ToLowerInvariant().Split("@")[0];
        if (string.IsNullOrEmpty(name))
        {
            return arg;
        }

        var lnAddress = await _lightningAddressService.ResolveByAddress(name);
        if (lnAddress is null)
        {
            return arg;
        }

        var nip5 = await _nip5Controller.Get(name);
        if (nip5.storeId != lnAddress.StoreDataId)
        {
            return arg;
        }

        arg.NostrPubkey = nip5.settings.PubKey;
        arg.AllowsNostr = true;
        return arg;
    }
}