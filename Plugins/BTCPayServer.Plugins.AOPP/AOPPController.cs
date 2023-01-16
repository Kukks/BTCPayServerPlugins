using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NBitcoin;
using NBitcoin.Crypto;
using NBitcoin.Protocol;
using NBXplorer;
using Newtonsoft.Json;

namespace BTCPayServer.Plugins.AOPP
{
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    [Route("plugins/{storeId}/AOPP")]
    public class AOPPController : Controller
    {
        private readonly BTCPayServerClient _btcPayServerClient;
        private readonly AOPPService _AOPPService;

        public AOPPController(BTCPayServerClient btcPayServerClient, AOPPService AOPPService)
        {
            _btcPayServerClient = btcPayServerClient;
            _AOPPService = AOPPService;
        }

        [HttpGet("")]
        public async Task<IActionResult> UpdateAOPPSettings(string storeId)
        {
            var store = await _btcPayServerClient.GetStore(storeId);

            UpdateAOPPSettingsViewModel vm = new UpdateAOPPSettingsViewModel();
            vm.StoreName = store.Name;
            AOPPSettings AOPP = null;
            try
            {
                AOPP = await _AOPPService.GetAOPPForStore(storeId);
            }
            catch (Exception)
            {
                // ignored
            }

            SetExistingValues(AOPP, vm);
            return View(vm);
        }

        private void SetExistingValues(AOPPSettings existing, UpdateAOPPSettingsViewModel vm)
        {
            if (existing == null)
                return;
            vm.Enabled = existing.Enabled;
        }

        [HttpPost("")]
        public async Task<IActionResult> UpdateAOPPSettings(string storeId, UpdateAOPPSettingsViewModel vm,
            string command)
        {
            if (vm.Enabled)
            {
                if (!ModelState.IsValid)
                {
                    return View(vm);
                }
            }

            var AOPPSettings = new AOPPSettings()
            {
                Enabled = vm.Enabled,
            };

            switch (command)
            {
                case "save":
                    await _AOPPService.SetAOPPForStore(storeId, AOPPSettings);
                    TempData["SuccessMessage"] = "AOPP settings modified";
                    return RedirectToAction(nameof(UpdateAOPPSettings), new {storeId});

                default:
                    return View(vm);
            }
        }
        
        

        internal static String BITCOIN_SIGNED_MESSAGE_HEADER = "Bitcoin Signed Message:\n";
        internal static byte[] BITCOIN_SIGNED_MESSAGE_HEADER_BYTES = Encoding.UTF8.GetBytes(BITCOIN_SIGNED_MESSAGE_HEADER);

        //http://bitcoinj.googlecode.com/git-history/keychain/core/src/main/java/com/google/bitcoin/core/Utils.java
        private static byte[] FormatMessageForSigning(byte[] messageBytes)
        {
            MemoryStream ms = new MemoryStream();

            ms.WriteByte((byte)BITCOIN_SIGNED_MESSAGE_HEADER_BYTES.Length);
            ms.Write(BITCOIN_SIGNED_MESSAGE_HEADER_BYTES, 0, BITCOIN_SIGNED_MESSAGE_HEADER_BYTES.Length);

            VarInt size = new VarInt((ulong)messageBytes.Length);
            ms.Write(size.ToBytes(), 0, size.ToBytes().Length);
            ms.Write(messageBytes, 0, messageBytes.Length);
            return ms.ToArray();
        }

        public class AoppRequest
        {
            public Uri aopp { get; set; }   
        }
        
        [HttpPost]
        [Route("{invoiceId}")]
        [AllowAnonymous]
        public async Task<IActionResult> AOPPExecute(string storeId, string invoiceId,
            [FromBody] AoppRequest request ,
            [FromServices] IHttpClientFactory httpClientFactory,
            [FromServices] BTCPayNetworkProvider btcPayNetworkProvider,
            [FromServices] IExplorerClientProvider explorerClientProvider,
            [FromServices] BTCPayServerClient btcPayServerClient,
            [FromServices] IBTCPayServerClientFactory btcPayServerClientFactory)
        {
            try
            {
                var client = await btcPayServerClientFactory.Create(null, new[] {storeId});

                var invoice = await client.GetInvoice(storeId, invoiceId);
                if (invoice.Status is not InvoiceStatus.New)
                {
                    return NotFound();
                }

                var qs = HttpUtility.ParseQueryString(request.aopp.Query);
                var asset = qs.Get("asset");
                var network = btcPayNetworkProvider.GetNetwork<BTCPayNetwork>(asset);
                
                
                
                var invoicePaymentMethods = await client.GetInvoicePaymentMethods(storeId, invoiceId);

                var pm = invoicePaymentMethods.FirstOrDefault(model =>
                    model.PaymentMethod.Equals(asset, StringComparison.InvariantCultureIgnoreCase));
                if (pm is null)
                {
                    return NotFound();
                }
                var supported = (await client.GetStoreOnChainPaymentMethods(storeId))
                    .FirstOrDefault(settings => settings.CryptoCode.Equals(asset, StringComparison.InvariantCultureIgnoreCase));
;
                var msg = qs.Get("msg");
                var format = qs.Get("format");
                var callback = new Uri(qs.Get("callback")!, UriKind.Absolute);
                ScriptType? expectedType = null;
                switch (format)
                {
                    case "p2pkh":
                        expectedType = ScriptType.P2PKH;
                        break;
                    case "p2wpkh":
                        expectedType = ScriptType.P2WPKH;
                        break;
                    case "p2sh":
                        expectedType = ScriptType.P2SH;
                        break;
                    case "p2tr":
                        expectedType = ScriptType.Taproot;
                        break;
                    case "any":
                        break;
                }

                var address = BitcoinAddress.Create(pm.Destination, network.NBitcoinNetwork);
                if (expectedType is not null && !address.ScriptPubKey
                        .IsScriptType(expectedType.Value))
                {
                    return BadRequest();
                }
                var derivatonScheme =
                    network.NBXplorerNetwork.DerivationStrategyFactory.Parse(supported.DerivationScheme);
                var explorerClient = explorerClientProvider.GetExplorerClient(network);
                var extKeyStr = await explorerClient.GetMetadataAsync<string>(
                    derivatonScheme,
                    WellknownMetadataKeys.AccountHDKey);
                if (extKeyStr == null)
                {
                    return BadRequest();
                }

                var accountKey = ExtKey.Parse(extKeyStr, network.NBitcoinNetwork);

                var keyInfo = await  explorerClient.GetKeyInformationAsync(derivatonScheme, address.ScriptPubKey);
                var privateKey = accountKey.Derive(keyInfo.KeyPath).PrivateKey;

                var messageBytes = Encoding.UTF8.GetBytes(msg);
                byte[] data = FormatMessageForSigning(messageBytes);
                var hash = Hashes.DoubleSHA256(data);
                var sig =  Convert.ToBase64String(privateKey.SignCompact(hash, true).Signature);
                
                var response = new
                {
                    version = 0,
                    address = pm.Destination,
                    signature = sig
                };
                using var httpClient = httpClientFactory.CreateClient();
                await httpClient.PostAsync(callback,
                    new StringContent(JsonConvert.SerializeObject(response), Encoding.UTF8, "application/json"));
                return Ok();
            }
            catch (Exception e)
            {
                return BadRequest(new {ErrorMessage = e.Message});
            }
        }
    }
}
