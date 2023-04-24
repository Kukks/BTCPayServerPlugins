using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Newtonsoft.Json.Linq;
using AuthenticationSchemes = BTCPayServer.Abstractions.Constants.AuthenticationSchemes;

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
        public async Task<IActionResult> Purchase(string storeId, TicketTailorViewModel request, bool preview = false)
        {
            var config = await _ticketTailorService.GetTicketTailorForStore(storeId);
            (TicketTailorClient.Hold, string error)? hold = null;
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

                    var price = 0m;
                    TicketTailorClient.DiscountCode discountCode = null;
                    if (!string.IsNullOrEmpty(request.DiscountCode) && config.AllowDiscountCodes)
                    {
                        discountCode = await client.GetDiscountCode(request.DiscountCode);
                        if (discountCode?.expires?.unix is not null && DateTimeOffset.FromUnixTimeSeconds(discountCode.expires.unix) < DateTimeOffset.Now)
                        {
                            discountCode = null;
                        }
                    }

                    var discountedAmount = 0m;
                    foreach (var purchaseRequestItem in request.Items)
                    {
                        if (purchaseRequestItem.Quantity <= 0)
                        {
                            continue;
                        }
                        var ticketType = evt.TicketTypes.FirstOrDefault(type => type.Id == purchaseRequestItem.TicketTypeId);
                       
                        var specificTicket =
                            config.SpecificTickets?.SingleOrDefault(ticket => ticketType?.Id == ticket.TicketTypeId);
                        if ((config.SpecificTickets?.Any() is true && specificTicket is null) || ticketType is null ||
                            (!string.IsNullOrEmpty(ticketType.AccessCode) && 
                             !ticketType.AccessCode.Equals(request.AccessCode, StringComparison.InvariantCultureIgnoreCase)) ||
                           !new []{"on_sale" , "locked"}.Contains(ticketType.Status.ToLowerInvariant())
                              || specificTicket?.Hidden is true)
                        {
                            TempData.SetStatusMessageModel(new StatusMessageModel
                            {
                                Severity = StatusMessageModel.StatusSeverity.Error,
                                Html = "The ticket was not found."
                            });
                            return RedirectToAction("View", new {storeId});
                        }

                        if (purchaseRequestItem.Quantity > ticketType.MaxPerOrder ||
                            purchaseRequestItem.Quantity < ticketType.MinPerOrder )
                        {
                            TempData.SetStatusMessageModel(new StatusMessageModel
                            {
                                Severity = StatusMessageModel.StatusSeverity.Error,
                                Html = "The amount of tickets was not allowed."
                            });
                            return RedirectToAction("View", new {storeId});
                        }

                        var ticketCost = ticketType.Price;
                        if (specificTicket is not null)
                        {
                            ticketCost =specificTicket.Price.GetValueOrDefault(ticketType.Price);
                        }

                        var originalTicketCost = ticketCost;
                        if (discountCode?.ticket_types.Contains(ticketType?.Id) is true)
                        {
                            switch (discountCode.type)
                            {
                                case "fixed_amount" when discountCode.face_value_amount is not null:
                                    ticketCost -= (discountCode.face_value_amount.Value / 100m);
                                    break;
                                case "percentage" when discountCode.face_value_percentage is not null:

                                    var percentageOff = (discountCode.face_value_percentage.Value / 100m);
                                    
                                    ticketCost -= (ticketCost * percentageOff);
                                    break;
                            }
                        }
        
                        var thisTicketBatchCost = ticketCost * purchaseRequestItem.Quantity;
                        discountedAmount += (originalTicketCost * purchaseRequestItem.Quantity) - thisTicketBatchCost;
                        
                        price += thisTicketBatchCost;
                    }

                    if (preview)
                    {
                        return Json(new
                        {
                            discountedAmount,
                            total = price
                        });
                    }

                    hold = await client.CreateHold(new TicketTailorClient.CreateHoldRequest()
                    {
                        EventId = evt.Id,
                        Note = "Created by BTCPay Server",
                        TicketTypeId = request.Items.ToDictionary(item => item.TicketTypeId, item => item.Quantity)
                    });
                    if (!string.IsNullOrEmpty(hold.Value.error))
                    {
                        TempData.SetStatusMessageModel(new StatusMessageModel
                        {
                            Severity = StatusMessageModel.StatusSeverity.Error,
                            Html = $"Could not reserve tickets because {hold.Value.error}"
                        });
                        return RedirectToAction("View", new {storeId});
                    }
                    
                    
                    var btcpayClient = await CreateClient(storeId);
                    var redirectUrl = Request.GetAbsoluteUri(Url.Action("Receipt",
                        "TicketTailor", new {storeId, invoiceId = "kukkskukkskukks"}));
                    redirectUrl = redirectUrl.Replace("kukkskukkskukks", "{InvoiceId}");
                    request.Name??=string.Empty;
                    var nameSplit = request.Name.Split(" ", StringSplitOptions.RemoveEmptyEntries);
                    if (config.RequireFullName && nameSplit.Length < 2)
                    {
                        TempData.SetStatusMessageModel(new StatusMessageModel
                        {
                            Severity = StatusMessageModel.StatusSeverity.Error,
                            Html = "Please enter your full name."
                        });
                        return RedirectToAction("View", new {storeId});
                    }

                    request.Name = nameSplit.Length switch
                    {
                        0 => "Satoshi Nakamoto",
                        < 2 => $"{nameSplit} Nakamoto",
                        _ => request.Name
                    };
                    var inv = await btcpayClient.CreateInvoice(storeId,
                        new CreateInvoiceRequest()
                        {
                            Amount = price,
                            Currency = evt.Currency,
                            Type = InvoiceType.Standard,
                            AdditionalSearchTerms = new[] {"tickettailor", hold.Value.Item1.Id, evt.Id},
                            Checkout =
                            {
                                RequiresRefundEmail = true,
                                RedirectAutomatically = price > 0,
                                RedirectURL = redirectUrl,
                            },
                            Receipt = new InvoiceDataBase.ReceiptOptions()
                            {
                                Enabled = false
                            },
                            Metadata = JObject.FromObject(new
                            {
                                btcpayUrl = Request.GetAbsoluteRoot(),
                                buyerName = request.Name, 
                                buyerEmail = request.Email, 
                                holdId = hold.Value.Item1.Id,
                                orderId="tickettailor",
                                discountCode,
                                discountedAmount
                            })
                        });

                    while (inv.Amount == 0 && inv.Status == InvoiceStatus.New)
                    {
                        if (inv.Status == InvoiceStatus.New)
                            inv = await btcpayClient.GetInvoice(inv.StoreId, inv.Id);
                    }

                    return inv.Status == InvoiceStatus.Settled
                        ? RedirectToAction("Receipt", new {storeId, invoiceId = inv.Id})
                        : RedirectToAction("Checkout", "UIInvoice", new {invoiceId = inv.Id});
                }
            }
            catch (Exception e)
            {
                TempData.SetStatusMessageModel(new StatusMessageModel
                {
                    Severity = StatusMessageModel.StatusSeverity.Error,
                    Html = $"Could not continue with ticket purchase.</br>{e.Message}"
                });
                if (hold?.Item1 is not null)
                {
                    
                    var client = new TicketTailorClient(_httpClientFactory, config.ApiKey);
                    await client.DeleteHold(hold?.Item1.Id);
                }
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
                if (invoice.Status == InvoiceStatus.Settled && 
                    invoice.Metadata.TryGetValue("orderId", out var orderId) && orderId.Value<string>() == "tickettailor" &&
                    invoice.Metadata.TryGetValue("ticketIds", out var ticketIds))
                {
                        await SetTicketTailorTicketResult(storeId, result, ticketIds.Values<string>());
                    
                }else if (invoice.Status == InvoiceStatus.Settled)
                {
                    await _ticketTailorService.CheckAndIssueTicket(invoice.Id);
                }

                return View(result);
            }
            catch (Exception e)
            {
                return NotFound();
            }
        }

        private async Task SetTicketTailorTicketResult(string storeId, TicketReceiptPage result, IEnumerable<string> ticketIds)
        {
            var settings = await _ticketTailorService.GetTicketTailorForStore(storeId);
            var client = new TicketTailorClient(_httpClientFactory, settings.ApiKey);
            var tickets = await Task.WhenAll(ticketIds.Select(s => client.GetTicket(s)));
            var evt = await client.GetEvent(settings.EventId);
            result.Event = evt;
            result.Tickets = tickets;
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
            public TicketTailorClient.IssuedTicket[] Tickets { get; set; }
            public TicketTailorClient.Event Event { get; set; }
            public TicketTailorSettings Settings { get; set; }
        }


        private readonly IHttpClientFactory _httpClientFactory;
        private readonly TicketTailorService _ticketTailorService;
        private readonly IBTCPayServerClientFactory _btcPayServerClientFactory;

        public TicketTailorController(IHttpClientFactory httpClientFactory,
            TicketTailorService ticketTailorService,
            IBTCPayServerClientFactory btcPayServerClientFactory)
        {
            
            _httpClientFactory = httpClientFactory;
            _ticketTailorService = ticketTailorService;
            _btcPayServerClientFactory = btcPayServerClientFactory;
        }

        [HttpGet("update")]
        public async Task<IActionResult> UpdateTicketTailorSettings(string storeId)
        {
            UpdateTicketTailorSettingsViewModel vm = new();
            TicketTailorSettings TicketTailor;
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
                    vm.RequireFullName = TicketTailor.RequireFullName;
                    vm.AllowDiscountCodes = TicketTailor.AllowDiscountCodes;
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
            string command)
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
                BypassAvailabilityCheck = vm.BypassAvailabilityCheck,
                RequireFullName = vm.RequireFullName,
                AllowDiscountCodes = vm.AllowDiscountCodes
            };
            

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
       
        
    }
}
