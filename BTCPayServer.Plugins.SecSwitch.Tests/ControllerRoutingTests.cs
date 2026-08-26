using System.Reflection;
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
    }

    [Fact]
    public void Controller_is_routed_under_plugins_secswitch()
    {
        var route = typeof(SecSwitchController).GetCustomAttribute<RouteAttribute>();
        Assert.NotNull(route);
        Assert.Equal("~/plugins/secswitch", route!.Template);
    }

    [Theory]
    [InlineData("Settings")]
    [InlineData("Audit")]
    [InlineData("Verify")]
    [InlineData("Suppress")]
    public void Expected_actions_exist(string action)
        => Assert.NotEmpty(typeof(SecSwitchController).GetMethods().Where(m => m.Name == action));

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
