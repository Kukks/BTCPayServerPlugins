using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using AuthenticationSchemes = BTCPayServer.Abstractions.Constants.AuthenticationSchemes;
using IConfiguration = Microsoft.Extensions.Configuration.IConfiguration;

namespace BTCPayServer.Plugins.TicketTailor
{
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    [Route("plugins/{storeId}/TicketTailor")]
    public class TicketTailorController : Controller
    {
        [AllowAnonymous]
        [HttpGet("")]
        public async Task<IActionResult> View(string storeId)
        {
            var config = await _ticketTailorService.GetTicketTailorForStore(storeId);
            try
            {
                if (config?.ApiKey is not null && config?.EventId is not null)
                {
                    var client = new TicketTailorClient(_httpClientFactory, config.ApiKey);
                    var evt = await client.GetEvent(config.EventId);
                    if (evt is null)
                    {
                        return NotFound();
                    }

                    return View(new TicketTailorViewModel() {Event = evt, Settings = config});
                }
            }
            catch (Exception e)
            {
            }

            return NotFound();
        }


        [AllowAnonymous]
        [HttpPost("")]
        public async Task<IActionResult> Purchase(string storeId, string ticketTypeId, string firstName,
            string lastName, string email)
        {
            var config = await _ticketTailorService.GetTicketTailorForStore(storeId);
            try
            {
                if (config?.ApiKey is not null && config?.EventId is not null)
                {
                    var client = new TicketTailorClient(_httpClientFactory, config.ApiKey);
                    var evt = await client.GetEvent(config.EventId);
                    if (evt is null || (!config.BypassAvailabilityCheck && (evt.Unavailable == "true" || evt.TicketsAvailable == "false")))
                    {
                        return NotFound();
                    }

                    var ticketType = evt.TicketTypes.FirstOrDefault(type => type.Id == ticketTypeId);
                    var specificTicket =
                        config.SpecificTickets?.SingleOrDefault(ticket => ticketType?.Id == ticket.TicketTypeId);
                    if (ticketType is not null && specificTicket is not null)
                    {
                        ticketType.Price = specificTicket.Price.GetValueOrDefault(ticketType.Price);
                    }

                    if (ticketType is null || (specificTicket is null && ticketType.Status != "on_sale") ||
                        ticketType.Quantity <= 0)
                    {
                        return NotFound();
                    }

                    var btcpayClient = await CreateClient(storeId);
                    var redirectUrl = Request.GetAbsoluteUri(Url.Action("Receipt",
                        "TicketTailor", new {storeId, invoiceId = "kukkskukkskukks"}));
                    redirectUrl = redirectUrl.Replace("kukkskukkskukks", "{InvoiceId}");
                    var inv = await btcpayClient.CreateInvoice(storeId,
                        new CreateInvoiceRequest()
                        {
                            Amount = ticketType.Price,
                            Currency = evt.Currency,
                            Type = InvoiceType.Standard,
                            AdditionalSearchTerms = new[] {"tickettailor", ticketTypeId, evt.Id},
                            Checkout =
                            {
                                RequiresRefundEmail = true,
                                RedirectAutomatically = ticketType.Price > 0,
                                RedirectURL = redirectUrl,
                            },
                            Receipt = new InvoiceDataBase.ReceiptOptions()
                            {
                                Enabled = false
                            },
                            Metadata = JObject.FromObject(new
                            {
                                buyerName = $"{firstName} {lastName}", buyerEmail = email, ticketTypeId,orderId="tickettailor"
                            })
                        });

                    while (inv.Amount == 0 && inv.Status == InvoiceStatus.New)
                    {
                        if (inv.Status == InvoiceStatus.New)
                            inv = await btcpayClient.GetInvoice(inv.StoreId, inv.Id);
                    }

                    if (inv.Status == InvoiceStatus.Settled)
                        return RedirectToAction("Receipt", new {storeId, invoiceId = inv.Id});
                    return Redirect(inv.CheckoutLink);
                }
            }
            catch (Exception e)
            {
            }

            return RedirectToAction("View", new {storeId});
        }


