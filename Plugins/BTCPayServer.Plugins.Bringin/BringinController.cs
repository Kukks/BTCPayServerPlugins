using System;
using System.Net.Http;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.Bringin;

[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Route("plugins/{storeId}/Bringin")]
public class BringinController : Controller
{
    private readonly BringinService _bringinService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly BTCPayNetworkProvider _btcPayNetworkProvider;

    public BringinController(BringinService bringinService, IHttpClientFactory httpClientFactory, BTCPayNetworkProvider btcPayNetworkProvider)
    {
        _bringinService = bringinService;
        _httpClientFactory = httpClientFactory;
        _btcPayNetworkProvider = btcPayNetworkProvider;
    }
    
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    [HttpGet("onboard")]
    public async Task<IActionResult> Onboard(string storeId)
    {
        var vm = await _bringinService.Update(storeId);
        
        var callbackUri = Url.Action("Callback", "Bringin", new
        {
            code = vm.Code,
            storeId
        }, Request.Scheme);
        
        var network = _btcPayNetworkProvider.GetNetwork<BTCPayNetwork>("BTC").NBitcoinNetwork;
        var httpClient = BringinClient.CreateClient(network,_httpClientFactory);
        var onboardUri = await BringinClient.OnboardUri(httpClient, new Uri(callbackUri), network);
        return Redirect(onboardUri.ToString());
    }

    [Authorize(Policy = Policies.CanViewStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    [HttpGet("")]
    public async Task<IActionResult> Edit()
    {
        return View();
    }
    
    [HttpPost("callback")]
    [HttpGet("callback")]
    [AllowAnonymous]
    public async Task<IActionResult> Callback(string storeId, string code, [FromBody]BringinVerificationUpdate content)
    {
        var vm = await _bringinService.Update(storeId);
        if(vm.Code != code) return BadRequest();
        if(content.verificationStatus != "APPROVED") return BadRequest("Verification not approved");

        if (string.IsNullOrEmpty(vm.ApiKey) && !string.IsNullOrEmpty(content.apikey))
        {
            vm.ApiKey = content.apikey;
            await _bringinService.Update(storeId, vm);
           
        }
        return Ok();
    }
    
    public class BringinVerificationUpdate
    {
        public string userId { get; set; }
        public string apikey { get; set; }
        public string verificationStatus { get; set; }
    }
    
}