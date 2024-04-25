using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Client.Models;
using BTCPayServer.Controllers;
using BTCPayServer.Data;
using BTCPayServer.HostedServices;
using BTCPayServer.HostedServices.Webhooks;
using BTCPayServer.Services;
using BTCPayServer.Services.Apps;
using BTCPayServer.Services.PaymentRequests;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PaymentRequestData = BTCPayServer.Client.Models.PaymentRequestData;
using WebhookDeliveryData = BTCPayServer.Data.WebhookDeliveryData;

namespace BTCPayServer.Plugins.Subscriptions;

public class SubscriptionService : EventHostedServiceBase, IWebhookProvider
{
    private readonly AppService _appService;
    private readonly PaymentRequestRepository _paymentRequestRepository;
    private readonly LinkGenerator _linkGenerator;
    private readonly BTCPayNetworkJsonSerializerSettings _btcPayNetworkJsonSerializerSettings;
    private readonly WebhookSender _webhookSender;

    public const string PaymentRequestSubscriptionIdKey = "subscriptionId";
    public const string PaymentRequestSourceKey = "source";
    public const string PaymentRequestSourceValue = "subscription";
    public const string PaymentRequestAppId = "appId";
    
    
    
    public SubscriptionService(EventAggregator eventAggregator,
        ILogger<SubscriptionService> logger,
        AppService appService,
        PaymentRequestRepository paymentRequestRepository,
        LinkGenerator linkGenerator,
        BTCPayNetworkJsonSerializerSettings btcPayNetworkJsonSerializerSettings,
        WebhookSender webhookSender) : base(eventAggregator, logger)
    {
        _appService = appService;
        _paymentRequestRepository = paymentRequestRepository;
        _linkGenerator = linkGenerator;
        _btcPayNetworkJsonSerializerSettings = btcPayNetworkJsonSerializerSettings;
        _webhookSender = webhookSender;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        
        await  base.StartAsync(cancellationToken);
        _ = ScheduleChecks();
    }

    private CancellationTokenSource _checkTcs = new();
    private async Task ScheduleChecks()
    {
        
        while (!CancellationToken.IsCancellationRequested)
        {
            try
            {

                await CreatePaymentRequestForActiveSubscriptionCloseToEnding();
            }
            catch (Exception e)
            {
                Logs.PayServer.LogError(e, "Error while checking subscriptions");
            }
            _checkTcs = new CancellationTokenSource();
            _checkTcs.CancelAfter(TimeSpan.FromHours(1));
            await CancellationTokenSource.CreateLinkedTokenSource(_checkTcs.Token, CancellationToken).Token;
        }
    }

    public async Task<Data.PaymentRequestData?> ReactivateSubscription(string appId, string subscriptionId)
    {
        var tcs = new TaskCompletionSource<object>();
        PushEvent(new SequentialExecute(async () =>
        {
            var app = await _appService.GetApp(appId, SubscriptionApp.AppType, false, true);
            if (app == null)
            {
                return null;
            }

            var settings = app.GetSettings<SubscriptionAppSettings>();
            if (!settings.Subscriptions.TryGetValue(subscriptionId, out var subscription))
            {
                return null;
            }

            if (subscription.Status == SubscriptionStatus.Active)
                return null;

            var lastSettled = subscription.Payments.Where(p => p.Settled).MaxBy(history => history.PeriodEnd);
            var lastPr =
                await _paymentRequestRepository.FindPaymentRequest(lastSettled.PaymentRequestId, null,
                    CancellationToken.None);
            var lastBlob = lastPr.GetBlob();

            var pr = new Data.PaymentRequestData()
            {
                StoreDataId = app.StoreDataId,
                Status = PaymentRequestData.PaymentRequestStatus.Pending,
                Created = DateTimeOffset.UtcNow, Archived = false,
            };
            var additionalData = lastBlob.AdditionalData;
            additionalData[PaymentRequestSubscriptionIdKey] = JToken.FromObject(subscriptionId);
            pr.SetBlob(new PaymentRequestBaseData()
            {
                ExpiryDate = DateTimeOffset.UtcNow.AddDays(1),
                Amount = settings.Price,
                Currency = settings.Currency,
                StoreId = app.StoreDataId,
                Title = $"{settings.SubscriptionName} Subscription Reactivation",
                Description = settings.Description,
                AdditionalData = additionalData
            });
            return await _paymentRequestRepository.CreateOrUpdatePaymentRequest(pr);
        }, tcs));
        
        
        return await tcs.Task as Data.PaymentRequestData;
    }


