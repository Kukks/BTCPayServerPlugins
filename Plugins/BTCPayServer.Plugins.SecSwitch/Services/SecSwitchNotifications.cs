using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Services.Notifications;

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
    public sealed class Handler : NotificationHandler<SecSwitchNotification>
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
            // Hardcoded rather than LinkGenerator-built like core's PluginUpdateNotification.Handler
            // (which resolves controller/action + pathBase: options.RootPath). On an instance served
            // under a non-root path prefix (BTCPayServerOptions.RootPath set), this absolute path
            // resolves against the web server root and 404s instead of landing on the prefixed route.
            // Fixing that means this Handler must take LinkGenerator + BTCPayServerOptions as
            // constructor dependencies (Handler classes ARE DI-constructed - core registers its own
            // via services.AddSingleton<INotificationHandler, Handler>()) - deferred and flagged for
            // review rather than changed here, since every test in this file constructs Handler with
            // "new SecSwitchNotification.Handler()".
            vm.ActionLink = "/plugins/secswitch/audit";
        }
    }
}
