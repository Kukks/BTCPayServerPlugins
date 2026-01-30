#nullable enable
namespace BTCPayServer.Plugins.SwapMiddleware;

/// <summary>
/// Server-level settings for the Swap Middleware plugin.
/// Stored using ISettingsRepository (not per-store).
/// </summary>
public class SwapMiddlewareSettings
{
    /// <summary>
    /// Master switch to enable/disable the middleware
    /// </summary>
    public bool Enabled { get; set; }

    // === SideShift Configuration ===

    /// <summary>
    /// SideShift affiliate ID to inject into API calls.
    /// Get yours at https://sideshift.ai/affiliates
    /// </summary>
    public string? SideShiftAffiliateId { get; set; }

    // === FixedFloat Configuration ===

    /// <summary>
    /// FixedFloat referral code for widget URL.
    /// Get yours at https://ff.io/affiliate
    /// </summary>
    public string? FixedFloatRefCode { get; set; }

    /// <summary>
    /// FixedFloat API key (for future API integration)
    /// </summary>
    public string? FixedFloatApiKey { get; set; }

    /// <summary>
    /// FixedFloat API secret (for future API integration)
    /// </summary>
    public string? FixedFloatApiSecret { get; set; }

    // === Advanced Settings ===

    /// <summary>
    /// Cache duration in minutes for coins/facts endpoints (default: 5)
    /// </summary>
    public int CacheDurationMinutes { get; set; } = 5;
}
