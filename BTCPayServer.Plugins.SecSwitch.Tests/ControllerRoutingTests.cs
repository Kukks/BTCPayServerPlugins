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
    public void Controller_is_admin_only_and_cookie_authenticated()
    {
        var authorize = typeof(SecSwitchController).GetCustomAttributes<AuthorizeAttribute>().ToList();

        Assert.Contains(authorize, a =>
            a.Policy == Policies.CanModifyServerSettings && a.AuthenticationSchemes == AuthenticationSchemes.Cookie);
    }

    [Fact]
    public void Controller_is_routed_under_plugins_secswitch()
    {
        var route = typeof(SecSwitchController).GetCustomAttribute<RouteAttribute>();
        Assert.Equal("~/plugins/secswitch", route?.Template);
    }

    [Theory]
    [InlineData("Settings", "GET", "")]
    [InlineData("Settings", "POST", "")]
    [InlineData("Audit", "GET", "audit")]
    [InlineData("SuppressConfirm", "GET", "suppress")]
    [InlineData("Suppress", "POST", "suppress")]
    [InlineData("UnsuppressConfirm", "GET", "unsuppress")]
    [InlineData("Unsuppress", "POST", "unsuppress")]
    public void Expected_actions_exist_with_the_correct_verb_and_route(string action, string verb, string template)
    {
        var methods = typeof(SecSwitchController).GetMethods().Where(m => m.Name == action).ToList();
        Assert.Contains(methods, m => verb == "GET"
            ? m.GetCustomAttribute<HttpGetAttribute>()?.Template == template
            : m.GetCustomAttribute<HttpPostAttribute>()?.Template == template);
    }

    [Fact]
    public void State_changing_actions_validate_the_antiforgery_token()
    {
        var posts = typeof(SecSwitchController).GetMethods()
            .Where(m => m.GetCustomAttribute<HttpPostAttribute>() is not null)
            .ToList();

        Assert.NotEmpty(posts);
        Assert.All(posts, m => Assert.NotNull(m.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>()));
    }
}
