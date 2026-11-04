using BTCPayServer.Plugins.SecSwitch.Models;
using BTCPayServer.Plugins.SecSwitch.Services;
using Xunit;
using static BTCPayServer.Plugins.SecSwitch.Tests.Fixtures;

namespace BTCPayServer.Plugins.SecSwitch.Tests;

public class PolicyResolverTests
{
    static PolicyDecision Resolve(Advisory advisory, InstanceState state, SecSwitchSettings? settings = null)
        => PolicyResolver.Resolve(advisory, state, settings ?? Settings());

    [Fact]
    public void Disabled_never_acts()
    {
        var decision = Resolve(Adv(), State(), Settings(enabled: false));

        Assert.Equal(SecSwitchAction.None, decision.Action);
    }

    [Fact]
    public void Not_applicable_advisory_yields_no_action_and_carries_the_reason()
    {
        var decision = Resolve(Adv(), State("2.0.0"));

        Assert.Equal(SecSwitchAction.None, decision.Action);
        Assert.Contains("not affected", decision.Reason);
    }

    [Fact]
    public void Unknown_core_version_keeps_the_indeterminate_phrase_in_the_reason()
    {
        var decision = Resolve(Adv(identifier: "BTCPayServer", affected: "<9.0.0"), State(coreVersion: null));

        Assert.Equal(SecSwitchAction.None, decision.Action);
        Assert.Contains(AdvisoryApplicability.IndeterminateVersionPhrase, decision.Reason);
    }

    [Fact]
    public void Plugin_with_a_fix_is_updated_by_default()
    {
        Assert.Equal(SecSwitchAction.UpdatePlugin, Resolve(Adv(fixedVersion: "1.2.3"), State()).Action);
    }

    [Fact]
    public void Plugin_without_a_fix_is_disabled()
    {
        Assert.Equal(SecSwitchAction.DisablePlugin, Resolve(Adv(), State()).Action);
    }

    [Fact]
    public void Preferring_disable_overrides_an_available_fix()
    {
        var decision = Resolve(Adv(fixedVersion: "1.2.3"), State(), Settings(preferUpdate: false));

        Assert.Equal(SecSwitchAction.DisablePlugin, decision.Action);
    }

    [Theory]
    [InlineData(true, true, SecSwitchAction.UpdateCore)]
    [InlineData(true, false, SecSwitchAction.Lockdown)]
    [InlineData(false, true, SecSwitchAction.Lockdown)]
    [InlineData(false, false, SecSwitchAction.Lockdown)]
    public void Core_updates_only_with_a_fix_and_host_support_otherwise_locks_down(bool hasFix, bool canUpdate, SecSwitchAction expected)
    {
        var advisory = Adv(identifier: "BTCPayServer", affected: "<9.0.0", fixedVersion: hasFix ? "9.0.0" : null);

        Assert.Equal(expected, Resolve(advisory, State(canUpdateCore: canUpdate)).Action);
    }

    [Fact]
    public void Preferring_disable_does_not_stop_a_core_update()
    {
        var advisory = Adv(identifier: "BTCPayServer", affected: "<9.0.0", fixedVersion: "9.0.0");

        Assert.Equal(SecSwitchAction.UpdateCore, Resolve(advisory, State(canUpdateCore: true), Settings(preferUpdate: false)).Action);
    }

    [Theory]
    [InlineData("Plug", "<1.2.3", "1.2.3", SecSwitchAction.UpdatePlugin)]
    [InlineData("Plug", "<1.2.3", null, SecSwitchAction.DisablePlugin)]
    [InlineData("BTCPayServer", "<9.0.0", null, SecSwitchAction.Lockdown)]
    public void Manual_mode_downgrades_any_action_to_notify_and_keeps_the_intended_one(
        string identifier, string affected, string? fixedVersion, SecSwitchAction intended)
    {
        var decision = Resolve(Adv(identifier: identifier, affected: affected, fixedVersion: fixedVersion), State(), Settings(autoApply: false));

        Assert.Equal(SecSwitchAction.Notify, decision.Action);
        Assert.Equal(intended, decision.IntendedAction);
        Assert.Contains(intended.ToString(), decision.Reason);
    }

    [Theory]
    [InlineData("1.0.9")]
    [InlineData("1.1.0")]
    [InlineData("1.2.0")]
    [InlineData("not a version")]
    public void A_fixed_version_that_would_downgrade_or_stay_affected_is_ignored_and_the_plugin_is_disabled(string fixedVersion)
    {
        var decision = Resolve(Adv(fixedVersion: fixedVersion), State("1.1.0"));

        Assert.Equal(SecSwitchAction.DisablePlugin, decision.Action);
        Assert.Contains("is ignored", decision.Reason);
    }

    [Fact]
    public void A_blank_fixed_version_is_simply_no_fix_and_says_nothing_about_being_ignored()
    {
        var decision = Resolve(Adv(fixedVersion: "  "), State("1.1.0"));

        Assert.Equal(SecSwitchAction.DisablePlugin, decision.Action);
        Assert.DoesNotContain("ignored", decision.Reason);
    }

    [Fact]
    public void The_fix_is_compared_with_the_installed_version_part_for_part()
    {
        const string affected = ">=1.0.0 && <=1.2.3";

        Assert.Equal(SecSwitchAction.DisablePlugin, Resolve(Adv(affected: affected, fixedVersion: "1.2.3"), State("1.2.3.0")).Action);
        Assert.Equal(SecSwitchAction.UpdatePlugin, Resolve(Adv(affected: affected, fixedVersion: "1.2.4"), State("1.2.3.0")).Action);
    }

    [Theory]
    [InlineData("2.4.7")]
    [InlineData("2.4.0")]
    [InlineData("not a version")]
    public void A_core_fix_that_is_not_newer_leaves_the_server_locked_down_even_with_host_support(string fixedVersion)
    {
        var advisory = Adv(identifier: "BTCPayServer", affected: "<9.0.0", fixedVersion: fixedVersion);

        Assert.Equal(SecSwitchAction.Lockdown, Resolve(advisory, State(canUpdateCore: true)).Action);
    }

    [Fact]
    public void An_automatic_decision_has_no_intended_action()
    {
        Assert.Null(Resolve(Adv(), State()).IntendedAction);
    }

    [Theory]
    [InlineData(AdvisorySeverity.Low)]
    [InlineData(AdvisorySeverity.Critical)]
    public void Severity_does_not_gate_the_action(AdvisorySeverity severity)
    {
        Assert.Equal(SecSwitchAction.DisablePlugin, Resolve(Adv(severity: severity), State()).Action);
    }
}
