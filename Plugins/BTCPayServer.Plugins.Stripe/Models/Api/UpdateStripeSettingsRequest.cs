#nullable enable
namespace BTCPayServer.Plugins.Stripe.Models.Api;

/// <summary>
/// Request to update Stripe settings.
/// All fields are optional - existing values are preserved when not provided.
/// </summary>
public class UpdateStripeSettingsRequest
{
    public bool? Enabled { get; set; }

    public string? PublishableKey { get; set; }

    public string? SecretKey { get; set; }

    public string? SettlementCurrency { get; set; }

    public string? AdvancedConfig { get; set; }
}
