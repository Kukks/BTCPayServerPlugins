using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.HostedServices;
using BTCPayServer.Plugins.Emails.HostedServices;
using BTCPayServer.Plugins.Webhooks;
using BTCPayServer.Services;
using BTCPayServer.Services.Apps;
using BTCPayServer.Services.PaymentRequests;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Subscriptions;

public class SubscriptionService : EventHostedServiceBase
{
    private readonly AppService _appService;
    private readonly PaymentRequestRepository _paymentRequestRepository;
    private readonly LinkGenerator _linkGenerator;

    private const string ReferenceIdPrefix = "subscription";
    private const char ReferenceIdSeparator = '|';

    public static string EncodeReferenceId(string appId, string? subscriptionId)
        => subscriptionId is null
            ? $"{ReferenceIdPrefix}{ReferenceIdSeparator}{appId}"
            : $"{ReferenceIdPrefix}{ReferenceIdSeparator}{appId}{ReferenceIdSeparator}{subscriptionId}";

    public static bool TryParseReferenceId(string? referenceId, out string appId, out string? subscriptionId)
    {
        appId = null!;
        subscriptionId = null;
        if (referenceId is null) return false;
        var parts = referenceId.Split(ReferenceIdSeparator);
        if (parts.Length < 2 || parts[0] != ReferenceIdPrefix) return false;
        appId = parts[1];
        subscriptionId = parts.Length >= 3 ? parts[2] : null;
        return true;
    }



    public SubscriptionService(EventAggregator eventAggregator,
        ILogger<SubscriptionService> logger,
        AppService appService,
        PaymentRequestRepository paymentRequestRepository,
        LinkGenerator linkGenerator) : base(eventAggregator, logger)
    {
        _appService = appService;
        _paymentRequestRepository = paymentRequestRepository;
        _linkGenerator = linkGenerator;
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
                Status = PaymentRequestStatus.Pending,
                Created = DateTimeOffset.UtcNow, Archived = false,
                Amount = settings.Price,
                Currency = settings.Currency,
                Title = $"{settings.SubscriptionName} Subscription Reactivation",
                Expiry = DateTimeOffset.UtcNow.AddDays(1),
                ReferenceId = EncodeReferenceId(appId, subscriptionId),
            };
            pr.SetBlob(new PaymentRequestBlob()
            {
                Description = settings.Description,
                RequestBaseUrl = lastBlob.RequestBaseUrl,
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
                                    Status = PaymentRequestStatus.Pending,
                                    Created = DateTimeOffset.UtcNow, Archived = false,
                                    Amount = settings.Price,
                                    Currency = settings.Currency,
                                    Title = $"{settings.SubscriptionName} Subscription Renewal",
                                    Expiry = currentPeriod.PeriodEnd,
                                    ReferenceId = EncodeReferenceId(app.Id, subscription.Key),
                                };
                                pr.SetBlob(new PaymentRequestBlob()
                                {
                                    Description = settings.Description,
                                    RequestBaseUrl = lastBlob.RequestBaseUrl,
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
                        foreach (var changedSubscription in changedSubscriptions)
                        {
                            EventAggregator.Publish(CreateSubscriptionStatusUpdatedEvent(app.Id, app.StoreDataId,
                                changedSubscription.Key, changedSubscription.Value.Status, null, changedSubscription.Value.Email));
                        }
                    }
                }

                foreach (var deliverRequest in deliverRequests)
                {
                    EventAggregator.Publish(CreateSubscriptionRenewalRequestedEvent(app.Id,
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
                if (!TryParseReferenceId(paymentRequestStatusUpdated.Data.ReferenceId, out var subscriptionAppId, out var subscriptionIdFromRef))
                {
                    return;
                }

                var isNew = subscriptionIdFromRef is null;

                if (isNew && paymentRequestStatusUpdated.Data.Status !=
                    PaymentRequestStatus.Completed)
                {
                    return;
                }

                if (paymentRequestStatusUpdated.Data.Status == PaymentRequestStatus.Completed)
                {
                    var blob = paymentRequestStatusUpdated.Data.GetBlob();
                    var email = blob.Email ?? blob.FormResponse?["buyerEmail"]?.Value<string>();
                    await HandlePaidSubscription(subscriptionAppId, subscriptionIdFromRef, paymentRequestStatusUpdated.Data.Id,
                        email);
                }
                else if (!isNew)
                {
                    await HandleUnSettledSubscription(subscriptionAppId, subscriptionIdFromRef,
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
                EventAggregator.Publish(CreateSubscriptionStatusUpdatedEvent(app.Id, app.StoreDataId,
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
        // Update ReferenceId to include subscriptionId if not already set
        if (paymentRequest.ReferenceId != EncodeReferenceId(appId, subscriptionId))
        {
            paymentRequest.ReferenceId = EncodeReferenceId(appId, subscriptionId);
        }
        var blob = paymentRequest.GetBlob();
        var path = _linkGenerator.GetPathByAction("ViewSubscription", "Subscription", new {appId, id = subscriptionId});
        Uri? url = null;
        if (blob.RequestBaseUrl is not null && path is not null)
        {
            url = new Uri(new Uri(blob.RequestBaseUrl), path);
        }
        if (url is not null && !blob.Description.Contains(url.ToString()))
        {
            var subscriptionHtml =
                "<div class=\"d-flex justify-content-center mt-4\"><a class=\"btn btn-primary\" href=\"" + url +
                "\">View Subscription</a></div>";
            blob.Description += subscriptionHtml;
            paymentRequest.SetBlob(blob);
            await _paymentRequestRepository.CreateOrUpdatePaymentRequest(paymentRequest);
        }
        if (changed)
        {
            EventAggregator.Publish(CreateSubscriptionStatusUpdatedEvent(app.Id, app.StoreDataId,
                subscriptionId, subscription.Status, url?.ToString(), subscription.Email));
        }
    }

    WebhookSubscriptionEvent CreateSubscriptionStatusUpdatedEvent(
        string appId, string storeId, string subscriptionId, SubscriptionStatus status, string? subscriptionUrl,
        string? email)
    {
        return new WebhookSubscriptionEvent(SubscriptionStatusUpdated, storeId)
        {
            AppId = appId,
            SubscriptionId = subscriptionId,
            Status = status.ToString(),
            Email = email
        };
    }

    WebhookSubscriptionEvent CreateSubscriptionRenewalRequestedEvent(
        string appId, string storeId, string subscriptionId, string? subscriptionUrl,
        string? paymentRequestId, string? email)
    {
        return new WebhookSubscriptionEvent(SubscriptionRenewalRequested, storeId)
        {
            AppId = appId,
            SubscriptionId = subscriptionId,
            PaymentRequestId = paymentRequestId,
            Email = email
        };
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

}

public class SubscriptionWebhookTriggerProvider : WebhookTriggerProvider<SubscriptionService.WebhookSubscriptionEvent>
{
    protected override SubscriptionService.WebhookSubscriptionEvent? GetWebhookEvent(SubscriptionService.WebhookSubscriptionEvent evt)
        => evt;

    protected override Task BeforeSending(EmailRuleMatchContext context, WebhookTriggerContext<SubscriptionService.WebhookSubscriptionEvent> webhookTriggerContext)
    {
        var email = webhookTriggerContext.Event.Email;
        if (email != null &&
            context.MatchedRule.GetBTCPayAdditionalData()?.CustomerEmail is true &&
            MailboxAddressValidator.TryParse(email, out var mb))
        {
            context.To.Insert(0, mb);
        }
        return Task.CompletedTask;
    }
}