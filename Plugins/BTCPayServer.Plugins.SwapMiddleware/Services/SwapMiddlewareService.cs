#nullable enable
using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.SwapMiddleware.Services;

/// <summary>
/// Service for managing swap middleware settings and proxying requests to swap providers.
/// </summary>
public class SwapMiddlewareService
{
    private readonly ISettingsRepository _settingsRepository;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _memoryCache;
    private readonly ILogger<SwapMiddlewareService> _logger;

    private const string SideShiftBaseUrl = "https://sideshift.ai";
    private const string SettingsCacheKey = "SwapMiddlewareSettings";

    public SwapMiddlewareService(
        ISettingsRepository settingsRepository,
        IHttpClientFactory httpClientFactory,
        IMemoryCache memoryCache,
        ILogger<SwapMiddlewareService> logger)
    {
        _settingsRepository = settingsRepository;
        _httpClientFactory = httpClientFactory;
        _memoryCache = memoryCache;
        _logger = logger;
    }

    // === Settings Management ===

    public async Task<SwapMiddlewareSettings> GetSettings()
    {
        return await _memoryCache.GetOrCreateAsync(SettingsCacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);
            return await _settingsRepository.GetSettingAsync<SwapMiddlewareSettings>()
                   ?? new SwapMiddlewareSettings();
        }) ?? new SwapMiddlewareSettings();
    }

    public async Task UpdateSettings(SwapMiddlewareSettings settings)
    {
        await _settingsRepository.UpdateSetting(settings);
        _memoryCache.Remove(SettingsCacheKey);
    }

    // === SideShift Proxy Methods ===

    /// <summary>
    /// Proxy GET request to SideShift /api/v2/coins (cached)
    /// </summary>
    public async Task<ProxyResponse> GetSideShiftCoins(string? userIp)
    {
        var settings = await GetSettings();
        var cacheKey = "sideshift-coins-proxy";

        var cached = await _memoryCache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow =
                TimeSpan.FromMinutes(settings.CacheDurationMinutes > 0 ? settings.CacheDurationMinutes : 5);

            try
            {
                var client = CreateSideShiftClient(userIp);
                var response = await client.GetAsync($"{SideShiftBaseUrl}/api/v2/coins");
                var content = await response.Content.ReadAsStringAsync();

                return new ProxyResponse(
                    response.IsSuccessStatusCode,
                    content,
                    (int)response.StatusCode);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching SideShift coins");
                return new ProxyResponse(false, JsonConvert.SerializeObject(new { error = "Failed to fetch coins" }), 502);
            }
        });

        return cached!;
    }

    /// <summary>
    /// Proxy GET request to SideShift /api/v1/facts (cached)
    /// </summary>
    public async Task<ProxyResponse> GetSideShiftFacts(string? userIp)
    {
        var settings = await GetSettings();
        var cacheKey = "sideshift-facts-proxy";

        var cached = await _memoryCache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow =
                TimeSpan.FromMinutes(settings.CacheDurationMinutes > 0 ? settings.CacheDurationMinutes : 5);

            try
            {
                var client = CreateSideShiftClient(userIp);
                var response = await client.GetAsync($"{SideShiftBaseUrl}/api/v1/facts");
                var content = await response.Content.ReadAsStringAsync();

                return new ProxyResponse(
                    response.IsSuccessStatusCode,
                    content,
                    (int)response.StatusCode);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching SideShift facts");
                return new ProxyResponse(false, JsonConvert.SerializeObject(new { error = "Failed to fetch facts" }), 502);
            }
        });

        return cached!;
    }

    /// <summary>
    /// Proxy POST request to SideShift /api/v2/shifts/variable with affiliateId injection
    /// </summary>
    public async Task<ProxyResponse> CreateSideShiftVariableShift(string requestBody, string? userIp)
    {
        var settings = await GetSettings();

        if (!settings.Enabled)
        {
            return new ProxyResponse(false,
                JsonConvert.SerializeObject(new { error = new { message = "SwapMiddleware is not enabled" } }),
                503);
        }

        if (string.IsNullOrEmpty(settings.SideShiftAffiliateId))
        {
            return new ProxyResponse(false,
                JsonConvert.SerializeObject(new { error = new { message = "SideShift affiliate ID is not configured" } }),
                503);
        }

        try
        {
            // Parse the incoming request and inject affiliateId
            var requestJson = JObject.Parse(requestBody);
            requestJson["affiliateId"] = settings.SideShiftAffiliateId;

            var client = CreateSideShiftClient(userIp);
            var content = new StringContent(
                requestJson.ToString(),
                Encoding.UTF8,
                "application/json");

            var response = await client.PostAsync(
                $"{SideShiftBaseUrl}/api/v2/shifts/variable",
                content);

            var responseContent = await response.Content.ReadAsStringAsync();

            _logger.LogInformation(
                "SideShift variable shift proxied. StatusCode: {StatusCode}",
                response.StatusCode);

            return new ProxyResponse(response.IsSuccessStatusCode, responseContent, (int)response.StatusCode);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Invalid JSON in request body");
            return new ProxyResponse(false,
                JsonConvert.SerializeObject(new { error = new { message = "Invalid request body" } }),
                400);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error proxying SideShift shift request");
            return new ProxyResponse(false,
                JsonConvert.SerializeObject(new { error = new { message = "Proxy error" } }),
                502);
        }
    }

    // === FixedFloat Methods ===

    /// <summary>
    /// Get FixedFloat configuration (ref code for widget)
    /// </summary>
    public async Task<FixedFloatConfigResponse> GetFixedFloatConfig()
    {
        var settings = await GetSettings();

        return new FixedFloatConfigResponse
        {
            Enabled = settings.Enabled && !string.IsNullOrEmpty(settings.FixedFloatRefCode),
            RefCode = settings.FixedFloatRefCode
        };
    }

    // === Helper Methods ===

    private HttpClient CreateSideShiftClient(string? userIp)
    {
        var client = _httpClientFactory.CreateClient("sideshift-proxy");

        if (!string.IsNullOrEmpty(userIp) && !IsLocalIp(userIp))
        {
            client.DefaultRequestHeaders.Add("x-user-ip", userIp);
        }

        return client;
    }

    private static bool IsLocalIp(string ip)
    {
        return ip == "127.0.0.1" ||
               ip == "::1" ||
               ip.StartsWith("192.168.") ||
               ip.StartsWith("10.") ||
               ip.StartsWith("172.16.") ||
               ip == "localhost";
    }
}

/// <summary>
/// Response from a proxied request
/// </summary>
public record ProxyResponse(bool Success, string Content, int StatusCode);

/// <summary>
/// FixedFloat configuration response
/// </summary>
public class FixedFloatConfigResponse
{
    public bool Enabled { get; set; }
    public string? RefCode { get; set; }
}