        [AllowAnonymous]
        [HttpGet("receipt")]
        public async Task<IActionResult> Receipt(string storeId, string invoiceId)
        {
            var btcpayClient = await CreateClient(storeId);
            try
            {
                var result = new TicketReceiptPage() {InvoiceId = invoiceId};
                var invoice = await btcpayClient.GetInvoice(storeId, invoiceId);
                result.Status = invoice.Status;
                if (invoice.Status == InvoiceStatus.Settled)
                {
                    
                    if (invoice.Metadata.TryGetValue("ticketId", out var ticketId))
                    {
                        await SetTicketTailorTicketResult(storeId, result, ticketId);
                    }
                    else
                    {
                      invoice =  await _ticketTailorService.Handle(invoice.Id, storeId, Request.GetAbsoluteRootUri());
                      if (invoice.Metadata.TryGetValue("ticketId", out ticketId))
                      {
                          await SetTicketTailorTicketResult(storeId, result, ticketId);
                      }
                    }
                }

                return View(result);
            }
            catch (Exception e)
            {
                return NotFound();
            }
        }

        private async Task SetTicketTailorTicketResult(string storeId, TicketReceiptPage result, JToken ticketId)
        {
            var settings = await _ticketTailorService.GetTicketTailorForStore(storeId);
            var client = new TicketTailorClient(_httpClientFactory, settings.ApiKey);
            result.Ticket = await client.GetTicket(ticketId.ToString());
            var evt = await client.GetEvent(settings.EventId);
            result.Event = evt;
            result.TicketType =
                evt.TicketTypes.FirstOrDefault(type => type.Id == result.Ticket.TicketTypeId);
            result.Settings = settings;
        }

        private async Task<BTCPayServerClient> CreateClient(string storeId)
        {
            return await _btcPayServerClientFactory.Create(null, new[] {storeId}, new DefaultHttpContext()
            {
                Request =
                {
                    Scheme = "https",
                    Host = Request.Host,
                    Path = Request.Path,
                    PathBase = Request.PathBase
                }
            });
        }
        
        public class TicketReceiptPage
        {
            public string InvoiceId { get; set; }
            public InvoiceStatus Status { get; set; }
            public TicketTailorClient.IssuedTicket Ticket { get; set; }
            public TicketTailorClient.Event Event { get; set; }
            public TicketTailorClient.TicketType TicketType { get; set; }
            public TicketTailorSettings Settings { get; set; }
        }


        private readonly IHttpClientFactory _httpClientFactory;
        private readonly TicketTailorService _ticketTailorService;
        private readonly IBTCPayServerClientFactory _btcPayServerClientFactory;
        private readonly IConfiguration _configuration;
        private readonly LinkGenerator _linkGenerator;

        public TicketTailorController(IHttpClientFactory httpClientFactory,
            TicketTailorService ticketTailorService,
            IBTCPayServerClientFactory btcPayServerClientFactory,
            IConfiguration configuration,
            LinkGenerator linkGenerator )
        {
            
            _httpClientFactory = httpClientFactory;
            _ticketTailorService = ticketTailorService;
            _btcPayServerClientFactory = btcPayServerClientFactory;
            _configuration = configuration;
            _linkGenerator = linkGenerator;
        }

        [HttpGet("update")]
        public async Task<IActionResult> UpdateTicketTailorSettings(string storeId)
        {
            UpdateTicketTailorSettingsViewModel vm = new();
            TicketTailorSettings TicketTailor = null;
            try
            {
                TicketTailor = await _ticketTailorService.GetTicketTailorForStore(storeId);
                if (TicketTailor is not null)
                {
                    vm.ApiKey = TicketTailor.ApiKey;
                    vm.EventId = TicketTailor.EventId;
                    vm.ShowDescription = TicketTailor.ShowDescription;
                    vm.BypassAvailabilityCheck = TicketTailor.BypassAvailabilityCheck;
                    vm.CustomCSS = TicketTailor.CustomCSS;
                    vm.SpecificTickets = TicketTailor.SpecificTickets;
                }
            }
            catch (Exception)
            {
                // ignored
            }

            vm = await SetValues(vm);

            return View(vm);
        }

