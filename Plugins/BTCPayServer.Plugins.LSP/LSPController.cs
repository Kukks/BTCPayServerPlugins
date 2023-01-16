using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Lightning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using NBitcoin;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.LSP
{
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    [Route("plugins/{storeId}/LSP")]
    public class LSPController : Controller
    {
        [AllowAnonymous]
        [HttpGet("")]
        public async Task<IActionResult> View(string storeId)
        {
            var config = await _LSPService.GetLSPForStore(storeId);
            try
            {
                if (config?.Enabled is true)
                {
                    return View(new LSPViewModel() {Settings = config});
                }
            }
            catch (Exception e)
            {
                // ignored
            }

            return NotFound();
        }


        [AllowAnonymous]
        [HttpPost("")]
        public async Task<IActionResult> Purchase(string storeId, string email, uint inbound, bool privateChannel)
        {
            var config = await _LSPService.GetLSPForStore(storeId);
            try
            {
                if (config?.Enabled is not true || string.IsNullOrEmpty(email) || inbound < config.Minimum ||
                    inbound > config.Maximum)
                {
                    return RedirectToAction("View", new {storeId});
                }

                var price = Math.Ceiling((config.FeePerSat == 0 ? 0 : (config.FeePerSat * inbound)) + config.BaseFee);
                var btcpayClient = await CreateClient(storeId);
                var redirectUrl = Request.GetAbsoluteUri(Url.Action("Connect",
                    "LSP", new {storeId, invoiceId = "kukkskukkskukks"}));
                redirectUrl = redirectUrl.Replace("kukkskukkskukks", "{InvoiceId}");
                var inv = await btcpayClient.CreateInvoice(storeId,
                    new CreateInvoiceRequest()
                    {
                        Amount = price,
                        Currency = "sats",
                        Type = InvoiceType.Standard,
                        AdditionalSearchTerms = new[] {"LSP"},
                        Checkout =
                        {
                            RequiresRefundEmail = true,
                            RedirectAutomatically = price > 0,
                            RedirectURL = redirectUrl,
                        },
                        Metadata = JObject.FromObject(new
                        {
                            buyerEmail = email, 
                            privateChannel, 
                            inbound, 
                            config.BaseFee,
                            config.FeePerSat,
                            orderId = "LSP"
                        })
                    });

                while (inv.Amount == 0 && inv.Status == InvoiceStatus.New)
                {
                    if (inv.Status == InvoiceStatus.New)
                        inv = await btcpayClient.GetInvoice(inv.StoreId, inv.Id);
                }

                if (inv.Status == InvoiceStatus.Settled)
                    return RedirectToAction("Connect", new {storeId, invoiceId = inv.Id});
                return Redirect(inv.CheckoutLink);
            }
            catch (Exception e)
            {
            }

            return RedirectToAction("View", new {storeId});
        }


        [AllowAnonymous]
        [HttpGet("connect")]
        public async Task<IActionResult> Connect(string storeId, string invoiceId)
        {
            var btcpayClient = await CreateClient(storeId);
            try
            {
                var config = await _LSPService.GetLSPForStore(storeId);
                var result = new LSPConnectPage() {InvoiceId = invoiceId, Settings = config};
                var invoice = await btcpayClient.GetInvoice(storeId, invoiceId);
                result.Status = invoice.Status;
                if (invoice.Status != InvoiceStatus.Settled) return View(result);
                if (invoice.Metadata.TryGetValue("lsp-channel-complete", out _))
                {
                    return Redirect(invoice.CheckoutLink);
                }

                
                result.Invoice = invoice;
                result.LNURL = LNURL.LNURL.EncodeUri(new Uri(Request.GetAbsoluteUri(Url.Action(
                    "LNURLChannelRequest",
                    "LSP", new {storeId, invoiceId}))), "channelRequest", true).ToString();

                return View(result);
            }
            catch (Exception e)
            {
                return NotFound();
            }
        }

        private async Task<BTCPayServerClient> CreateClient(string storeId)
        {
            return await _btcPayServerClientFactory.Create(null, new[] {storeId},
                new DefaultHttpContext()
                {
                    Request =
                    {
                        Scheme = "https", Host = Request.Host, Path = Request.Path, PathBase = Request.PathBase
                    }
                });
        }

        public class LSPConnectPage
        {
            public string LNURL;
            public string InvoiceId { get; set; }
            public InvoiceStatus Status { get; set; }
            public LSPSettings Settings { get; set; }
            public InvoiceData Invoice { get; set; }
        }
        
        private readonly LSPService _LSPService;
        private readonly IBTCPayServerClientFactory _btcPayServerClientFactory;

        public LSPController(IHttpClientFactory httpClientFactory,
            LSPService LSPService,
            IBTCPayServerClientFactory btcPayServerClientFactory)
        {
            _LSPService = LSPService;
            _btcPayServerClientFactory = btcPayServerClientFactory;
        }

        [HttpGet("update")]
        public async Task<IActionResult> UpdateLSPSettings(string storeId)
        {
            LSPSettings vm = null;
            try
            {
                vm = await _LSPService.GetLSPForStore(storeId);
            }
            catch (Exception)
            {
                // ignored
            }

            vm ??= new();

            return View(vm);
        }

        [HttpPost("update")]
        public async Task<IActionResult> UpdateLSPSettings(string storeId,
            LSPSettings vm,
            string command)
        {
            if (!ModelState.IsValid)
            {
                return View(vm);
            }


            switch (command?.ToLowerInvariant())
            {
                case "save":
                    await _LSPService.SetLSPForStore(storeId, vm);
                    TempData["SuccessMessage"] = "LSP settings modified";
                    return RedirectToAction(nameof(UpdateLSPSettings), new {storeId});

                default:
                    return View(vm);
            }
        }
        

        [AllowAnonymous]
        [HttpGet("lnurlc-callback")]
        public async Task<IActionResult> LNURLChannelRequestCallback(string storeId, string k1, string remoteId)
        {
            if (!NodeInfo.TryParse(remoteId, out var remoteNode))
            {
                return BadRequest();
            }
            var btcPayClient = await CreateClient(storeId);
            var invoice = await btcPayClient.GetInvoice(storeId, k1);
            if (invoice?.Status != InvoiceStatus.Settled || invoice.Metadata.TryGetValue("lsp-channel-complete", out _))
            {
                return NotFound();
            }
            var settings = await _LSPService.GetLSPForStore(storeId);
            if (settings?.Enabled is not true)
            {
                return BadRequest();
            }
            if (!invoice.Metadata.TryGetValue("posData", out var posData))
            {
                posData = JToken.Parse("{}");
            }

            var inbound = invoice.Metadata["inbound"].Value<long>();
            try
            {
                await btcPayClient.ConnectToLightningNode(storeId, "BTC", new ConnectToNodeRequest(remoteNode));
                posData["LSP"] = JToken.FromObject(new Dictionary<string,object>());
                posData["LSP"]["Remote Node"] = remoteId;
                await btcPayClient.OpenLightningChannel(storeId, "BTC", new OpenLightningChannelRequest()
                {
                    ChannelAmount = new Money(inbound, MoneyUnit.Satoshi),
                    
                    NodeURI = remoteNode
                });
                posData["LSP"]["Channel Status"] = "Opened";
                invoice.Metadata["posData"] = posData;
                invoice.Metadata["lsp-channel-complete"] = true;
                await btcPayClient.UpdateInvoice(storeId, invoice.Id,
                    new UpdateInvoiceRequest() {Metadata = invoice.Metadata});
                return Ok(new LNURL.LNUrlStatusResponse()
                {
                    Status = "OK"
                });
            }
            catch (Exception e)
            {
                posData["Error"] =
                    $"Channel could not be created. You should refund customer.{Environment.NewLine}{e.Message}";
                invoice.Metadata["posData"] = posData;
                await btcPayClient.UpdateInvoice(storeId, invoice.Id,
                    new UpdateInvoiceRequest() {Metadata = invoice.Metadata});
                return Ok(new LNURL.LNUrlStatusResponse()
                {
                    Status = "ERROR", Reason = $"Channel could not be created to {remoteId}"
                });
                
            }
        }

        [AllowAnonymous]
        [HttpGet("{invoiceId}/lnurlc")]
        public async Task<IActionResult> LNURLChannelRequest(string storeId, string invoiceId, string nodeUri)
        {
            var btcPayClient = await CreateClient(storeId);
            var invoice = await btcPayClient.GetInvoice(storeId, invoiceId);
            if (invoice?.Status != InvoiceStatus.Settled || invoice.Metadata.TryGetValue("lsp-channel-complete", out _))
            {
                return NotFound();
            }
            var settings = await _LSPService.GetLSPForStore(storeId);
            if (settings?.Enabled is not true)
            {
                return BadRequest();
            }
            return Ok(new LNURL.LNURLChannelRequest()
            {
                Tag = "channelRequest",
                K1 = invoiceId,
                Callback = new Uri(Request.GetAbsoluteUri(Url.Action("LNURLChannelRequestCallback",
                    "LSP", new {storeId}))),
                Uri = nodeUri is null
                    ? (await btcPayClient.GetLightningNodeInfo(storeId, "BTC")).NodeURIs
                    .OrderBy(nodeInfo => nodeInfo.IsTor).First()
                    : NodeInfo.Parse(nodeUri)
            });
        }
    }
}
