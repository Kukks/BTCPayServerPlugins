using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Models;
using BTCPayServer.Services;
using BTCPayServer.Services.Apps;
using BTCPayServer.Services.PaymentRequests;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using PaymentRequestData = BTCPayServer.Data.PaymentRequestData;

namespace BTCPayServer.Plugins.Subscriptions;

[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public class SubscriptionController : Controller
{
    private readonly AppService _appService;
    private readonly UriResolver _uriResolver;
    private readonly PaymentRequestRepository _paymentRequestRepository;
    private readonly SubscriptionService _subscriptionService;

    public SubscriptionController(AppService appService,
        UriResolver uriResolver,
        PaymentRequestRepository paymentRequestRepository, SubscriptionService subscriptionService)
    {
        _appService = appService;
        _uriResolver = uriResolver;
        _paymentRequestRepository = paymentRequestRepository;
        _subscriptionService = subscriptionService;
    }

    [AllowAnonymous]
    [HttpGet("~/plugins/subscription/{appId}")]
    public async Task<IActionResult> View(string appId)
    {
        var app = await _appService.GetApp(appId, SubscriptionApp.AppType, true, false);

        if (app == null)
            return NotFound();
        var ss = app.GetSettings<SubscriptionAppSettings>();
        ss.SubscriptionName = app.Name;
        ViewData["StoreBranding"] =await StoreBrandingViewModel.CreateAsync(Request, _uriResolver, app.StoreData.GetStoreBlob());
        return View(ss);
    }

    [AllowAnonymous]
    [HttpGet("~/plugins/subscription/{appId}/{id}")]
    public async Task<IActionResult> ViewSubscription(string appId, string id)
    {
        var app = await _appService.GetApp(appId, SubscriptionApp.AppType, true, false);

        if (app == null)
            return NotFound();
        var ss = app.GetSettings<SubscriptionAppSettings>();
        ss.SubscriptionName = app.Name;
        if (!ss.Subscriptions.TryGetValue(id, out _))
        {
            return NotFound();
        }
        ViewData["StoreBranding"] =await StoreBrandingViewModel.CreateAsync(Request, _uriResolver, app.StoreData.GetStoreBlob());

        return View(ss);
    }

    [AllowAnonymous]
    [HttpGet("~/plugins/subscription/{appId}/{id}/reactivate")]
    public async Task<IActionResult> Reactivate(string appId, string id)
    {
        var pr = await _subscriptionService.ReactivateSubscription(appId, id);
        if (pr == null)
            return NotFound();
        return RedirectToAction("ViewPaymentRequest", "UIPaymentRequest", new {payReqId = pr.Id});
    }


    [AllowAnonymous]
    [HttpGet("~/plugins/subscription/{appId}/subscribe")]
    public async Task<IActionResult> Subscribe(string appId)
    {
        var app = await _appService.GetApp(appId, SubscriptionApp.AppType, false, false);

        if (app == null)
            return NotFound();
        var ss = app.GetSettings<SubscriptionAppSettings>();
        ss.SubscriptionName = app.Name;

        var pr = new PaymentRequestData()
        {
            StoreDataId = app.StoreDataId,
            Archived = false,
            Status = Client.Models.PaymentRequestData.PaymentRequestStatus.Pending
        };
        pr.SetBlob(new CreatePaymentRequestRequest()
        {
            Amount = ss.Price,
            Currency = ss.Currency,
            ExpiryDate = DateTimeOffset.UtcNow.AddDays(1),
            Description = ss.Description,
            Title = ss.SubscriptionName,
            FormId = ss.FormId,
            AllowCustomPaymentAmounts = false,
            AdditionalData = new Dictionary<string, JToken>()
            {
                {"source", JToken.FromObject("subscription")},
                {"appId", JToken.FromObject(appId)},
                {"url", HttpContext.Request.GetAbsoluteRoot()}
            },
        });

        pr = await _paymentRequestRepository.CreateOrUpdatePaymentRequest(pr);

        return RedirectToAction("ViewPaymentRequest", "UIPaymentRequest", new {payReqId = pr.Id});
    }


    [HttpGet("~/plugins/subscription/{appId}/update")]
    public async Task<IActionResult> Update(string appId)
    {
        var app = await _appService.GetApp(appId, SubscriptionApp.AppType, false, true);

        if (app == null)
            return NotFound();
        ViewData["archived"] = app.Archived;
        var ss = app.GetSettings<SubscriptionAppSettings>();
        ss.SubscriptionName = app.Name;

        return View(ss);
    }

    [HttpPost("~/plugins/subscription/{appId}/update")]
    public async Task<IActionResult> Update(string appId, SubscriptionAppSettings vm)
    {
        var app = await _appService.GetApp(appId, SubscriptionApp.AppType, true, true);

        if (string.IsNullOrEmpty(vm.Currency))
        {
            vm.Currency = app.StoreData.GetStoreBlob().DefaultCurrency;
            ModelState.Remove(nameof(vm.Currency));
        }

        if (string.IsNullOrEmpty(vm.Currency))
        {
            ModelState.AddModelError(nameof(vm.Currency), "Currency is required");
        }

        if (string.IsNullOrEmpty(vm.SubscriptionName))
        {
            ModelState.AddModelError(nameof(vm.SubscriptionName), "Subscription name is required");
        }

        if (vm.Price <= 0)
        {
            ModelState.AddModelError(nameof(vm.Price), "Price must be greater than 0");
        }

        if (vm.Duration <= 0)
        {
            ModelState.AddModelError(nameof(vm.Duration), "Duration must be greater than 0");
        }


        ViewData["archived"] = app.Archived;
        if (!ModelState.IsValid)
        {
            return View(vm);
        }

        var old = app.GetSettings<SubscriptionAppSettings>();
        vm.Subscriptions = old.Subscriptions;
        app.SetSettings(vm);
        app.Name = vm.SubscriptionName;
        await _appService.UpdateOrCreateApp(app);
        TempData["SuccessMessage"] = "Subscription settings modified";
        return RedirectToAction(nameof(Update), new {appId});
    }
}