using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Configuration;
using BTCPayServer.Plugins.SecSwitch.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.SecSwitch.Services;

/// <summary>In-memory view of "is a core advisory holding this server in lockdown", derived from the ledger.</summary>
public sealed class LockdownGate(LedgerStore ledger, ILogger<LockdownGate> logger)
{
    // Refreshes are serialized so the last one to finish is also the last one to have read: an hourly poll that
    // read the ledger before an admin lifted the lockdown must not write its stale answer after the admin's.
    readonly SemaphoreSlim _refreshing = new(1, 1);
    volatile RequestState? _state;

    sealed class RequestState(bool active)
    {
        public bool Active { get; } = active;
        public CancellationTokenSource OrdinaryRequests { get; } = new();
    }

    public async ValueTask<bool> IsActiveAsync() => (await GetRequestStateAsync()).Active;

    internal async ValueTask<(bool Active, CancellationToken OrdinaryRequests)> GetRequestStateAsync()
    {
        if (_state is null)
            await RefreshAsync(onlyIfNotLoaded: true);

        var state = _state;
        return state is null ? (true, CancellationToken.None) : (state.Active, state.OrdinaryRequests.Token);
    }

    public static bool HasActiveLockdown(SecSwitchSettings? settings, SecSwitchLedger ledger) =>
        settings is { Enabled: true, AutoApply: true } &&
        ledger.Entries.Values.Any(e => e is { LockedDown: true, Suppressed: false });

    public Task<bool> RefreshAsync() => RefreshAsync(onlyIfNotLoaded: false);

    internal async Task ActivateAsync()
    {
        await _refreshing.WaitAsync();
        try
        {
            SetState(active: true);
        }
        finally
        {
            _refreshing.Release();
        }
    }

    async Task<bool> RefreshAsync(bool onlyIfNotLoaded)
    {
        await _refreshing.WaitAsync();
        try
        {
            if (onlyIfNotLoaded && _state is not null)
                return true;
            var settings = await ledger.GetPolicyAsync();
            var locked = settings is { Enabled: true, AutoApply: true } &&
                         HasActiveLockdown(settings, await ledger.GetAsync());
            SetState(locked);
            return true;
        }
        catch (Exception e)
        {
            logger.LogError(e, "SecSwitch could not finish refreshing its lockdown state");
            return false;
        }
        finally
        {
            _refreshing.Release();
        }
    }

    void SetState(bool active)
    {
        var previous = _state;
        if (previous?.Active == active)
            return;
        _state = new RequestState(active);
        if (active)
            previous?.OrdinaryRequests.Cancel();
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

/// <summary>Restricts the web app to authentication and admin recovery while locked down.</summary>
public sealed class LockdownMiddleware(
    RequestDelegate next, LockdownGate gate, BTCPayServerOptions options, IWebHostEnvironment environment)
{
    static readonly PathString[] SignInReadPaths =
        ["/login", "/login/second-login", "/login/authenticator", "/lnurlauth/login-check", "/lnurlauth/login-callback"];
    static readonly PathString[] SignInPostPaths =
        ["/login", "/login/lnurlauth", "/login/passkey/options", "/login/fido2", "/login/authenticator", "/logout"];
    static readonly FileExtensionContentTypeProvider AssetTypes = new();

    string RootPrefix => options.RootPath is { Length: > 1 } root ? root : "";
    string AuditPath => RootPrefix + "/plugins/secswitch/audit";
    string LoginPath => QueryHelpers.AddQueryString(RootPrefix + "/login", "returnUrl", AuditPath);

    public async Task InvokeAsync(HttpContext context)
    {
        var (active, ordinaryRequests) = await gate.GetRequestStateAsync();
        var path = GetRelativePath(context.Request.Path);
        var read = HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method);
        var post = HttpMethods.IsPost(context.Request.Method);
        var signIn = read && SignInReadPaths.Contains(path) || post && SignInPostPaths.Contains(path);
        var recovery = IsRecoveryRequest(path, read, post);
        if (!active)
        {
            using var abort = signIn || recovery ? default : ordinaryRequests.Register(context.Abort);
            if (signIn || recovery || !ordinaryRequests.IsCancellationRequested)
                await next(context);
            return;
        }

        if (signIn || recovery && await IsServerAdminAsync(context))
        {
            await next(context);
            return;
        }

        if (read && (path == "/" || path == "/plugins/secswitch") && await IsServerAdminAsync(context))
        {
            context.Response.Redirect(AuditPath);
            return;
        }

        if (read && await TryServeAssetAsync(context, path))
            return;

        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.RetryAfter = "3600";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.ContentType = "text/plain; charset=utf-8";
        if (!HttpMethods.IsHead(context.Request.Method))
            await context.Response.WriteAsync(
                $"BTCPay Server is temporarily unavailable while a security advisory is being resolved. Server administrators can sign in at {LoginPath}.");
    }

    static bool IsRecoveryRequest(PathString path, bool read, bool post) =>
        read && path == "/plugins/secswitch/audit" ||
        (read || post) && path == "/plugins/secswitch/suppress" ||
        post && path == "/plugins/secswitch/update";

    PathString GetRelativePath(PathString path)
    {
        if (RootPrefix.Length == 0)
            return path;
        if (!path.StartsWithSegments(RootPrefix, out var remaining))
            return PathString.Empty;
        return remaining.HasValue ? remaining : "/";
    }

    // Serve real web-root files directly so a route resembling an asset cannot reach the application.
    async Task<bool> TryServeAssetAsync(HttpContext context, PathString path)
    {
        if (path.Value is not { } value || !AssetTypes.TryGetContentType(value, out var contentType))
            return false;
        var file = environment.WebRootFileProvider.GetFileInfo(value);
        if (!file.Exists || file.IsDirectory)
            return false;

        context.Response.ContentType = contentType;
        context.Response.ContentLength = file.Length;
        if (!HttpMethods.IsHead(context.Request.Method))
            await context.Response.SendFileAsync(file, context.RequestAborted);
        return true;
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
