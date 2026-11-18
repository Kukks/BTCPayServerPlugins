using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Configuration;
using BTCPayServer.Plugins.SecSwitch.Controllers;
using BTCPayServer.Services.Notifications;
using Microsoft.AspNetCore.Routing;

namespace BTCPayServer.Plugins.SecSwitch.Services;

/// <summary>
/// Persisted admin notification for an advisory. BTCPay Server reconstructs it from a JSON blob.
/// </summary>
public sealed class SecSwitchNotification : BaseNotification
{
    const string TypeName = "secswitch";

    public SecSwitchNotification() { }

    public string AdvisoryId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Severity { get; set; } = "";
    public string Outcome { get; set; } = "";
    public bool NeedsDecision { get; set; }

    public override string Identifier => TypeName;
    public override string NotificationType => TypeName;

    public sealed class Handler(LinkGenerator linkGenerator, BTCPayServerOptions options)
        : NotificationHandler<SecSwitchNotification>
    {
        public override string NotificationType => TypeName;

        public override (string identifier, string name)[] Meta => [(TypeName, "Security advisory")];

        protected override void FillViewModel(SecSwitchNotification notification, NotificationViewModel vm)
        {
            vm.Identifier = notification.Identifier;
            vm.Type = notification.NotificationType;
            // The BTCPay notification bell renders plain text. Keep the outcome in the linked audit.
            var summary = notification.NeedsDecision ? "⚠️ Review security advisory" : "SecSwitch took action";
            var title = ActionExecutor.Sanitize(notification.Title?.Trim(), maxLength: 88).Trim();
            vm.Body = title.Length == 0 ? summary : $"{summary} · {title}";
            vm.ActionLink = linkGenerator.GetPathByAction(
                action: nameof(SecSwitchController.Audit),
                controller: "SecSwitch",
                values: null,
                pathBase: options.RootPath);
        }
    }
}
