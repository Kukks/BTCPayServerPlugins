using System.Security.Claims;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Client;
using BTCPayServer.Configuration;
using BTCPayServer.Plugins.SecSwitch.Models;
using BTCPayServer.Plugins.SecSwitch.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BTCPayServer.Plugins.SecSwitch.Tests;

public class LockdownTests : IDisposable
{
    readonly FakeSettingsRepository _settings = new();
    readonly LedgerStore _ledger;
    readonly LockdownGate _gate;
    readonly FakeAuthentication _authentication = new();
    readonly FakeAuthorization _authorization = new();
    readonly DirectoryInfo _webRoot = Directory.CreateTempSubdirectory("secswitch-wwwroot-");
    readonly FakeEnvironment _environment;

    public LockdownTests()
    {
        _ledger = new LedgerStore(_settings);
        _gate = new LockdownGate(_settings, _ledger, NullLogger<LockdownGate>.Instance);
        foreach (var file in new[] { "main/site.css", "vendor/jquery/jquery.js", "img/logo.svg", "fonts/a.woff2" })
        {
            var path = Path.Combine(_webRoot.FullName, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "x");
        }
        _environment = new FakeEnvironment(_webRoot.FullName);
    }

    public void Dispose()
    {
        (_environment.WebRootFileProvider as IDisposable)?.Dispose();
        _webRoot.Delete(recursive: true);
    }

    Task Configure(bool enabled = true, bool autoApply = true)
        => _settings.UpdateSetting(new SecSwitchSettings { Enabled = enabled, AutoApply = autoApply });

    Task Lock(bool suppressed = false) => _ledger.RecordAsync(
        new LedgerEntry { AdvisoryId = "core-1", Status = LedgerStatus.Acted, LockedDown = true, Suppressed = suppressed });

    async Task LockDown()
    {
        await Configure();
        await Lock();
        await _gate.RefreshAsync();
    }

    async Task<(DefaultHttpContext Context, bool Reached)> Send(string path, string rootPath = "/", string method = "GET")
    {
        var provider = new ServiceCollection()
            .AddSingleton<IAuthenticationService>(_authentication)
            .AddSingleton<IAuthorizationService>(_authorization)
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Path = path;
        context.Request.Method = method;
        context.Response.Body = new MemoryStream();
        var reached = false;
        var middleware = new LockdownMiddleware(
            _ => { reached = true; return Task.CompletedTask; }, _gate, new BTCPayServerOptions { RootPath = rootPath }, _environment);

        await middleware.InvokeAsync(context);
        return (context, reached);
    }

