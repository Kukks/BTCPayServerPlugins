using System.Reflection;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Plugins.SecSwitch.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace BTCPayServer.Plugins.SecSwitch.Tests;

public class ControllerRoutingTests
{
    [Fact]
    public void Controller_is_admin_only()
    {
        var authorize = typeof(SecSwitchController).GetCustomAttributes<AuthorizeAttribute>().ToList();
        Assert.Contains(authorize, a => a.Policy == Policies.CanModifyServerSettings);

        // Fix-round strengthening (Task 12 review, Finding M5): the policy alone does not pin the
        // authentication scheme - without this, AuthenticationSchemes.Cookie could be silently
        // widened or dropped (e.g. to also/instead accept an API-key scheme) with this test still
        // green, even though the policy assertion above still passes.
        Assert.Contains(authorize, a =>
            a.Policy == Policies.CanModifyServerSettings &&
            a.AuthenticationSchemes == AuthenticationSchemes.Cookie);
    }

    [Fact]
    public void Controller_is_routed_under_plugins_secswitch()
    {
        var route = typeof(SecSwitchController).GetCustomAttribute<RouteAttribute>();
        Assert.NotNull(route);
        Assert.Equal("~/plugins/secswitch", route!.Template);
    }

    // Final whole-branch review, Finding M8: a name-only `Expected_actions_exist` theory used to sit
    // here. It carried the solution's ONLY analyzer warning (xUnit2030), its own comment conceded it
    // "would stay green even if [HttpGet(\"audit\")] were deleted", and every case it covered is a
    // strict subset of the verb+template theory below - which pins the method name AND the verb AND
    // the route template. Deleted rather than silenced: a weaker duplicate of a stronger test is not
    // worth a suppression.
    //
    // Fix-round strengthening (Task 12 review, Finding M5): this theory pins verb AND route template
    // together for every action the controller exposes, so either one drifting - or an action losing
    // its route attribute entirely - fails the test.
    [Theory]
    [InlineData("Settings", "GET", "")]
    [InlineData("Settings", "POST", "")]
    [InlineData("Audit", "GET", "audit")]
    [InlineData("SuppressConfirm", "GET", "suppress")]
    [InlineData("Suppress", "POST", "suppress")]
    [InlineData("UnsuppressConfirm", "GET", "unsuppress")]
    [InlineData("Unsuppress", "POST", "unsuppress")]
    [InlineData("Verify", "GET", "verify")]
    [InlineData("Verify", "POST", "verify")]
    [InlineData("AddTrustedKey", "POST", "trusted-keys/add")]
    [InlineData("RemoveTrustedKeyConfirm", "GET", "trusted-keys/remove")]
    [InlineData("RemoveTrustedKey", "POST", "trusted-keys/remove")]
    public void Expected_actions_exist_with_the_correct_verb_and_route(string action, string verb, string template)
    {
        var methods = typeof(SecSwitchController).GetMethods().Where(m => m.Name == action).ToList();
        Assert.NotEmpty(methods);
        Assert.Contains(methods, m =>
        {
            var routeTemplate = verb switch
            {
                "GET" => m.GetCustomAttribute<HttpGetAttribute>()?.Template,
                "POST" => m.GetCustomAttribute<HttpPostAttribute>()?.Template,
                _ => throw new ArgumentOutOfRangeException(nameof(verb), verb, "Only GET/POST are used by this controller.")
            };
            return routeTemplate == template;
        });
    }

    [Fact]
    public void State_changing_actions_validate_the_antiforgery_token()
    {
        var posts = typeof(SecSwitchController).GetMethods()
            .Where(m => m.GetCustomAttribute<HttpPostAttribute>() is not null)
            .ToList();
        Assert.NotEmpty(posts);
        Assert.All(posts, m =>
            Assert.NotNull(m.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>()));
    }
}