    private async Task CreatePaymentRequestForActiveSubscriptionCloseToEnding()
    {
        var tcs = new TaskCompletionSource<object>();

        PushEvent(new SequentialExecute(async () =>
        {
            var apps = await _appService.GetApps(SubscriptionApp.AppType);
            apps = apps.Where(data => !data.Archived).ToList();
            List<(string appId, string subscriptionId, string paymentRequestId, string email)> deliverRequests = new();
            foreach (var app in apps)
            {
                var settings = app.GetSettings<SubscriptionAppSettings>();
                settings.SubscriptionName = app.Name;
                if (settings.Subscriptions?.Any() is true)
                {
                    var changedSubscriptions = new List<KeyValuePair<string, Subscription>>();
                    
                    foreach (var subscription in settings.Subscriptions)
                    {
                        var changed = DetermineStatusOfSubscription(subscription.Value);
                        if (subscription.Value.Status == SubscriptionStatus.Active)
                        {
                            var currentPeriod = subscription.Value.Payments.FirstOrDefault(p => p.Settled &&
                                p.PeriodStart <= DateTimeOffset.UtcNow &&
                                p.PeriodEnd >= DateTimeOffset.UtcNow);

                            //there should only ever be one future payment request at a time
                            var nextPeriod =
                                subscription.Value.Payments.FirstOrDefault(p => p.PeriodStart > DateTimeOffset.UtcNow);

                            if (currentPeriod is null || nextPeriod is not null)
                                continue;


                            var noticePeriod = currentPeriod.PeriodEnd - DateTimeOffset.UtcNow;

                            var lastPr = await _paymentRequestRepository.FindPaymentRequest(
                                currentPeriod.PaymentRequestId, null,
                                CancellationToken.None);
                            var lastBlob = lastPr.GetBlob();

                            if (noticePeriod.Days <= Math.Min(3, settings.Duration))
                            {
                                var pr = new Data.PaymentRequestData()
                                {
                                    StoreDataId = app.StoreDataId,
                                    Status = PaymentRequestData.PaymentRequestStatus.Pending,
                                    Created = DateTimeOffset.UtcNow, Archived = false
                                };
                                pr.SetBlob(new PaymentRequestBaseData()
                                {
                                    ExpiryDate = currentPeriod.PeriodEnd,
                                    Amount = settings.Price,
                                    Currency = settings.Currency,
                                    StoreId = app.StoreDataId,
                                    Title = $"{settings.SubscriptionName} Subscription Renewal",
                                    Description = settings.Description,
                                    AdditionalData = lastBlob.AdditionalData
                                });
                                pr = await _paymentRequestRepository.CreateOrUpdatePaymentRequest(pr);

                                var start = DateOnly.FromDateTime(currentPeriod.PeriodEnd.AddDays(1));
                                var end = settings.DurationType == DurationType.Day
                                    ? start.AddDays(settings.Duration)
                                    : start.AddMonths(settings.Duration);
                                var newHistory = new SubscriptionPaymentHistory()
                                {
                                    PaymentRequestId = pr.Id,
                                    PeriodStart = start.ToDateTime(TimeOnly.MinValue),
                                    PeriodEnd = end.ToDateTime(TimeOnly.MinValue),
                                    Settled = false
                                };
                                subscription.Value.Payments.Add(newHistory);

                                deliverRequests.Add((app.Id, subscription.Key, pr.Id, subscription.Value.Email));
                            }
                        }
                        if(changed)
                            changedSubscriptions.Add(subscription);
                    }

                    app.SetSettings(settings);

                    await _appService.UpdateOrCreateApp(app);
                    
                    if (changedSubscriptions.Any())
                    {
                        var webhooks = await _webhookSender.GetWebhooks(app.StoreDataId, SubscriptionStatusUpdated);
                        foreach (var changedSubscription in changedSubscriptions)
                        {
                            foreach (var webhook in webhooks)
                            {
                                _webhookSender.EnqueueDelivery(CreateSubscriptionStatusUpdatedDeliveryRequest(webhook, app.Id,
                                    app.StoreDataId,
                                    changedSubscription.Key, changedSubscription.Value.Status, null, changedSubscription.Value.Email));
                            }

                            EventAggregator.Publish(CreateSubscriptionStatusUpdatedDeliveryRequest(null, app.Id, app.StoreDataId,
                                changedSubscription.Key, changedSubscription.Value.Status, null, changedSubscription.Value.Email));
                        }
                        
                    }
                }

                foreach (var deliverRequest in deliverRequests)
                {
                    var webhooks = await _webhookSender.GetWebhooks(app.StoreDataId, SubscriptionRenewalRequested);
                    foreach (var webhook in webhooks)
                    {
                        _webhookSender.EnqueueDelivery(CreateSubscriptionRenewalRequestedDeliveryRequest(webhook,
                            app.Id, app.StoreDataId, deliverRequest.subscriptionId, null,
                            deliverRequest.paymentRequestId, deliverRequest.email));
                    }

                    EventAggregator.Publish(CreateSubscriptionRenewalRequestedDeliveryRequest(null, app.Id,
                        app.StoreDataId, deliverRequest.subscriptionId, null,
                        deliverRequest.paymentRequestId, deliverRequest.email));
                }
            }

            return null;
        }, tcs));
        await tcs.Task;
    }

