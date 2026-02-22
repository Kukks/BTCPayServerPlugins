#nullable enable
namespace BTCPayServer.Plugins.Stripe.Models.Api;

/// <summary>
/// Response from the test connection endpoint.
/// </summary>
public class TestConnectionResponse
{
    public bool Success { get; set; }

    public string? Message { get; set; }

    /// <summary>
    /// "test" or "live" depending on API key mode
    /// </summary>
    public string? Mode { get; set; }
}