    static async Task<string> Body(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return await new StreamReader(context.Response.Body).ReadToEndAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task The_gate_is_open_without_a_locking_entry()
    {
        await Configure();

        Assert.False(await _gate.IsActiveAsync());
    }

    [Fact]
    public async Task The_gate_locks_an_enabled_automatic_instance_that_has_a_locking_entry()
    {
        await LockDown();

        Assert.True(await _gate.IsActiveAsync());
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public async Task The_gate_stays_open_when_disabled_in_manual_mode_or_when_the_entry_is_suppressed(
        bool enabled, bool autoApply, bool suppressed)
    {
        await Configure(enabled, autoApply);
        await Lock(suppressed);
        await _gate.RefreshAsync();

        Assert.False(await _gate.IsActiveAsync());
    }

    [Fact]
    public async Task The_gate_serves_cached_state_until_it_is_refreshed()
    {
        await Configure();
        Assert.False(await _gate.IsActiveAsync());

        await Lock();
        Assert.False(await _gate.IsActiveAsync());

        await _gate.RefreshAsync();
        Assert.True(await _gate.IsActiveAsync());
    }

    [Fact]
    public async Task Overlapping_refreshes_cannot_let_a_stale_read_overwrite_a_newer_one()
    {
        var gated = new GatedSettings(_settings);
        var gate = new LockdownGate(gated, new LedgerStore(gated), NullLogger<LockdownGate>.Instance);
        await _settings.UpdateSetting(new SecSwitchSettings { Enabled = true });
        await Lock();

        var stale = gate.RefreshAsync();
        await gated.FirstReadTaken;
        await _settings.UpdateSetting(new SecSwitchSettings { Enabled = false });
        var fresh = gate.RefreshAsync();
        gated.Release();
        await Task.WhenAll(stale, fresh);

        Assert.False(await gate.IsActiveAsync());
    }

    sealed class GatedSettings(ISettingsRepository inner) : ISettingsRepository
    {
        readonly TaskCompletionSource _firstReadTaken = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int _reads;

        public Task FirstReadTaken => _firstReadTaken.Task;

        public void Release() => _release.TrySetResult();

        // The first settings read takes its snapshot and then waits, like a refresh that is slow to finish.
        public async Task<T?> GetSettingAsync<T>(string? name = null) where T : class
        {
            var snapshot = await inner.GetSettingAsync<T>(name);
            if (typeof(T) == typeof(SecSwitchSettings) && Interlocked.Increment(ref _reads) == 1)
            {
                _firstReadTaken.TrySetResult();
                await _release.Task;
            }
            return snapshot;
        }

        public Task UpdateSetting<T>(T obj, string? name = null) where T : class => inner.UpdateSetting(obj, name);

        public Task<T> WaitSettingsChanged<T>(CancellationToken cancellationToken = default) where T : class
            => throw new NotSupportedException();
    }

    [Fact]
    public async Task An_open_gate_passes_every_request_through()
    {
        await Configure();

        var (context, reached) = await Send("/api/v1/stores");

        Assert.True(reached);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("/", "GET")]
    [InlineData("/api/v1/stores", "GET")]
    [InlineData("/i/abc", "GET")]
    [InlineData("/loginx", "GET")]
    [InlineData("/api/v1/data.json", "GET")]
    [InlineData("/x.js/evil", "GET")]
    [InlineData("/apps/abc/pos/x.js", "GET")]
    [InlineData("/apps/abc/pos/x.js", "POST")]
    [InlineData("/apps/abc/pos/x.css", "GET")]
    [InlineData("/main/missing.css", "GET")]
    [InlineData("/main", "GET")]
    public async Task A_locked_gate_answers_503_to_anonymous_requests_including_routes_that_only_look_like_files(
        string path, string method)
    {
        await LockDown();

        var (context, reached) = await Send(path, method: method);

        Assert.False(reached);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal("3600", context.Response.Headers.RetryAfter.ToString());
        Assert.Contains("offline", await Body(context));
    }

    [Theory]
    [InlineData("/login", "GET")]
    [InlineData("/LOGIN", "GET")]
    [InlineData("/login", "POST")]
    [InlineData("/login/2fa", "POST")]
    [InlineData("/logout", "GET")]
    [InlineData("/main/site.css", "GET")]
    [InlineData("/main/site.css", "HEAD")]
    [InlineData("/vendor/jquery/jquery.js", "GET")]
    [InlineData("/img/logo.svg", "GET")]
    [InlineData("/fonts/a.woff2", "GET")]
    [InlineData("/lnurlauth/login-check", "GET")]
    [InlineData("/lnurlauth/login-callback", "GET")]
    [InlineData("/LnurlAuth/Login-Callback", "GET")]
    public async Task A_locked_gate_still_serves_the_sign_in_flow_and_real_static_files(string path, string method)
    {
        await LockDown();

        var (_, reached) = await Send(path, method: method);

        Assert.True(reached);
    }

    [Theory]
    [InlineData("/main/site.css", "POST")]
    [InlineData("/main/site.css", "PUT")]
    [InlineData("/lnurlauth/login-check", "POST")]
    [InlineData("/lnurlauth/login-callback/extra", "GET")]
    [InlineData("/lnurlauth/register", "GET")]
    [InlineData("/lnurlauth/1/delete", "POST")]
    public async Task A_locked_gate_refuses_other_methods_and_other_lnurl_auth_routes(string path, string method)
    {
        await LockDown();

        var (_, reached) = await Send(path, method: method);

        Assert.False(reached);
    }

    [Fact]
    public async Task A_locked_gate_lets_a_server_admin_through_using_the_cookie_scheme()
    {
        await LockDown();
        _authentication.Principal = new ClaimsPrincipal(new ClaimsIdentity(AuthenticationSchemes.Cookie));
        _authorization.Allow = true;

        var (_, reached) = await Send("/server/plugins");

        Assert.True(reached);
        Assert.Equal(AuthenticationSchemes.Cookie, _authentication.RequestedScheme);
        Assert.Equal(Policies.CanModifyServerSettings, _authorization.RequestedPolicy);
    }

    [Fact]
    public async Task A_locked_gate_blocks_an_authenticated_user_who_is_not_a_server_admin()
    {
        await LockDown();
        _authentication.Principal = new ClaimsPrincipal(new ClaimsIdentity(AuthenticationSchemes.Cookie));
        _authorization.Allow = false;

        var (context, reached) = await Send("/stores");

        Assert.False(reached);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("/btcpay/login/2fa", true)]
    [InlineData("/btcpay/main/site.css", true)]
    [InlineData("/btcpay/api/v1/stores", false)]
    [InlineData("/btcpay/apps/abc/pos/x.js", false)]
    public async Task The_root_path_prefix_is_ignored_when_matching_exemptions(string path, bool exempt)
    {
        await LockDown();

        var (_, reached) = await Send(path, rootPath: "/btcpay");

        Assert.Equal(exempt, reached);
    }

    [Theory]
    [InlineData("/", "/login")]
    [InlineData("/btcpay", "/btcpay/login")]
    public async Task The_refusal_tells_an_admin_where_to_sign_in(string rootPath, string loginPath)
    {
        await LockDown();

        var (context, _) = await Send("/stores", rootPath);

        Assert.Contains($"sign in at {loginPath}.", await Body(context));
    }

    [Fact]
    public async Task The_startup_filter_puts_the_middleware_ahead_of_the_rest_of_the_pipeline()
    {
        await LockDown();
        var provider = new ServiceCollection()
            .AddSingleton(_gate)
            .AddSingleton(new BTCPayServerOptions { RootPath = "/" })
            .AddSingleton<IWebHostEnvironment>(_environment)
            .AddSingleton<IAuthenticationService>(_authentication)
            .AddSingleton<IAuthorizationService>(_authorization)
            .BuildServiceProvider();
        var app = new ApplicationBuilder(provider);
        var reached = false;
        new LockdownStartupFilter().Configure(inner => inner.Run(_ => { reached = true; return Task.CompletedTask; }))(app);
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Path = "/";

        await app.Build()(context);

        Assert.False(reached);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
    }

    sealed class FakeEnvironment(string webRoot) : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = webRoot;
        public IFileProvider WebRootFileProvider { get; set; } = new PhysicalFileProvider(webRoot);
        public string ApplicationName { get; set; } = "test";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = webRoot;
        public string EnvironmentName { get; set; } = "Test";
    }

    sealed class FakeAuthentication : IAuthenticationService
    {
        public ClaimsPrincipal? Principal { get; set; }
        public string? RequestedScheme { get; private set; }

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme)
        {
            RequestedScheme = scheme;
            return Task.FromResult(Principal is null
                ? AuthenticateResult.NoResult()
                : AuthenticateResult.Success(new AuthenticationTicket(Principal, scheme ?? "")));
        }

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) => throw new NotSupportedException();
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();
    }

    sealed class FakeAuthorization : IAuthorizationService
    {
        public bool Allow { get; set; }
        public string? RequestedPolicy { get; private set; }

        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName)
        {
            RequestedPolicy = policyName;
            return Task.FromResult(Allow ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        }

        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements)
            => throw new NotSupportedException();
    }
}
