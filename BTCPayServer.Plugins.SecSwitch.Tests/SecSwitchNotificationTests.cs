using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Plugins.SecSwitch.Services;
using Xunit;

namespace BTCPayServer.Plugins.SecSwitch.Tests;

public class SecSwitchNotificationTests
{
    [Fact]
    public void Has_a_parameterless_constructor_for_blob_deserialisation()
    {
        // BTCPayServer round-trips notifications through a typed JSON blob; without this it throws at runtime.
        Assert.NotNull(typeof(SecSwitchNotification).GetConstructor(Type.EmptyTypes));
    }

    [Fact]
    public void Identifier_and_type_are_stable()
    {
        var n = new SecSwitchNotification();
        Assert.Equal("secswitch", n.Identifier);
        Assert.Equal("secswitch", n.NotificationType);
    }

    [Fact]
    public void Body_mentions_the_advisory_and_the_outcome()
    {
        var n = new SecSwitchNotification
        {
            AdvisoryId = "a1", Title = "Stored XSS", Severity = "High",
            Outcome = "Queued disable of Plug", NeedsDecision = false
        };
        var vm = new NotificationViewModel();
        ((INotificationHandler)new SecSwitchNotification.Handler()).FillViewModel(n, vm);

        Assert.Contains("Stored XSS", vm.Body);
        Assert.Contains("Queued disable of Plug", vm.Body);
        Assert.Equal("secswitch", vm.Type);
    }

    [Fact]
    public void Decision_required_notifications_are_marked_as_such()
    {
        var n = new SecSwitchNotification
        {
            AdvisoryId = "a1", Title = "Stored XSS", Severity = "High",
            Outcome = "Manual mode", NeedsDecision = true
        };
        var vm = new NotificationViewModel();
        ((INotificationHandler)new SecSwitchNotification.Handler()).FillViewModel(n, vm);
        Assert.Contains("action required", vm.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FillViewModel_never_throws_when_every_property_is_null_or_empty()
    {
        // Simulates a hostile/malformed blob deserialised with nulls in non-nullable string
        // properties (the same idiom ActionExecutorTests.cs uses for Advisory.Identifier). A
        // throwing handler here would break the admin's entire notification list, not just this entry.
        var n = new SecSwitchNotification
        {
            AdvisoryId = null!, Title = null!, Severity = null!, Outcome = null!, NeedsDecision = false
        };
        var vm = new NotificationViewModel();
        var ex = Record.Exception(() => ((INotificationHandler)new SecSwitchNotification.Handler()).FillViewModel(n, vm));
        Assert.Null(ex);
        Assert.NotNull(vm.Body);
    }

    [Fact]
    public void Sanitizes_control_characters_and_caps_length_before_reaching_the_body()
    {
        // Title comes from a signed-but-still-attacker-authored advisory document; the signature
        // proves provenance, not that the free-text field is short or clean. Outcome can embed a
        // caught exception's Message. Neither may reach vm.Body unbounded or with control characters.
        // Built from numeric char codes rather than literal/escaped characters in source: U+2028
        // (line separator) is visually indistinguishable from a plain space in most editors (same
        // reasoning as ActionExecutor.Sanitize's own comment) and a literal copy is easy to corrupt.
        var bel = (char)0x07;
        var lineSeparator = (char)0x2028;
        var hostileTitle = "Click" + bel + "Here" + lineSeparator + "Now" + new string('x', 500);
        var n = new SecSwitchNotification
        {
            AdvisoryId = "a1", Title = hostileTitle, Severity = "High", Outcome = "ok", NeedsDecision = false
        };
        var vm = new NotificationViewModel();
        ((INotificationHandler)new SecSwitchNotification.Handler()).FillViewModel(n, vm);

        Assert.DoesNotContain(bel, vm.Body);
        Assert.DoesNotContain(lineSeparator, vm.Body);
        Assert.True(vm.Body.Length < hostileTitle.Length);
    }
}
