using System;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Client;
using BTCPayServer.Configuration;
using BTCPayServer.Plugins.SecSwitch.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.SecSwitch.Services;

/// <summary>In-memory view of "is a core advisory holding this server in lockdown", derived from the ledger.</summary>
public sealed class LockdownGate(ISettingsRepository settingsRepository, LedgerStore ledger, ILogger<LockdownGate> logger)
{
    volatile int _state; // 0 = not loaded yet, 1 = open, 2 = locked down

    public async ValueTask<bool> IsActiveAsync()
    {
        if (_state == 0)
            await RefreshAsync();
        return _state == 2;
    }

    public async Task RefreshAsync()
    {
        try
        {
            var settings = await settingsRepository.GetSettingAsync<SecSwitchSettings>();
            var locked = settings is { Enabled: true, AutoApply: true } &&
                         (await ledger.GetAsync()).Entries.Values.Any(e => e is { LockedDown: true, Suppressed: false });
            _state = locked ? 2 : 1;
        }
        catch (Exception e)
        {
            logger.LogError(e, "SecSwitch could not read its lockdown state; leaving the server reachable");
        }
    }
}

public sealed class LockdownStartupFilter : IStartupFilter
{
    // Registered as a startup filter so the middleware runs ahead of everything else in core's pipeline.
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.UseMiddleware<LockdownMiddleware>();
        next(app);
    };
}

/// <summary>While locked down, answers 503 to everyone except server admins (and the sign-in flow they need).</summary>
public sealed class LockdownMiddleware(
    RequestDelegate next, LockdownGate gate, BTCPayServerOptions options, IWebHostEnvironment environment)
{
    // Anonymous by design: the wallet calls the callback and the login page polls the check, both for LNURL-auth 2FA.
    static readonly PathString[] LnurlAuthLoginPaths = ["/lnurlauth/login-check", "/lnurlauth/login-callback"];

    string LoginPath => (options.RootPath is { Length: > 1 } root ? root : "") + "/login";

    public async Task InvokeAsync(HttpContext context)
    {
        if (await gate.IsActiveAsync() && !IsExempt(context.Request) && !await IsServerAdminAsync(context))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.RetryAfter = "3600";
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync(
                $"This server is offline while a security advisory is being resolved. Server administrators can sign in at {LoginPath}.");
            return;
        }
        await next(context);
    }

    // This runs before core's Map(RootPath), so the root path is still part of the request path here.
    // Static assets are exempt only when a file really exists under wwwroot: matching on a file extension alone
    // would let any route with a free final segment (".../pos/x.js") through.
    bool IsExempt(HttpRequest request)
    {
        var path = request.Path;
        if (options.RootPath is { Length: > 1 } root && path.StartsWithSegments(root, out var remaining))
            path = remaining;

        if (path.StartsWithSegments("/login") || path.StartsWithSegments("/logout"))
            return true;
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
            return false;

        return LnurlAuthLoginPaths.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase)) ||
               (path.HasValue && environment.WebRootFileProvider.GetFileInfo(path.Value).Exists);
    }

    // Authenticates the cookie explicitly: core's UseAuthentication has not run yet at this point.
    static async Task<bool> IsServerAdminAsync(HttpContext context)
    {
        var result = await context.AuthenticateAsync(AuthenticationSchemes.Cookie);
        if (!result.Succeeded || result.Principal is null)
            return false;
        var authorization = context.RequestServices.GetRequiredService<IAuthorizationService>();
        return (await authorization.AuthorizeAsync(result.Principal, Policies.CanModifyServerSettings)).Succeeded;
    }
}
