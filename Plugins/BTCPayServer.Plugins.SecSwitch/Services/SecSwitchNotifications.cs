using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Configuration;
using BTCPayServer.Plugins.SecSwitch.Controllers;
using BTCPayServer.Services.Notifications;
using Microsoft.AspNetCore.Routing;

namespace BTCPayServer.Plugins.SecSwitch.Services;

/// <summary>
/// Admin bell-icon notification for a single advisory outcome: either what SecSwitch already did
/// automatically (queued a disable/update, or made no change) or that manual mode is holding a
/// decision open for the admin. The parameterless constructor and settable properties are
/// load-bearing, not incidental style: BTCPayServer persists and reloads notifications by
/// round-tripping this type through a typed JSON blob (mirrors core's PluginUpdateNotification in
/// BTCPayServer/Plugins/PluginManager/PluginUpdateFetcher.cs), so a missing parameterless
/// constructor throws at deserialisation time rather than at compile time.
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

    /// <summary>
    /// Renders the stored notification into the admin's notification list. Must never throw -
    /// including when every property is null or empty, e.g. a blob deserialised from a partially
    /// written or hand-edited settings row - because a throwing handler breaks the ENTIRE admin
    /// notification list, not just this one entry.
    ///
    /// Title, Severity and Outcome are attacker-influenced: Title is copied verbatim from a
    /// GPG-quorum-SIGNED advisory document, but the signature only proves the ecosystem's trusted
    /// signers agreed to publish it, not that its free-text fields are well-formed or short; Outcome
    /// can embed a caught exception's Message (see ActionExecutor.ExecuteAsync's catch block), which
    /// may carry attacker-influenced or connection-specific text. Both are run through
    /// ActionExecutor.Sanitize before reaching vm.Body so unbounded length or control characters
    /// (including U+2028/U+2029) never reach this persisted, rendered admin surface.
    /// </summary>
    public sealed class Handler(LinkGenerator linkGenerator, BTCPayServerOptions options)
        : NotificationHandler<SecSwitchNotification>
    {
        public override string NotificationType => TypeName;

        public override (string identifier, string name)[] Meta => [(TypeName, "Security advisory")];

        protected override void FillViewModel(SecSwitchNotification notification, NotificationViewModel vm)
        {
            vm.Identifier = notification.Identifier;
            vm.Type = notification.NotificationType;
            var prefix = notification.NeedsDecision ? "Action required" : "Handled";
            var severity = ActionExecutor.Sanitize(notification.Severity);
            var title = ActionExecutor.Sanitize(notification.Title);
            var outcome = ActionExecutor.Sanitize(notification.Outcome);
            vm.Body = $"{prefix}: [{severity}] {title} — {outcome}";
            // LinkGenerator-built, matching core's PluginUpdateNotification.Handler
            // (BTCPayServer/Plugins/PluginManager/PluginUpdateFetcher.cs): resolves the route by
            // controller/action rather than a hardcoded absolute path, and threads pathBase:
            // options.RootPath through so this still lands on the right route on an instance served
            // under a non-root path prefix. GetPathByAction returns null (never throws) if the route
            // cannot be resolved (e.g. a hostile/stub LinkGenerator in a test) - FillViewModel's own
            // "must never throw" contract holds either way.
            vm.ActionLink = linkGenerator.GetPathByAction(
                action: nameof(SecSwitchController.Audit),
                controller: "SecSwitch",
                values: null,
                pathBase: options.RootPath);
        }
    }
}