        private async Task<UpdateTicketTailorSettingsViewModel> SetValues(UpdateTicketTailorSettingsViewModel vm)
        {
            try
            {
                if (!string.IsNullOrEmpty(vm.ApiKey))
                {
                    TicketTailorClient.Event? evt = null;
                    var client = new TicketTailorClient(_httpClientFactory, vm.ApiKey);
                    var evts = await client.GetEvents();
                    if (vm.EventId is not null && evts.All(e => e.Id != vm.EventId))
                    {
                        vm.EventId = null;
                        vm.SpecificTickets = new List<SpecificTicket>();
                    }
                    else
                    {
                        if (vm.EventId is null)
                        {
                            vm.SpecificTickets = new List<SpecificTicket>();
                        }
                        else
                        {
                            evt = evts.SingleOrDefault(e => e.Id == vm.EventId);
                        }
                    }

                    evts = evts.Prepend(new TicketTailorClient.Event() {Id = null, Title = "Select an event"})
                        .ToArray();
                    vm.Events = new SelectList(evts, nameof(TicketTailorClient.Event.Id),
                        nameof(TicketTailorClient.Event.Title), vm.EventId);

                    if (vm.EventId is not null)
                    {
                        vm.TicketTypes = evt?.TicketTypes?.ToArray();
                    }
                }
            }
            catch (Exception e)
            {
                ModelState.AddModelError(nameof(vm.ApiKey), "Api key did not work.");
            }

            return vm;
        }


