#nullable enable
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Stripe.PaymentHandler;
using BTCPayServer.Plugins.Stripe.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.Stripe.Controllers;

/// <summary>
/// Controller for managing per-store Stripe settings.
/// </summary>
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Route("plugins/{storeId}/stripe")]
public class StripeController : Controller
{
    private readonly StoreRepository _storeRepository;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly StripeService _stripeService;

    public StripeController(
        StoreRepository storeRepository,
        PaymentMethodHandlerDictionary handlers,
        StripeService stripeService)
    {
        _storeRepository = storeRepository;
        _handlers = handlers;
        _stripeService = stripeService;
    }

    private StripePaymentMethodConfig? GetConfig(StoreData store)
    {
        return store.GetPaymentMethodConfig<StripePaymentMethodConfig>(
            StripePlugin.StripePaymentMethodId, _handlers);
    }

    [HttpGet("")]
    public async Task<IActionResult> Configure(string storeId)
    {
        var store = await _storeRepository.FindStore(storeId);
        if (store == null)
            return NotFound();

        var config = GetConfig(store) ?? new StripePaymentMethodConfig();
        return View(config);
    }

    [HttpPost("")]
    public async Task<IActionResult> Configure(string storeId, StripePaymentMethodConfig config, string command)
    {

        switch (command?.ToLowerInvariant())
        {
            case "save":
                return await SaveSettings(storeId, config);

            case "test":
                return await TestConnection(storeId, config);

            case "registerwebhook":
                return await RegisterWebhook(storeId, config);

            default:
                return View(config);
        }
    }

    private async Task<IActionResult> SaveSettings(string storeId, StripePaymentMethodConfig config)
    {
        var store = await _storeRepository.FindStore(storeId);
        if (store == null)
            return NotFound();

        // Get existing config to preserve secrets if not provided
        var existingConfig = GetConfig(store);

        // Preserve existing secret values if new values are empty (password fields don't submit values)
        if (string.IsNullOrWhiteSpace(config.SecretKey) && !string.IsNullOrWhiteSpace(existingConfig?.SecretKey))
            config.SecretKey = existingConfig.SecretKey;

        if (string.IsNullOrWhiteSpace(config.WebhookSecret) && !string.IsNullOrWhiteSpace(existingConfig?.WebhookSecret))
            config.WebhookSecret = existingConfig.WebhookSecret;

        if (config.Enabled)
        {
            // Validate required fields when enabling
            if (string.IsNullOrWhiteSpace(config.PublishableKey))
                ModelState.AddModelError(nameof(config.PublishableKey), "Publishable key is required");

            if (string.IsNullOrWhiteSpace(config.SecretKey))
                ModelState.AddModelError(nameof(config.SecretKey), "Secret key is required");

            if (string.IsNullOrWhiteSpace(config.SettlementCurrency))
                ModelState.AddModelError(nameof(config.SettlementCurrency), "Settlement currency is required");

            // Validate API key prefixes
            if (!string.IsNullOrWhiteSpace(config.PublishableKey) && !config.PublishableKey.StartsWith("pk_"))
                ModelState.AddModelError(nameof(config.PublishableKey), "Publishable key must start with 'pk_'");

            if (!string.IsNullOrWhiteSpace(config.SecretKey) && !config.SecretKey.StartsWith("sk_"))
                ModelState.AddModelError(nameof(config.SecretKey), "Secret key must start with 'sk_'");
        }

        if (!ModelState.IsValid)
            return View(config);

        // Save using payment method config storage
        var handler = _handlers[StripePlugin.StripePaymentMethodId];
        store.SetPaymentMethodConfig(handler, config);
        await _storeRepository.UpdateStore(store);

        TempData.SetStatusMessageModel(new StatusMessageModel
        {
            Severity = StatusMessageModel.StatusSeverity.Success,
            Message = "Stripe settings saved successfully"
        });

        return RedirectToAction(nameof(Configure), new { storeId });
    }

    private async Task<IActionResult> TestConnection(string storeId, StripePaymentMethodConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.SecretKey))
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Severity = StatusMessageModel.StatusSeverity.Error,
                Message = "Secret key is required to test connection"
            });
            return View(config);
        }

        var (success, error) = await _stripeService.TestConnection(config);

        if (success)
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Severity = StatusMessageModel.StatusSeverity.Success,
                Message = $"Successfully connected to Stripe ({(config.IsTestMode ? "Test Mode" : "Live Mode")})"
            });
        }
        else
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Severity = StatusMessageModel.StatusSeverity.Error,
                Message = $"Failed to connect to Stripe: {error}"
            });
        }

        return View(config);
    }

    private async Task<IActionResult> RegisterWebhook(string storeId, StripePaymentMethodConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.SecretKey))
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Severity = StatusMessageModel.StatusSeverity.Error,
                Message = "Secret key is required to register webhook"
            });
            return View(config);
        }

        var externalUrl = Request.GetAbsoluteRoot();
        var result = await _stripeService.TryRegisterWebhook(config, storeId, externalUrl);

        if (result.Success)
        {
            // Update config with webhook details
            config.WebhookId = result.WebhookId;
            if (!string.IsNullOrEmpty(result.WebhookSecret))
            {
                config.WebhookSecret = result.WebhookSecret;
            }

            // Save the updated config
            var store = await _storeRepository.FindStore(storeId);
            if (store != null)
            {
                var handler = _handlers[StripePlugin.StripePaymentMethodId];
                store.SetPaymentMethodConfig(handler, config);
                await _storeRepository.UpdateStore(store);
            }

            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Severity = StatusMessageModel.StatusSeverity.Success,
                Message = result.Message ?? "Webhook registered successfully"
            });
        }
        else
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Severity = StatusMessageModel.StatusSeverity.Warning,
                Message = result.Reason ?? "Failed to register webhook"
            });
        }

        return View(config);
    }
}