    protected override void SubscribeToEvents()
    {
        Subscribe<PaymentRequestEvent>();
        Subscribe<SequentialExecute>();
        base.SubscribeToEvents();
    }


    public record SequentialExecute(Func<Task<object>> Action, TaskCompletionSource<object> TaskCompletionSource);


    protected override async Task ProcessEvent(object evt, CancellationToken cancellationToken)
    {
        switch (evt)
        {
            case SequentialExecute sequentialExecute:
            {
                var task = await sequentialExecute.Action();
                sequentialExecute.TaskCompletionSource.SetResult(task);
                return;
            }
            case PaymentRequestEvent {Type: PaymentRequestEvent.StatusChanged} paymentRequestStatusUpdated:
            {
                var prBlob = paymentRequestStatusUpdated.Data.GetBlob();
                if (!prBlob.AdditionalData.TryGetValue(PaymentRequestSourceKey, out var src) ||
                    src.Value<string>() != PaymentRequestSourceValue ||
                    !prBlob.AdditionalData.TryGetValue(PaymentRequestAppId, out var subscriptionAppidToken) ||
                    subscriptionAppidToken.Value<string>() is not { } subscriptionAppId)
                {
                    return;
                }

                var isNew = !prBlob.AdditionalData.TryGetValue(PaymentRequestSubscriptionIdKey, out var subscriptionIdToken);

                if (isNew && paymentRequestStatusUpdated.Data.Status !=
                    PaymentRequestData.PaymentRequestStatus.Completed)
                {
                    return;
                }

                if (paymentRequestStatusUpdated.Data.Status == PaymentRequestData.PaymentRequestStatus.Completed)
                {
                    var subscriptionId = subscriptionIdToken?.Value<string>();
                    var blob = paymentRequestStatusUpdated.Data.GetBlob();
                    var email = blob.Email ?? blob.FormResponse?["buyerEmail"]?.Value<string>();
                    await HandlePaidSubscription(subscriptionAppId, subscriptionId, paymentRequestStatusUpdated.Data.Id,
                        email);
                }
                else if (!isNew)
                {
                    await HandleUnSettledSubscription(subscriptionAppId, subscriptionIdToken.Value<string>(),
                        paymentRequestStatusUpdated.Data.Id);
                }

                
                await _checkTcs.CancelAsync();

                break;
            }
            // case PaymentRequestEvent {Type: PaymentRequestEvent.Updated} paymentRequestEvent:
            // {
            //     var prBlob = paymentRequestEvent.Data.GetBlob();
            //     if (!prBlob.AdditionalData.TryGetValue("source", out var src) ||
            //         src.Value<string>() != "subscription" ||
            //         !prBlob.AdditionalData.TryGetValue("appId", out var subscriptionAppidToken) ||
            //         subscriptionAppidToken.Value<string>() is not { } subscriptionAppId)
            //     {
            //         return;
            //     }
            //
            //     
            //     var isNew = !prBlob.AdditionalData.TryGetValue("subscriptionId", out var subscriptionIdToken);
            //     if(isNew)
            //         return;
            //     
            //     var app = await _appService.GetApp(subscriptionAppId, SubscriptionApp.AppType, false, true);
            //     if (app == null)
            //     {
            //         return;
            //     }
            //
            //     var settings = app.GetSettings<SubscriptionAppSettings>();
            //
            //     var subscriptionId = subscriptionIdToken!.Value<string>();
            //
            //     if (!settings.Subscriptions.TryGetValue(subscriptionId, out var subscription))
            //     {
            //         return;
            //     }
            //
            //     var payment = subscription.Payments.Find(p => p.PaymentRequestId == paymentRequestEvent.Data.Id);
            //
            //     if (payment is null)
            //     {
            //         return;
            //     }
            // }
        }

        await base.ProcessEvent(evt, cancellationToken);
    }

