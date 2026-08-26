using BTCPayServer.Plugins.SecSwitch.Models;
using BTCPayServer.Plugins.SecSwitch.Services;
using Xunit;

namespace BTCPayServer.Plugins.SecSwitch.Tests;

public class PolicyResolverTests
{
    static Advisory Adv(string identifier = "Plug", string affected = ">=1.0.0 && <2.0.0",
        string? fixedVersion = "2.0.0", AdvisorySeverity severity = AdvisorySeverity.Critical) =>
        new() { Id = "x", Identifier = identifier, AffectedVersions = affected,
                FixedVersion = fixedVersion, Severity = severity, Title = "t" };

    static InstanceState State(bool canUseSsh = false, string coreVersion = "2.4.2") =>
        new(new Dictionary<string, Version> { ["Plug"] = Version.Parse("1.5.0") },
            Version.Parse(coreVersion), canUseSsh);

    static SecSwitchSettings Settings(Action<SecSwitchSettings>? tweak = null)
    {
        var s = new SecSwitchSettings { Enabled = true };
        tweak?.Invoke(s);
        return s;
    }

    [Fact]
    public void Disabled_plugin_never_acts()
    {
        var d = PolicyResolver.Resolve(Adv(), State(), Settings(s => s.Enabled = false));
        Assert.Equal(SecSwitchAction.None, d.Action);
    }

    [Fact]
    public void Not_applicable_advisory_yields_no_action()
    {
        var d = PolicyResolver.Resolve(Adv(identifier: "NotInstalled"), State(), Settings());
        Assert.Equal(SecSwitchAction.None, d.Action);
    }

