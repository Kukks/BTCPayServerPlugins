#nullable enable
using System;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Stripe.Models.Api;
using BTCPayServer.Plugins.Stripe.PaymentHandler;
using BTCPayServer.Plugins.Stripe.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.Stripe.Controllers;

[ApiController]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Greenfield, Policy = Policies.CanViewStoreSettings)]
[EnableCors(CorsPolicies.All)]
public class GreenfieldStripeController : ControllerBase
{
    private readonly StoreRepository _storeRepository;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly StripeService _stripeService;

    public GreenfieldStripeController(
        StoreRepository storeRepository,
        PaymentMethodHandlerDictionary handlers,
        StripeService stripeService)
    {
        _storeRepository = storeRepository;
        _handlers = handlers;
        _stripeService = stripeService;
    }

    private static StripePaymentMethodConfig? GetConfig(StoreData store, PaymentMethodHandlerDictionary handlers)
    {
        return store.GetPaymentMethodConfig<StripePaymentMethodConfig>(
            StripePlugin.StripePaymentMethodId, handlers);
    }

    private static string? MaskApiKey(string? key)
    {
        if (string.IsNullOrEmpty(key))
            return null;
        if (key.Length <= 10)
            return "***";
        return key[..3] + "***..." + key[^4..];
    }

    private static StripeSettingsData ToStripeSettingsData(StripePaymentMethodConfig config)
    {
        return new StripeSettingsData
        {
            Enabled = config.Enabled,
            PublishableKey = MaskApiKey(config.PublishableKey),
            SecretKey = MaskApiKey(config.SecretKey),
            SettlementCurrency = config.SettlementCurrency ?? "USD",
            AdvancedConfig = config.AdvancedConfig,
            IsConfigured = config.IsConfigured,
            IsTestMode = config.IsTestMode,
            WebhookId = config.WebhookId
        };
    }

    [HttpGet("~/api/v1/stores/{storeId}/stripe/settings")]
    public IActionResult GetSettings(string storeId)
    {
        var store = HttpContext.GetStoreData();
        var config = GetConfig(store, _handlers) ?? new StripePaymentMethodConfig();
        return Ok(ToStripeSettingsData(config));
    }