        [HttpPost("update")]
        public async Task<IActionResult> UpdateTicketTailorSettings(string storeId,
            UpdateTicketTailorSettingsViewModel vm,
            string command,
            [FromServices] BTCPayServerClient btcPayServerClient)
        {
            vm = await SetValues(vm);

            if (command == "add-specific-ticket" && vm.NewSpecificTicket is not null)
            {
                vm.SpecificTickets ??= new List<SpecificTicket>();
                vm.SpecificTickets.Add(new() {TicketTypeId = vm.NewSpecificTicket});
                vm.NewSpecificTicket = null;
                return View(vm);
            }

            if (command.StartsWith("remove-specific-ticket"))
            {
                var i = int.Parse(command.Substring(command.IndexOf(":", StringComparison.InvariantCultureIgnoreCase) +
                                                    1));
                vm.SpecificTickets.RemoveAt(i);
                return View(vm);
            }

            if (!ModelState.IsValid)
            {
                return View(vm);
            }
            ModelState.Clear();
            var settings = new TicketTailorSettings()
            {
                ApiKey = vm.ApiKey,
                EventId = vm.EventId,
                ShowDescription = vm.ShowDescription,
                CustomCSS = vm.CustomCSS,
                SpecificTickets = vm.SpecificTickets,
                BypassAvailabilityCheck = vm.BypassAvailabilityCheck
            };
            
            var bindAddress = _configuration.GetValue("bind", IPAddress.Loopback);
            if (Equals(bindAddress, IPAddress.Any))
            {
                bindAddress = IPAddress.Loopback;
            } 
            if (Equals(bindAddress, IPAddress.IPv6Any))
            {
                bindAddress = IPAddress.IPv6Loopback;
            }
            int bindPort = _configuration.GetValue<int>("port", 443);
            
            string rootPath = _configuration.GetValue<string>("rootpath", "/");
            string attempt1 = null;
            if (bindAddress is not null)
            {
                attempt1 = _linkGenerator.GetUriByAction("Callback",
                    "TicketTailor", new {storeId,test= true}, "https", new HostString(bindAddress?.ToString(), bindPort),
                    new PathString(rootPath));
            }
         
            var attempt2 = Request.GetAbsoluteUri(Url.Action("Callback",
                "TicketTailor", new {storeId, test= true}));


            HttpRequestMessage Create(string uri)
            {
                return new HttpRequestMessage(HttpMethod.Post, uri)
                {
                    Content = new StringContent(
                        JsonConvert.SerializeObject(new WebhookInvoiceEvent(WebhookEventType.InvoiceSettled))
                        ,Encoding.UTF8, 
                        "application/json"),
                    
                };
            }

            HttpClient CreateClient(string uri)
            {
                var link = new Uri(uri);
                if (link.IsLoopback)
                {
                    return _httpClientFactory.CreateClient("greenfield-webhook.loopback");
                    
                }else if (link.Host.EndsWith("onion"))
                {
                    return _httpClientFactory.CreateClient("greenfield-webhook.onion");
                }
                else
                {
                   return  _httpClientFactory.CreateClient("greenfield-webhook.clearnet");
                }
            }
            

            HttpResponseMessage result = null;
            if (attempt1 is not null)
            {

                try
                {

                    result = await CreateClient(attempt1).SendAsync(Create(attempt1), CancellationToken.None);
                }
                catch (Exception e)
                {

                }
            }

            string webhookUrl = null;
           if (result?.IsSuccessStatusCode is true)
           {
               webhookUrl = _linkGenerator.GetUriByAction("Callback",
                   "TicketTailor", new {storeId}, "http", new HostString(bindAddress.ToString(), bindPort),
                   new PathString(rootPath));;
           }
           else
           {
               try
               {
                   result = null;
                   result = await CreateClient(attempt2).SendAsync(Create(attempt2), CancellationToken.None);
               }
               catch (Exception e)
               {
               }
               if (result?.IsSuccessStatusCode is true)
               {
                   webhookUrl = Request.GetAbsoluteUri(Url.Action("Callback",
                       "TicketTailor", new {storeId}));;
               }
               
           }

           if (webhookUrl is null)
           {
               ModelState.AddModelError("", $"{attempt1} or {attempt2} was not reachable by BTCPayServer.");
               
               return View(vm);
               
           }else if (vm.ApiKey is not null && vm.EventId is not null)
            {
                var webhooks = await btcPayServerClient.GetWebhooks(storeId);
                var webhook = webhooks.FirstOrDefault(data => data.Enabled && data.Url == webhookUrl && (data.AuthorizedEvents.Everything || data.AuthorizedEvents.SpecificEvents.Contains(WebhookEventType.InvoiceSettled)));
                if (webhook is null)
                {
                    await CreateWebhook(storeId, btcPayServerClient, webhookUrl);
                }
            }

            switch (command?.ToLowerInvariant())
            {
                case "save":
                    await _ticketTailorService.SetTicketTailorForStore(storeId, settings);
                    TempData["SuccessMessage"] = "TicketTailor settings modified";
                    return RedirectToAction(nameof(UpdateTicketTailorSettings), new {storeId});

                default:
                    return View(vm);
            }
        }

        private static async Task<string> CreateWebhook(string storeId, BTCPayServerClient btcPayServerClient,
            string webhookUrl)
        {
            var wh = await btcPayServerClient.CreateWebhook(storeId,
                new CreateStoreWebhookRequest()
                {
                    Enabled = true,
                    Url = webhookUrl,
                    AuthorizedEvents = new StoreWebhookBaseData.AuthorizedEventsData()
                    {
                        Everything = false,
                        SpecificEvents = new[] {WebhookEventType.InvoiceSettled}
                    },
                    AutomaticRedelivery = true
                });
            return wh.Id;
        }

        [AllowAnonymous]
        [HttpPost("callback")]
        public async Task<IActionResult> Callback(string storeId, [FromBody] WebhookInvoiceSettledEvent response, [FromQuery ]bool test)
        {
            if (test)
            {
                return Ok();
            }
            if (response.StoreId != storeId && response.Type != WebhookEventType.InvoiceSettled)
            {
                return BadRequest();
            }

            await _ticketTailorService.Handle(response.InvoiceId, response.StoreId, Request.GetAbsoluteRootUri());

            return Ok();
        }
        
        
    }
}