    private async Task HandleUnSettledSubscription(string appId, string subscriptionId, string paymenRequestId)
    {
        var app = await _appService.GetApp(appId, SubscriptionApp.AppType, false, true);
        if (app == null)
        {
            return;
        }

        var settings = app.GetSettings<SubscriptionAppSettings>();
        if (settings.Subscriptions.TryGetValue(subscriptionId, out var subscription))
        {
            var existingPayment = subscription.Payments.Find(p => p.PaymentRequestId == paymenRequestId);
            if (existingPayment is not null)
                existingPayment.Settled = false;

            var changed = DetermineStatusOfSubscription(subscription);

            app.SetSettings(settings);
            await _appService.UpdateOrCreateApp(app);

            if (changed)
            {
                var webhooks = await _webhookSender.GetWebhooks(app.StoreDataId, SubscriptionStatusUpdated);
                foreach (var webhook in webhooks)
                {
                    _webhookSender.EnqueueDelivery(CreateSubscriptionStatusUpdatedDeliveryRequest(webhook, app.Id,
                        app.StoreDataId,
                        subscriptionId, subscription.Status, null, subscription.Email));
                }

                EventAggregator.Publish(CreateSubscriptionStatusUpdatedDeliveryRequest(null, app.Id, app.StoreDataId,
                    subscriptionId, subscription.Status, null, subscription.Email));
            }
        }
    }

