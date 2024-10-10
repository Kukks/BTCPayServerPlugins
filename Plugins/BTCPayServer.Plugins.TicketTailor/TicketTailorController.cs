using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Controllers;
using BTCPayServer.Data;
using BTCPayServer.Models;
using BTCPayServer.Services;
using BTCPayServer.Services.Apps;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.TicketTailor
{
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public class TicketTailorController : Controller
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly TicketTailorService _ticketTailorService;
        private readonly UriResolver _uriResolver;
        private readonly AppService _appService;
        private readonly ApplicationDbContextFactory _contextFactory;
        private readonly InvoiceRepository _invoiceRepository;
        private readonly UIInvoiceController _uiInvoiceController;

        public TicketTailorController(IHttpClientFactory httpClientFactory,
            TicketTailorService ticketTailorService,
            UriResolver uriResolver,
            AppService appService,
            ApplicationDbContextFactory contextFactory,
            InvoiceRepository invoiceRepository,
            UIInvoiceController uiInvoiceController )
        {
            _httpClientFactory = httpClientFactory;
            _ticketTailorService = ticketTailorService;
            _uriResolver = uriResolver;
            _appService = appService;
            _contextFactory = contextFactory;
            _invoiceRepository = invoiceRepository;
            _uiInvoiceController = uiInvoiceController;
        }


        [AllowAnonymous]
        [HttpGet("plugins/{storeId}/TicketTailor")]
        public async Task<IActionResult> ViewLegacy(string storeId)
        {
            await using var ctx = _contextFactory.CreateContext();
            var app = await ctx.Apps
                .Where(data => data.StoreDataId == storeId && data.AppType == TicketTailorApp.AppType)
                .FirstOrDefaultAsync();

            if (app is null)
                return NotFound();

            return RedirectToAction(nameof(View), new {storeId, appId = app.Id});
        }


        [AllowAnonymous]
        [HttpGet("plugins/TicketTailor/{appId}")]
        public async Task<IActionResult> View(string appId)
        {
            var app = await _appService.GetApp(appId, TicketTailorApp.AppType, true);
            if (app is null)
                return NotFound();
            try
            {
                var config = app.GetSettings<TicketTailorSettings>();
                if (config?.ApiKey is not null && config?.EventId is not null)
                {
                    var client = new TicketTailorClient(_httpClientFactory, config.ApiKey);
                    var evt = await client.GetEvent(config.EventId);
                    if (evt is null)
                    {
                        return NotFound();
                    }

                    return View(new TicketTailorViewModel()
                    {
                        Event = evt, Settings = config,
                        StoreBranding =  await StoreBrandingViewModel.CreateAsync(Request, _uriResolver, app.StoreData.GetStoreBlob())
                    });
                }
            }
            catch (Exception e)
            {
            }

            return NotFound();
        }

        [AllowAnonymous]
        [HttpPost("plugins/TicketTailor/{appId}")]
        public async Task<IActionResult> Purchase(string appId, TicketTailorViewModel request, bool preview = false)
        {
            var app = await _appService.GetApp(appId, TicketTailorApp.AppType, true);
            if (app is null)
                return NotFound();

            (TicketTailorClient.Hold, string error)? hold = null;
            var config = app.GetSettings<TicketTailorSettings>();
            try
            {
                if (config?.ApiKey is not null && config?.EventId is not null)
                {
                    var client = new TicketTailorClient(_httpClientFactory, config.ApiKey);
                    var evt = await client.GetEvent(config.EventId);
                    if (evt is null || (!config.BypassAvailabilityCheck &&
                                        (evt.Unavailable == "true" || evt.TicketsAvailable == "false")))
                    {
                        return NotFound();
                    }

                    var price = 0m;
                    TicketTailorClient.DiscountCode discountCode = null;
                    if (!string.IsNullOrEmpty(request.DiscountCode) && config.AllowDiscountCodes)
                    {
                        discountCode = await client.GetDiscountCode(request.DiscountCode);
                        if (discountCode?.expires?.unix is not null &&
                            DateTimeOffset.FromUnixTimeSeconds(discountCode.expires.unix) < DateTimeOffset.Now)
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

                        var ticketType =
                            evt.TicketTypes.FirstOrDefault(type => type.Id == purchaseRequestItem.TicketTypeId);

                        var specificTicket =
                            config.SpecificTickets?.SingleOrDefault(ticket => ticketType?.Id == ticket.TicketTypeId);
                        if ((config.SpecificTickets?.Any() is true && specificTicket is null) || ticketType is null ||
                            (!string.IsNullOrEmpty(ticketType.AccessCode) &&
                             !ticketType.AccessCode.Equals(request.AccessCode,
                                 StringComparison.InvariantCultureIgnoreCase)) ||
                            !new[] {"on_sale", "locked"}.Contains(ticketType.Status.ToLowerInvariant())
                            || specificTicket?.Hidden is true)
                        {
                            if (preview)
                            {
                                return Json(new
                                {
                                   Error = "The ticket was not found."
                                });
                            }
                            TempData.SetStatusMessageModel(new StatusMessageModel
                            {
                                Severity = StatusMessageModel.StatusSeverity.Error,
                                Html = "The ticket was not found."
                            });
                            return RedirectToAction("View", new {appId});
                        }

                        if (purchaseRequestItem.Quantity > ticketType.MaxPerOrder ||
                            purchaseRequestItem.Quantity < ticketType.MinPerOrder)
                        {
                            if (preview)
                            {
                                return Json(new
                                {
                                    Error = "The amount of tickets was not allowed."
                                });
                            }
                            
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
                            ticketCost = specificTicket.Price.GetValueOrDefault(ticketType.Price);
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
                        return RedirectToAction("View", new {appId});
                    }


                    var redirectUrl = Request.GetAbsoluteUri(Url.Action("Receipt",
                        "TicketTailor", new {storeId = app.StoreDataId, invoiceId = "kukkskukkskukks"}));
                    redirectUrl = redirectUrl.Replace("kukkskukkskukks", "{InvoiceId}");
                    request.Name ??= string.Empty;
                    var nameSplit = request.Name.Split(" ", StringSplitOptions.RemoveEmptyEntries);
                    if (config.RequireFullName && nameSplit.Length < 2)
                    {
                        TempData.SetStatusMessageModel(new StatusMessageModel
                        {
                            Severity = StatusMessageModel.StatusSeverity.Error,
                            Html = "Please enter your full name."
                        });
                        return RedirectToAction("View", new {appId});
                    }

                    request.Name = nameSplit.Length switch
                    {
                        0 => "Satoshi Nakamoto",
                        < 2 => $"{nameSplit} Nakamoto",
                        _ => request.Name
                    };
                    var inv = await _uiInvoiceController.CreateInvoiceCoreRaw( new CreateInvoiceRequest()
                        {
                            Amount = price,
                            Currency = evt.Currency,
                            Type = InvoiceType.Standard,
                            AdditionalSearchTerms = new[] {"tickettailor", hold.Value.Item1.Id, evt.Id, AppService.GetAppSearchTerm(app)},
                            Checkout =
                            {
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
                                orderId = "tickettailor",
                                appId,
                                discountCode,
                                discountedAmount
                            }),
                            
                        }, app.StoreData, HttpContext.Request.GetAbsoluteRoot(),new List<string> { AppService.GetAppInternalTag(appId) }, CancellationToken.None);

                    while (inv.Price == 0 && inv.Status == InvoiceStatus.New)
                    {
                        if (inv.Status == InvoiceStatus.New)
                            inv = await _invoiceRepository.GetInvoice(inv.Id);
                    }

                    return inv.Status == InvoiceStatus.Settled
                        ? RedirectToAction("Receipt", new {invoiceId = inv.Id})
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

            return RedirectToAction("View", new {appId});
        }


        [AllowAnonymous]
        [HttpGet("plugins/{storeId}/TicketTailor/receipt")]
        [HttpGet("plugins/TicketTailor/{invoiceId}/receipt")]
        public async Task<IActionResult> Receipt(string invoiceId)
        {
            try
            {
                var inv =await _invoiceRepository.GetInvoice(invoiceId);
                if (inv is null)
                {
                    return NotFound();
                }

                if (inv.Metadata.OrderId != "tickettailor")
                {
                    return NotFound();
                }
                
                var appId = AppService.GetAppInternalTags(inv).First();
                
                var result = new TicketReceiptPage() {InvoiceId = invoiceId};
                result.Status = inv.Status;
                if (result.Status == InvoiceStatus.Settled &&
                    inv.Metadata.AdditionalData.TryGetValue("ticketIds", out var ticketIds))
                {
                    await SetTicketTailorTicketResult(appId, result, ticketIds.Values<string>());
                }
                else if (inv.Status == InvoiceStatus.Settled)
                {
                    await _ticketTailorService.CheckAndIssueTicket(inv.Id);
                }

                return View(result);
            }
            catch (Exception e)
            {
                return NotFound();
            }
        }

        private async Task SetTicketTailorTicketResult(string appId, TicketReceiptPage result,
            IEnumerable<string> ticketIds)
        {
            var app = await _appService.GetApp(appId, TicketTailorApp.AppType);

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


        [HttpGet("~/plugins/TicketTailor/{appId}/update")]
        public async Task<IActionResult> UpdateTicketTailorSettings(string appId)
        {
            UpdateTicketTailorSettingsViewModel vm = new();

            try
            {
                var app = await _appService.GetApp(appId, TicketTailorApp.AppType, false, true);
                TicketTailorSettings tt = app.GetSettings<TicketTailorSettings>();
                if (tt is not null)
                {
                    vm.ApiKey = tt.ApiKey;
                    vm.EventId = tt.EventId;
                    vm.ShowDescription = tt.ShowDescription;
                    vm.SendEmail = tt.SendEmail;
                    vm.BypassAvailabilityCheck = tt.BypassAvailabilityCheck;
                    vm.CustomCSS = tt.CustomCSS;
                    vm.RequireFullName = tt.RequireFullName;
                    vm.AllowDiscountCodes = tt.AllowDiscountCodes;
                    vm.SpecificTickets = tt.SpecificTickets;
                }
                vm.Archived = app.Archived;
                vm.AppName = app.Name;
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


        [HttpPost("~/plugins/TicketTailor/{appId}/update")]
        public async Task<IActionResult> UpdateTicketTailorSettings(string appId,
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
                AllowDiscountCodes = vm.AllowDiscountCodes,
                SendEmail = vm.SendEmail
            };


            switch (command?.ToLowerInvariant())
            {
                case "save":
                    var app = await _appService.GetApp(appId, TicketTailorApp.AppType, false, true);
                    app.SetSettings(settings);
                    app.Name = vm.AppName;
                    await _appService.UpdateOrCreateApp(app);
                    TempData["SuccessMessage"] = "TicketTailor settings modified";
                    return RedirectToAction(nameof(UpdateTicketTailorSettings), new {appId});

                default:
                    return View(vm);
            }
        }
    }
}