    [HttpPut("~/api/v1/stores/{storeId}/stripe/settings")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public async Task<IActionResult> UpdateSettings(string storeId, [FromBody] UpdateStripeSettingsRequest? request)
    {
        var store = HttpContext.GetStoreData();
        if (request == null)
        {
            ModelState.AddModelError(string.Empty, "Request body is required");
            return this.CreateValidationError(ModelState);
        }

        var existingConfig = GetConfig(store, _handlers) ?? new StripePaymentMethodConfig();
        var config = new StripePaymentMethodConfig
        {
            Enabled = request.Enabled ?? existingConfig.Enabled,
            PublishableKey = !string.IsNullOrWhiteSpace(request.PublishableKey)
                ? request.PublishableKey.Trim()
                : existingConfig.PublishableKey,
            SecretKey = !string.IsNullOrWhiteSpace(request.SecretKey)
                ? request.SecretKey.Trim()
                : existingConfig.SecretKey,
            SettlementCurrency = !string.IsNullOrWhiteSpace(request.SettlementCurrency)
                ? request.SettlementCurrency.Trim().ToUpperInvariant()
                : (existingConfig.SettlementCurrency ?? "USD"),
            AdvancedConfig = request.AdvancedConfig ?? existingConfig.AdvancedConfig,
            WebhookId = existingConfig.WebhookId,
            WebhookSecret = existingConfig.WebhookSecret
        };

        if (config.Enabled)
        {
            if (string.IsNullOrWhiteSpace(config.PublishableKey))
                ModelState.AddModelError(nameof(request.PublishableKey), "Publishable key is required when enabled");
            if (string.IsNullOrWhiteSpace(config.SecretKey))
                ModelState.AddModelError(nameof(request.SecretKey), "Secret key is required when enabled");
            if (string.IsNullOrWhiteSpace(config.SettlementCurrency))
                ModelState.AddModelError(nameof(request.SettlementCurrency), "Settlement currency is required");
            if (!string.IsNullOrWhiteSpace(config.PublishableKey) && !config.PublishableKey.StartsWith("pk_"))
                ModelState.AddModelError(nameof(request.PublishableKey), "Publishable key must start with 'pk_'");
            if (!string.IsNullOrWhiteSpace(config.SecretKey) && !config.SecretKey.StartsWith("sk_"))
                ModelState.AddModelError(nameof(request.SecretKey), "Secret key must start with 'sk_'");
        }

        if (!ModelState.IsValid)
            return this.CreateValidationError(ModelState);

        var handler = _handlers[StripePlugin.StripePaymentMethodId];
        store.SetPaymentMethodConfig(handler, config);
        await _storeRepository.UpdateStore(store);

        return Ok(ToStripeSettingsData(config));
    }

    [HttpDelete("~/api/v1/stores/{storeId}/stripe/settings")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public async Task<IActionResult> DeleteSettings(string storeId)
    {
        var store = HttpContext.GetStoreData();
        var existingConfig = GetConfig(store, _handlers);
        if (existingConfig == null)
            return Ok();

        if (!string.IsNullOrEmpty(existingConfig.WebhookId) && !string.IsNullOrEmpty(existingConfig.SecretKey))
        {
            try
            {
                await _stripeService.DeleteWebhook(existingConfig);
            }
            catch (Exception)
            {
                // Ignore - webhook may already be deleted on Stripe's side
            }
        }

        var config = new StripePaymentMethodConfig
        {
            Enabled = false,
            SettlementCurrency = existingConfig.SettlementCurrency ?? "USD",
            AdvancedConfig = existingConfig.AdvancedConfig
        };

        var handler = _handlers[StripePlugin.StripePaymentMethodId];
        store.SetPaymentMethodConfig(handler, config);
        await _storeRepository.UpdateStore(store);

        return Ok();
    }

    [HttpPost("~/api/v1/stores/{storeId}/stripe/test")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public async Task<IActionResult> TestConnection(string storeId)
    {
        var store = HttpContext.GetStoreData();
        var config = GetConfig(store, _handlers);
        if (config == null || !config.IsConfigured)
        {
            return this.CreateAPIError(422, "stripe-not-configured",
                "Stripe is not configured for this store. Configure API keys first.");
        }

        var (success, error) = await _stripeService.TestConnection(config);

        return Ok(new TestConnectionResponse
        {
            Success = success,
            Message = success
                ? $"Successfully connected to Stripe ({(config.IsTestMode ? "Test Mode" : "Live Mode")})"
                : $"Failed to connect to Stripe: {error}",
            Mode = config.IsTestMode ? "test" : "live"
        });
    }

    [HttpPost("~/api/v1/stores/{storeId}/stripe/webhook/register")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public async Task<IActionResult> RegisterWebhook(string storeId)
    {
        var store = HttpContext.GetStoreData();
        var config = GetConfig(store, _handlers);
        if (config == null || !config.IsConfigured)
        {
            return this.CreateAPIError(422, "stripe-not-configured",
                "Stripe is not configured for this store. Configure API keys first.");
        }

        var externalUrl = Request.GetAbsoluteRoot();
        var result = await _stripeService.TryRegisterWebhook(config, storeId, externalUrl);

        if (!result.Success)
            return this.CreateAPIError(422, "webhook-registration-failed", result.Reason ?? "Failed to register webhook");

        config.WebhookId = result.WebhookId;
        if (!string.IsNullOrEmpty(result.WebhookSecret))
            config.WebhookSecret = result.WebhookSecret;

        var handler = _handlers[StripePlugin.StripePaymentMethodId];
        store.SetPaymentMethodConfig(handler, config);
        await _storeRepository.UpdateStore(store);

        return Ok(new WebhookStatusData
        {
            Configured = true,
            WebhookId = result.WebhookId,
            Message = result.Message
        });
    }

    [HttpGet("~/api/v1/stores/{storeId}/stripe/webhook/status")]
    public async Task<IActionResult> GetWebhookStatus(string storeId)
    {
        var store = HttpContext.GetStoreData();
        var config = GetConfig(store, _handlers);
        if (config == null || !config.IsConfigured)
        {
            return Ok(new WebhookStatusData
            {
                Configured = false,
                Message = "Stripe is not configured"
            });
        }

        var externalUrl = Request.GetAbsoluteRoot();
        var status = await _stripeService.GetWebhookStatus(config, storeId, externalUrl);

        return Ok(new WebhookStatusData
        {
            Configured = status.IsConfigured,
            WebhookId = status.WebhookId,
            WebhookUrl = status.WebhookUrl,
            Message = status.IsValid
                ? "Webhook is active"
                : (status.Error ?? status.Status ?? "Not configured")
        });
    }
}
