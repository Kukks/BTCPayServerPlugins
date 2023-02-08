using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Controllers;
using BTCPayServer.Data;
using BTCPayServer.Filters;
using BTCPayServer.Services.Apps;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Newtonsoft.Json.Linq;
using AuthenticationSchemes = BTCPayServer.Abstractions.Constants.AuthenticationSchemes;

namespace BTCPayServer.Plugins.TicketTailor
{
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    [Route("plugins/TicketTailor/{appId}")]
    public class UITicketTailorController : Controller
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly AppService _appService;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly UIInvoiceController _uiInvoiceController;
        private readonly InvoiceRepository _invoiceRepository;

        public UITicketTailorController(IHttpClientFactory httpClientFactory,
            AppService appService,  UserManager<ApplicationUser> userManager, 
            UIInvoiceController uiInvoiceController, InvoiceRepository invoiceRepository)
        {
            
            _httpClientFactory = httpClientFactory;
            _appService = appService;
            _userManager = userManager;
            _uiInvoiceController = uiInvoiceController;
            _invoiceRepository = invoiceRepository;
        }
        [AllowAnonymous]
        [DomainMappingConstraint(TicketTailorApp.AppType)]
        [HttpGet("")]
        [HttpGet("/")]
        [XFrameOptions(XFrameOptionsAttribute.XFrameOptions.AllowAll)]
        public async Task<IActionResult> View(string appId)
        {

            var app = await _appService.GetApp(appId, TicketTailorApp.AppType);
            if (app is null)
            {
                return NotFound();
            }

            var config = app.GetSettings<TicketTailorSettings>();
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
        [DomainMappingConstraint(TicketTailorApp.AppType)]
        [HttpPost("")]
        [HttpPost("/")]
        [XFrameOptions(XFrameOptionsAttribute.XFrameOptions.AllowAll)]
        public async Task<IActionResult> Purchase(string appId, TicketTailorViewModel request)
        {
            var app = await _appService.GetApp(appId, TicketTailorApp.AppType, true);
            if (app is null)
            {
                return NotFound();
            }

            var config = app.GetSettings<TicketTailorSettings>();
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
                    foreach (var purchaseRequestItem in request.Items)
                    {
                        if (purchaseRequestItem.Quantity <= 0)
                        {
                            continue;;
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
                            return RedirectToAction("View", new {appId});
                        }

                        if (purchaseRequestItem.Quantity > ticketType.MaxPerOrder ||
                            purchaseRequestItem.Quantity < ticketType.MinPerOrder )
                        {
                            TempData.SetStatusMessageModel(new StatusMessageModel
                            {
                                Severity = StatusMessageModel.StatusSeverity.Error,
                                Html = "The amount of tickets was not allowed."
                            });
                            return RedirectToAction("View", new {appId});
                        }

                        var ticketCost = ticketType.Price;
                        if (specificTicket is not null)
                        {
                            ticketCost =specificTicket.Price.GetValueOrDefault(ticketType.Price);
                        }

                        price += ticketCost * purchaseRequestItem.Quantity;
                    }

                    var hold = await client.CreateHold(new TicketTailorClient.CreateHoldRequest()
                    {
                        EventId = evt.Id,
                        Note = "Created by BTCPay Server",
                        TicketTypeId = request.Items.ToDictionary(item => item.TicketTypeId, item => item.Quantity)
                    });
                    if (!string.IsNullOrEmpty(hold.error))
                    {
                        TempData.SetStatusMessageModel(new StatusMessageModel
                        {
                            Severity = StatusMessageModel.StatusSeverity.Error,
                            Html = $"Could not reserve tickets because {hold.error}"
                        });
                        return RedirectToAction("View", new {appId});
                    }
                    
                    
                    var redirectUrl = Request.GetAbsoluteUri(Url.Action("Receipt",
                        "UITicketTailor", new {appId, invoiceId = "kukkskukkskukks"}));
                    redirectUrl = redirectUrl.Replace("kukkskukkskukks", "{InvoiceId}");
                    if(string.IsNullOrEmpty(request.Name))
                    {
                        request.Name = "Anonymous lizard";
                    }

                    var nameSplit = request.Name.Split(" ", StringSplitOptions.RemoveEmptyEntries);
                    if (nameSplit.Length < 2)
                    {
                        request.Name = nameSplit[0] + " Lizard";
                    }

                    var invoice = await _uiInvoiceController.CreateInvoiceCoreRaw(new CreateInvoiceRequest()
                        {
                            Amount = price,
                            Type = InvoiceType.Standard,
                            Currency = evt.Currency,
                            AdditionalSearchTerms = new[] {hold.Item1.Id, evt.Id, app.Id, app.AppType},
                            Receipt = new InvoiceDataBase.ReceiptOptions()
                            {
                                Enabled = false,
                            },
                            Checkout =
                            {
                                RequiresRefundEmail = true,
                                RedirectAutomatically = price > 0,
                                RedirectURL = redirectUrl,
                            },
                            Metadata = JObject.FromObject(new
                            {
                                btcpayUrl = Request.GetAbsoluteRoot(),
                                buyerName = request.Name,
                                buyerEmail = request.Email,
                                holdId = hold.Item1.Id,
                                orderId = AppService.GetAppOrderId(app)
                            })
                        }, app.StoreData, Request.GetAbsoluteRoot(),
                        new List<string>()
                        {
                            AppService.GetAppInternalTag(app.Id),
                            AppService.GetAppOrderId(app)
                        }, CancellationToken.None);

                    return invoice.Price == 0 ? RedirectToAction("Receipt", new {appId, invoiceId = invoice.Id}) : RedirectToAction("Checkout","UIInvoice", new {invoiceId = invoice.Id});
                }
            }
            catch (Exception e)
            {
            }