    private async Task HandlePaidSubscription(string appId, string? subscriptionId, string paymentRequestId,
        string? email)
    {
        var app = await _appService.GetApp(appId, SubscriptionApp.AppType, false, true);
        if (app == null)
        {
            return;
        }

        var settings = app.GetSettings<SubscriptionAppSettings>();

        subscriptionId ??= Guid.NewGuid().ToString();

        var start = DateOnly.FromDateTime(DateTimeOffset.UtcNow.DateTime);
        var end = settings.DurationType == DurationType.Day? start.AddDays(settings.Duration).ToDateTime(TimeOnly.MaxValue): start.AddMonths(settings.Duration).ToDateTime(TimeOnly.MaxValue);
        if (!settings.Subscriptions.TryGetValue(subscriptionId, out var subscription))
        {
            subscription = new Subscription()
            {
                Email = email,
                Start = DateTimeOffset.UtcNow,
                Status = SubscriptionStatus.Inactive,
                Payments =
                [
                    new SubscriptionPaymentHistory()
                    {
                        PaymentRequestId = paymentRequestId,
                        PeriodStart = start.ToDateTime(TimeOnly.MinValue),
                        PeriodEnd = end,
                        Settled = true
                    }
                ]
            };
            settings.Subscriptions.Add(subscriptionId, subscription);
        }

        var existingPayment = subscription.Payments.Find(p => p.PaymentRequestId == paymentRequestId);
        if (existingPayment is null)
        {
            subscription.Payments.Add(new SubscriptionPaymentHistory()
            {
                PaymentRequestId = paymentRequestId,
                PeriodStart = start.ToDateTime(TimeOnly.MinValue),
                PeriodEnd = end,
                Settled = true
            });
        }
        else
        {
            existingPayment.Settled = true;
        }


        var changed = DetermineStatusOfSubscription(subscription);
        app.SetSettings(settings);
        await _appService.UpdateOrCreateApp(app);

        var paymentRequest =
            await _paymentRequestRepository.FindPaymentRequest(paymentRequestId, null, CancellationToken.None);
        var blob = paymentRequest.GetBlob();
        blob.AdditionalData.TryGetValue("url", out var urlToken);
        var path = _linkGenerator.GetPathByAction("ViewSubscription", "Subscription", new {appId, id = subscriptionId});
        var url = new Uri(new Uri(urlToken.Value<string>()), path);
        if (blob.Description.Contains(url.ToString()))
            return;
        var subscriptionHtml =
            "<div class=\"d-flex justify-content-center mt-4\"><a class=\"btn btn-primary\" href=\"" + url +
            "\">View Subscription</a></div>";
        blob.Description += subscriptionHtml;
        blob.AdditionalData["subscriptionHtml"] = JToken.FromObject(subscriptionHtml);
        blob.AdditionalData["subscriptionUrl"] = JToken.FromObject(url);
        paymentRequest.SetBlob(blob);
        await _paymentRequestRepository.CreateOrUpdatePaymentRequest(paymentRequest);
        if (changed)
        {
            var webhooks = await _webhookSender.GetWebhooks(app.StoreDataId, SubscriptionStatusUpdated);
            foreach (var webhook in webhooks)
            {
                _webhookSender.EnqueueDelivery(CreateSubscriptionStatusUpdatedDeliveryRequest(webhook, app.Id,
                    app.StoreDataId,
                    subscriptionId, subscription.Status, url.ToString(), subscription.Email));
            }

            EventAggregator.Publish(CreateSubscriptionStatusUpdatedDeliveryRequest(null, app.Id, app.StoreDataId,
                subscriptionId, subscription.Status, url.ToString(), subscription.Email));
        }
    }

    SubscriptionWebhookDeliveryRequest CreateSubscriptionStatusUpdatedDeliveryRequest(WebhookData? webhook,
        string appId, string storeId, string subscriptionId, SubscriptionStatus status, string subscriptionUrl,
        string email)
    {
        var webhookEvent = new WebhookSubscriptionEvent(SubscriptionStatusUpdated, storeId)
        {
            WebhookId = webhook?.Id,
            AppId = appId,
            SubscriptionId = subscriptionId,
            Status = status.ToString(),
            Email = email
        };
        var delivery = webhook is null ? null : WebhookExtensions.NewWebhookDelivery(webhook.Id);
        if (delivery is not null)
        {
            webhookEvent.DeliveryId = delivery.Id;
            webhookEvent.OriginalDeliveryId = delivery.Id;
            webhookEvent.Timestamp = delivery.Timestamp;
        }

        return new SubscriptionWebhookDeliveryRequest(subscriptionUrl, webhook?.Id,
            webhookEvent,
            delivery,
            webhook?.GetBlob(), _btcPayNetworkJsonSerializerSettings);
    }

    SubscriptionWebhookDeliveryRequest CreateSubscriptionRenewalRequestedDeliveryRequest(WebhookData? webhook,
        string appId, string storeId, string subscriptionId, string subscriptionUrl,
        string paymentRequestId, string email)
    {
        var webhookEvent = new WebhookSubscriptionEvent(SubscriptionRenewalRequested, storeId)
        {
            WebhookId = webhook?.Id,
            AppId = appId,
            SubscriptionId = subscriptionId,
            PaymentRequestId = paymentRequestId,
            Email = email
        };
        var delivery = webhook is null ? null : WebhookExtensions.NewWebhookDelivery(webhook.Id);
        if (delivery is not null)
        {
            webhookEvent.DeliveryId = delivery.Id;
            webhookEvent.OriginalDeliveryId = delivery.Id;
            webhookEvent.Timestamp = delivery.Timestamp;
        }

        return new SubscriptionWebhookDeliveryRequest(subscriptionUrl, webhook?.Id,
            webhookEvent,
            delivery,
            webhook?.GetBlob(), _btcPayNetworkJsonSerializerSettings);
    }


