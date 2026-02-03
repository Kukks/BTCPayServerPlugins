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

        // Check webhook status if configured
        if (config.IsConfigured)
        {
            var externalUrl = Request.GetAbsoluteRoot();
            var webhookStatus = await _stripeService.GetWebhookStatus(config, storeId, externalUrl);
            ViewBag.WebhookStatus = webhookStatus;
        }

        return View(config);
    }

    [HttpPost("")]
    public async Task<IActionResult> Configure(string storeId, StripePaymentMethodConfig config, string command)
    {
        var store = await _storeRepository.FindStore(storeId);
        if (store == null)
            return NotFound();

        // Get existing config to merge with form data
        var existingConfig = GetConfig(store);

        // Merge existing secrets if form doesn't provide them (password fields don't submit values)
        if (existingConfig?.IsConfigured == true)
        {
            // Preserve API keys unless explicitly clearing
            if (string.IsNullOrWhiteSpace(config.PublishableKey))
                config.PublishableKey = existingConfig.PublishableKey;
            if (string.IsNullOrWhiteSpace(config.SecretKey))
                config.SecretKey = existingConfig.SecretKey;
            // Always preserve webhook ID (managed internally)
            if (string.IsNullOrWhiteSpace(config.WebhookId))
                config.WebhookId = existingConfig.WebhookId;
            // Preserve webhook secret if not provided (user can now manually enter/update it)
            if (string.IsNullOrWhiteSpace(config.WebhookSecret))
                config.WebhookSecret = existingConfig.WebhookSecret;
        }

        switch (command?.ToLowerInvariant())
        {
            case "save":
                return await SaveSettings(storeId, store, config);

            case "test":
                return await TestConnection(storeId, config);

            case "registerwebhook":
                return await RegisterWebhook(storeId, store, config);

            case "clearcredentials":
                return await ClearCredentials(storeId, store);

            default:
                return View(config);
        }
    }

    private async Task<IActionResult> SaveSettings(string storeId, StoreData store, StripePaymentMethodConfig config)
    {
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

    private async Task<IActionResult> RegisterWebhook(string storeId, StoreData store, StripePaymentMethodConfig config)
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
            var handler = _handlers[StripePlugin.StripePaymentMethodId];
            store.SetPaymentMethodConfig(handler, config);
            await _storeRepository.UpdateStore(store);

            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Severity = StatusMessageModel.StatusSeverity.Success,
                Message = result.Message ?? "Webhook registered successfully"
            });

            return RedirectToAction(nameof(Configure), new { storeId });
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

    private async Task<IActionResult> ClearCredentials(string storeId, StoreData store)
    {
        var existingConfig = GetConfig(store);
        if (existingConfig == null)
            return RedirectToAction(nameof(Configure), new { storeId });

        // Try to delete the webhook from Stripe if it exists
        if (!string.IsNullOrEmpty(existingConfig.WebhookId) && !string.IsNullOrEmpty(existingConfig.SecretKey))
        {
            try
            {
                await _stripeService.DeleteWebhook(existingConfig);
            }
            catch
            {
                // Ignore errors - webhook may already be deleted
            }
        }

        // Clear all credentials
        var config = new StripePaymentMethodConfig
        {
            Enabled = false,
            SettlementCurrency = existingConfig.SettlementCurrency,
            AdvancedConfig = existingConfig.AdvancedConfig
        };

        var handler = _handlers[StripePlugin.StripePaymentMethodId];
        store.SetPaymentMethodConfig(handler, config);
        await _storeRepository.UpdateStore(store);

        TempData.SetStatusMessageModel(new StatusMessageModel
        {
            Severity = StatusMessageModel.StatusSeverity.Success,
            Message = "Stripe credentials cleared"
        });

        return RedirectToAction(nameof(Configure), new { storeId });
    }
}