            return RedirectToAction("View", new {appId});
        }


        [AllowAnonymous]
        [HttpGet("receipt")]
        public async Task<IActionResult> Receipt(string appId, string invoiceId)
        {
            var app = await _appService.GetApp(appId, TicketTailorApp.AppType, true);
            if (app is null)
            {
                return NotFound();
            }

            var invoice = await _invoiceRepository.GetInvoice(invoiceId);
            if (!invoice.GetInternalTags("").Contains(AppService.GetAppOrderId(app)))
            {
                return NotFound();
            }
            try
            {
                var result = new TicketReceiptPage() {InvoiceId = invoiceId};
                result.Status = invoice.Status.ToModernStatus();
                if (result.Status != InvoiceStatus.Settled) return View(result);
                var ticketIds = invoice.Metadata.GetMetadata<string[]>("ticketIds");
                if(ticketIds?.Any() is true)
                    await SetTicketTailorTicketResult(app, result, ticketIds);

                return View(result);
            }
            catch (Exception e)
            {
                return NotFound();
            }
        }

        private async Task SetTicketTailorTicketResult(AppData app, TicketReceiptPage result, IEnumerable<string> ticketIds)
        {
            var settings = app.GetSettings<TicketTailorSettings>();
            var client = new TicketTailorClient(_httpClientFactory, settings.ApiKey);
            var tickets = await Task.WhenAll(ticketIds.Select(s => client.GetTicket(s)));
            var evt = await client.GetEvent(settings.EventId);
            result.Event = evt;
            result.Tickets = tickets;
            result.Settings = settings;
        }
        
        public class TicketReceiptPage
        {
            public string InvoiceId { get; set; }
            public InvoiceStatus Status { get; set; }
            public TicketTailorClient.IssuedTicket[] Tickets { get; set; }
            public TicketTailorClient.Event Event { get; set; }
            public TicketTailorSettings Settings { get; set; }
        }




        [HttpGet("update")]
        public async Task<IActionResult> UpdateTicketTailorSettings(string appId)
        {
            var app = await _appService.GetAppDataIfOwner(_userManager.GetUserId(User), appId, TicketTailorApp.AppType);
            if (app is null)
            {
                return NotFound();
            }
            UpdateTicketTailorSettingsViewModel vm = new();
            try
            {
                var settings = app.GetSettings<TicketTailorSettings>();
                if (settings is not null)
                {
                    vm.ApiKey = settings.ApiKey;
                    vm.EventId = settings.EventId;
                    vm.ShowDescription = settings.ShowDescription;
                    vm.BypassAvailabilityCheck = settings.BypassAvailabilityCheck;
                    vm.CustomCSS = settings.CustomCSS;
                    vm.SpecificTickets = settings.SpecificTickets;
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
        public async Task<IActionResult> UpdateTicketTailorSettings(string appId,
            UpdateTicketTailorSettingsViewModel vm,
            string command)
        {
            var app = await _appService.GetAppDataIfOwner(_userManager.GetUserId(User), appId, TicketTailorApp.AppType);
            if (app is null)
            {
                return NotFound();
            }
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
           
            switch (command?.ToLowerInvariant())
            {
                case "save":
                    app.SetSettings(settings);
                    await _appService.UpdateOrCreateApp(app);
                    TempData["SuccessMessage"] = "TicketTailor settings modified";
                    return RedirectToAction(nameof(UpdateTicketTailorSettings), new {appId});

                default:
                    return View(vm);
            }
        }
       
        
    }
}