    [Fact]
    public void Indeterminate_installed_version_reason_survives_into_decision()
    {
        // AdvisoryApplicability.IsApplicable returns false with a reason containing
        // "could not be determined" when it resolves an identifier but the installed version is
        // null - an indeterminate case, distinct from a clean not-affected. PolicyResolver must
        // still map this to SecSwitchAction.None (no special-casing here - a later task keys off
        // this exact phrase), but the phrase itself must survive verbatim into the decision's
        // Reason so that later task can find it.
        var state = new InstanceState(
            new Dictionary<string, Version> { ["Plug"] = null! }, Version.Parse("2.4.2"), CanUseSsh: false);
        var d = PolicyResolver.Resolve(Adv(), state, Settings());
        Assert.Equal(SecSwitchAction.None, d.Action);
        Assert.Contains("could not be determined", d.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Revoked_advisory_yields_no_action()
    {
        var a = Adv(); a.Revoked = true;
        var d = PolicyResolver.Resolve(a, State(), Settings());
        Assert.Equal(SecSwitchAction.None, d.Action);
    }

    [Fact]
    public void Plugin_with_fix_updates_by_default()
    {
        var d = PolicyResolver.Resolve(Adv(), State(), Settings());
        Assert.Equal(SecSwitchAction.UpdatePlugin, d.Action);
    }

    [Fact]
    public void Plugin_without_fix_is_disabled()
    {
        var d = PolicyResolver.Resolve(Adv(fixedVersion: null), State(), Settings());
        Assert.Equal(SecSwitchAction.DisablePlugin, d.Action);
    }

    [Fact]
    public void Prefer_disable_setting_overrides_available_fix()
    {
        var d = PolicyResolver.Resolve(Adv(), State(), Settings(s => s.PreferUpdateOverDisable = false));
        Assert.Equal(SecSwitchAction.DisablePlugin, d.Action);
    }

    [Fact]
    public void Manual_mode_downgrades_any_action_to_notify()
    {
        var d = PolicyResolver.Resolve(Adv(), State(), Settings(s => s.AutoApply = false));
        Assert.Equal(SecSwitchAction.Notify, d.Action);
    }

    [Fact]
    public void Notify_only_pin_downgrades_that_identifier_only()
    {
        var settings = Settings(s => s.NotifyOnlyIdentifiers.Add("Plug"));
        Assert.Equal(SecSwitchAction.Notify, PolicyResolver.Resolve(Adv(), State(), settings).Action);

        // The "...only" half of this test's name: a second, unpinned identifier resolved against
        // the very same settings object must still receive its own intended action, not be swept
        // into Notify by the pin meant for "Plug" alone.
        var otherState = new InstanceState(
            new Dictionary<string, Version> { ["Other"] = Version.Parse("1.5.0") },
            Version.Parse("2.4.2"), CanUseSsh: false);
        var otherDecision = PolicyResolver.Resolve(Adv(identifier: "Other"), otherState, settings);
        Assert.Equal(SecSwitchAction.UpdatePlugin, otherDecision.Action);
    }

    [Theory]
    [InlineData(AdvisorySeverity.Critical, SecSwitchAction.UpdatePlugin)]
    [InlineData(AdvisorySeverity.High, SecSwitchAction.UpdatePlugin)]
    [InlineData(AdvisorySeverity.Medium, SecSwitchAction.Notify)]
    [InlineData(AdvisorySeverity.Low, SecSwitchAction.Notify)]
    public void Severity_gate_acts_only_on_critical_and_high(AdvisorySeverity severity, SecSwitchAction expected)
    {
        var d = PolicyResolver.Resolve(Adv(severity: severity), State(), Settings());
        Assert.Equal(expected, d.Action);
    }

    [Fact]
    public void Disabling_the_severity_gate_acts_on_low_severity_too()
    {
        var d = PolicyResolver.Resolve(
            Adv(severity: AdvisorySeverity.Low), State(), Settings(s => s.SeverityGateEnabled = false));
        Assert.Equal(SecSwitchAction.UpdatePlugin, d.Action);
    }

    [Fact]
    public void Core_with_fix_and_ssh_updates_core()
    {
        var d = PolicyResolver.Resolve(
            Adv(identifier: "BTCPayServer", affected: "<2.5.0", fixedVersion: "2.5.0"),
            State(canUseSsh: true), Settings());
        Assert.Equal(SecSwitchAction.UpdateCore, d.Action);
    }

    [Fact]
    public void Core_with_fix_but_no_ssh_shuts_down()
    {
        var d = PolicyResolver.Resolve(
            Adv(identifier: "BTCPayServer", affected: "<2.5.0", fixedVersion: "2.5.0"),
            State(canUseSsh: false), Settings());
        Assert.Equal(SecSwitchAction.ShutdownCore, d.Action);
    }

    // --- Task 13 review, Finding I2 (Important) fix: CheckConfigurationHostedService.StartAsync
    // fires its SSH probe without awaiting it, so CanUseSsh can still be false purely because
    // verification has not finished yet - distinct from SSH never having been configured at all.
    // A fixable core advisory must be DEFERRED in the former case, not resolved as ShutdownCore
    // (SecSwitchMonitor would cache that as the terminal Acted, which no later poll could ever
    // correct back to the real UpdateCore once the probe actually succeeds).

    [Fact]
    public void Core_with_fix_and_ssh_configured_but_not_yet_verified_defers_rather_than_shutting_down()
    {
        var state = new InstanceState(
            new Dictionary<string, Version> { ["Plug"] = Version.Parse("1.5.0") },
            Version.Parse("2.4.2"), CanUseSsh: false, SshVerificationPending: true);

        var d = PolicyResolver.Resolve(
            Adv(identifier: "BTCPayServer", affected: "<2.5.0", fixedVersion: "2.5.0"), state, Settings());

        Assert.Equal(SecSwitchAction.None, d.Action);
        Assert.Contains(PolicyResolver.SshVerificationPendingPhrase, d.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Core_with_fix_and_ssh_genuinely_not_configured_still_shuts_down()
    {
        // Regression guard: SshVerificationPending defaults to false (matches every pre-existing
        // InstanceState construction in this file), so "SSH not configured at all" must still take
        // the ORIGINAL ShutdownCore path, not the new deferred one - the two must never collapse
        // into the same behaviour.
        var d = PolicyResolver.Resolve(
            Adv(identifier: "BTCPayServer", affected: "<2.5.0", fixedVersion: "2.5.0"),
            State(canUseSsh: false), Settings());

        Assert.Equal(SecSwitchAction.ShutdownCore, d.Action);
    }

    [Fact]
    public void Core_without_fix_shuts_down_even_when_ssh_verification_is_pending()
    {
        // The deferral only ever applies to a FIXABLE core advisory - one with no fix at all has
        // nothing to wait for SSH to apply, so it must still shut down immediately regardless of
        // SSH's verification state.
        var state = new InstanceState(
            new Dictionary<string, Version> { ["Plug"] = Version.Parse("1.5.0") },
            Version.Parse("2.4.2"), CanUseSsh: false, SshVerificationPending: true);

        var d = PolicyResolver.Resolve(
            Adv(identifier: "BTCPayServer", affected: "<2.5.0", fixedVersion: null), state, Settings());

        Assert.Equal(SecSwitchAction.ShutdownCore, d.Action);
    }

    [Fact]
    public void Plugin_advisory_is_unaffected_by_ssh_verification_pending()
    {
        // SshVerificationPending only ever matters for core-targeted advisories - a plugin advisory
        // must resolve exactly as it always did regardless of SSH's state.
        var state = new InstanceState(
            new Dictionary<string, Version> { ["Plug"] = Version.Parse("1.5.0") },
            Version.Parse("2.4.2"), CanUseSsh: false, SshVerificationPending: true);

        var d = PolicyResolver.Resolve(Adv(), state, Settings());

        Assert.Equal(SecSwitchAction.UpdatePlugin, d.Action);
    }

    [Fact]
    public void Core_without_fix_shuts_down_even_with_ssh()
    {
        var d = PolicyResolver.Resolve(
            Adv(identifier: "BTCPayServer", affected: "<2.5.0", fixedVersion: null),
            State(canUseSsh: true), Settings());
        Assert.Equal(SecSwitchAction.ShutdownCore, d.Action);
    }

    [Fact]
    public void Decision_always_carries_a_human_readable_reason()
    {
        Assert.NotEmpty(PolicyResolver.Resolve(Adv(), State(), Settings()).Reason);
        Assert.NotEmpty(PolicyResolver.Resolve(Adv(identifier: "Nope"), State(), Settings()).Reason);
    }

    [Fact]
    public void Manual_mode_downgrade_populates_intended_action()
    {
        var d = PolicyResolver.Resolve(Adv(), State(), Settings(s => s.AutoApply = false));
        Assert.Equal(SecSwitchAction.Notify, d.Action);
        Assert.Equal(SecSwitchAction.UpdatePlugin, d.IntendedAction);
    }

    [Fact]
    public void Notify_only_pin_downgrade_populates_intended_action()
    {
        var settings = Settings(s => s.NotifyOnlyIdentifiers.Add("Plug"));
        var d = PolicyResolver.Resolve(Adv(), State(), settings);
        Assert.Equal(SecSwitchAction.Notify, d.Action);
        Assert.Equal(SecSwitchAction.UpdatePlugin, d.IntendedAction);
    }

    [Fact]
    public void Severity_gate_downgrade_populates_intended_action()
    {
        var d = PolicyResolver.Resolve(Adv(severity: AdvisorySeverity.Low), State(), Settings());
        Assert.Equal(SecSwitchAction.Notify, d.Action);
        Assert.Equal(SecSwitchAction.UpdatePlugin, d.IntendedAction);
    }

    [Fact]
    public void Non_downgraded_decision_has_no_intended_action()
    {
        var d = PolicyResolver.Resolve(Adv(), State(), Settings());
        Assert.Equal(SecSwitchAction.UpdatePlugin, d.Action);
        Assert.Null(d.IntendedAction);
    }

    [Fact]
    public void Null_settings_fails_closed_without_throwing()
    {
        var d = PolicyResolver.Resolve(Adv(), State(), null!);
        Assert.Equal(SecSwitchAction.None, d.Action);
        Assert.NotEmpty(d.Reason);
    }

    [Fact]
    public void Null_advisory_fails_closed_without_throwing()
    {
        var d = PolicyResolver.Resolve(null!, State(), Settings());
        Assert.Equal(SecSwitchAction.None, d.Action);
        Assert.NotEmpty(d.Reason);
    }

    [Fact]
    public void Null_state_fails_closed_without_throwing()
    {
        var d = PolicyResolver.Resolve(Adv(), null!, Settings());
        Assert.Equal(SecSwitchAction.None, d.Action);
        Assert.NotEmpty(d.Reason);
    }

    [Fact]
    public void Null_notify_only_identifiers_is_treated_as_no_pins()
    {
        // Reachable once Enabled/AutoApply are both true and the advisory is applicable - deeper
        // in the chain than the other null-input tests, so it needs its own case: a null list must
        // not be dereferenced by Contains(), and must behave exactly like an empty one (no pin).
        var settings = Settings(s => s.NotifyOnlyIdentifiers = null!);
        var d = PolicyResolver.Resolve(Adv(), State(), settings);
        Assert.Equal(SecSwitchAction.UpdatePlugin, d.Action);
    }
}