    public bool DetermineStatusOfSubscription(Subscription subscription)
    {
        var now = DateTimeOffset.UtcNow;
        if (subscription.Payments.Count == 0)
        {
            if (subscription.Status != SubscriptionStatus.Inactive)
            {
                subscription.Status = SubscriptionStatus.Inactive;
                return true;
            }

            return false;
        }

        var newStatus =
            subscription.Payments.Any(history =>
                history.Settled && history.PeriodStart <= now && history.PeriodEnd >= now)
                ? SubscriptionStatus.Active
                : SubscriptionStatus.Inactive;
        if (newStatus != subscription.Status)
        {
            subscription.Status = newStatus;
            return true;
        }

        return false;
    }

    public const string SubscriptionStatusUpdated = "SubscriptionStatusUpdated";
    public const string SubscriptionRenewalRequested = "SubscriptionRenewalRequested";

    public Dictionary<string, string> GetSupportedWebhookTypes()
    {
        return new Dictionary<string, string>
        {
            {SubscriptionStatusUpdated, "A subscription status has been updated"},
            {SubscriptionRenewalRequested, "A subscription has generated a payment request for renewal"}
        };
    }

    public WebhookEvent CreateTestEvent(string type, params object[] args)
    {
        var storeId = args[0].ToString();
        return new WebhookSubscriptionEvent(type, storeId)
        {
            AppId = "__test__" + Guid.NewGuid() + "__test__",
            SubscriptionId = "__test__" + Guid.NewGuid() + "__test__",
            Status = SubscriptionStatus.Active.ToString()
        };
    }

    public class WebhookSubscriptionEvent : StoreWebhookEvent
    {
        public WebhookSubscriptionEvent(string type, string storeId)
        {
            if (!type.StartsWith("subscription", StringComparison.InvariantCultureIgnoreCase))
                throw new ArgumentException("Invalid event type", nameof(type));
            Type = type;
            StoreId = storeId;
        }


        [JsonProperty(Order = 2)] public string AppId { get; set; }

        [JsonProperty(Order = 3)] public string SubscriptionId { get; set; }
        [JsonProperty(Order = 4)] public string Status { get; set; }
        [JsonProperty(Order = 5)] public string PaymentRequestId { get; set; }
        [JsonProperty(Order = 6)] public string Email { get; set; }
    }

    public class SubscriptionWebhookDeliveryRequest(
        string receiptUrl,
        string? webhookId,
        WebhookSubscriptionEvent webhookEvent,
        WebhookDeliveryData? delivery,
        WebhookBlob? webhookBlob,
        BTCPayNetworkJsonSerializerSettings btcPayNetworkJsonSerializerSettings)
        : WebhookSender.WebhookDeliveryRequest(webhookId!, webhookEvent, delivery!, webhookBlob!)
    {
        public override Task<SendEmailRequest?> Interpolate(SendEmailRequest req,
            UIStoresController.StoreEmailRule storeEmailRule)
        {
            if (storeEmailRule.CustomerEmail &&
                MailboxAddressValidator.TryParse(webhookEvent.Email, out var bmb))
            {
                req.Email ??= string.Empty;
                req.Email += $",{bmb}";
            }


            req.Subject = Interpolate(req.Subject);
            req.Body = Interpolate(req.Body);
            return Task.FromResult(req)!;
        }

        private string Interpolate(string str)
        {
            var res = str.Replace("{Subscription.SubscriptionId}", webhookEvent.SubscriptionId)
                .Replace("{Subscription.Status}", webhookEvent.Status)
                .Replace("{Subscription.PaymentRequestId}", webhookEvent.PaymentRequestId)
                .Replace("{Subscription.AppId}", webhookEvent.AppId);

            return res;
        }
    }
}