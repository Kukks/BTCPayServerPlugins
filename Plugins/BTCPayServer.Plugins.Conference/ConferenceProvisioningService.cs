using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.PointOfSale;
using BTCPayServer.Services.Apps;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Conference;

public class ConferenceProvisioningService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly StoreRepository _storeRepository;
    private readonly AppService _appService;

    public ConferenceProvisioningService(
        IServiceProvider serviceProvider,
        StoreRepository storeRepository,
        AppService appService)
    {
        _serviceProvider = serviceProvider;
        _storeRepository = storeRepository;
        _appService = appService;
    }

    public async Task<ProvisionResult> ProvisionMerchant(
        string adminUserId,
        ConferenceMerchant merchant,
        ConferenceSettings conferenceSettings)
    {
        var result = new ProvisionResult();

        try
        {
            // Step 1: Create or find user
            if (string.IsNullOrEmpty(merchant.UserId))
            {
                var (userId, wasCreated) = await CreateOrFindUser(merchant.Email, merchant.Password);
                merchant.UserId = userId;
                merchant.UserCreatedByPlugin = wasCreated;
                merchant.Password = null; // Clear password after use
            }

            // Step 2: Create store if needed
            if (string.IsNullOrEmpty(merchant.StoreId))
            {
                var storeId = await CreateStore(adminUserId, merchant, conferenceSettings);
                merchant.StoreId = storeId;
            }
            else
            {
                // Store exists, ensure config is applied
                await ApplyStoreSettings(merchant, conferenceSettings);
            }

            // Step 3: Ensure merchant user is on the store as Employee
            await EnsureStoreUser(merchant.StoreId, merchant.UserId);

            // Step 4: Create POS app if needed
            if (string.IsNullOrEmpty(merchant.PosAppId))
            {
                var posAppId = await CreatePosApp(merchant, conferenceSettings);
                merchant.PosAppId = posAppId;
            }

            result.Success = true;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
        }

        return result;
    }

    public async Task<MerchantHealthStatus> CheckMerchantHealth(ConferenceMerchant merchant)
    {
        var status = new MerchantHealthStatus();

        if (string.IsNullOrEmpty(merchant.UserId))
        {
            status.UserStatus = "Not provisioned";
            return status;
        }

        // Check user exists
        using var scope = _serviceProvider.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByIdAsync(merchant.UserId);
        status.UserStatus = user != null ? "OK" : "User deleted";

        // Check store exists
        if (!string.IsNullOrEmpty(merchant.StoreId))
        {
            var store = await _storeRepository.FindStore(merchant.StoreId);
            status.StoreStatus = store != null ? "OK" : "Store deleted";
        }
        else
        {
            status.StoreStatus = "Not provisioned";
        }

        // Check POS app exists
        if (!string.IsNullOrEmpty(merchant.PosAppId))
        {
            var app = await _appService.GetApp(merchant.PosAppId, null, includeArchived: true);
            status.PosStatus = app != null ? "OK" : "POS deleted";
        }
        else
        {
            status.PosStatus = "Not provisioned";
        }

        return status;
    }

    public async Task ReapplySettings(
        List<ConferenceMerchant> merchants,
        ConferenceSettings conferenceSettings,
        bool forceLightning,
        bool forceCurrency,
        bool forceSpread)
    {
        foreach (var merchant in merchants)
        {
            if (string.IsNullOrEmpty(merchant.StoreId))
                continue;

            var store = await _storeRepository.FindStore(merchant.StoreId);
            if (store == null)
                continue;

            var blob = store.GetStoreBlob();
            var changed = false;

            if (forceCurrency)
            {
                blob.DefaultCurrency = merchant.Currency ?? conferenceSettings.DefaultCurrency;
                changed = true;
            }

            if (forceSpread)
            {
                blob.Spread = (merchant.Spread ?? conferenceSettings.DefaultSpread) / 100.0m;
                changed = true;
            }

            if (changed)
            {
                store.SetStoreBlob(blob);
            }

            if (forceLightning)
            {
                var connString = merchant.LightningConnectionString ??
                                 conferenceSettings.DefaultLightningConnectionString;
                if (!string.IsNullOrEmpty(connString))
                {
                    SetLightningPaymentMethod(store, connString);
                }
            }

            if (changed || forceLightning)
            {
                await _storeRepository.UpdateStore(store);
            }
        }
    }

    public async Task<RepairResult> RepairMerchant(
        string adminUserId,
        ConferenceMerchant merchant,
        ConferenceSettings conferenceSettings)
    {
        var health = await CheckMerchantHealth(merchant);
        var result = new RepairResult();

        if (health.UserStatus != "OK")
        {
            merchant.UserId = null;
        }

        if (health.StoreStatus != "OK")
        {
            merchant.StoreId = null;
            merchant.PosAppId = null; // POS depends on store
        }

        if (health.PosStatus != "OK")
        {
            merchant.PosAppId = null;
        }

        var provision = await ProvisionMerchant(adminUserId, merchant, conferenceSettings);
        result.Success = provision.Success;
        result.Error = provision.Error;
        result.RepairedComponents = new List<string>();

        if (health.UserStatus != "OK") result.RepairedComponents.Add("User");
        if (health.StoreStatus != "OK") result.RepairedComponents.Add("Store");
        if (health.PosStatus != "OK") result.RepairedComponents.Add("POS");

        return result;
    }

    public async Task ArchiveMerchant(ConferenceMerchant merchant)
    {
        // Archive the POS app
        if (!string.IsNullOrEmpty(merchant.PosAppId))
        {
            var app = await _appService.GetApp(merchant.PosAppId, null, includeArchived: true);
            if (app != null)
            {
                app.Archived = true;
                await _appService.UpdateOrCreateApp(app);
            }
        }

        // Archive the store
        if (!string.IsNullOrEmpty(merchant.StoreId))
        {
            var store = await _storeRepository.FindStore(merchant.StoreId);
            if (store != null)
            {
                store.Archived = true;
                await _storeRepository.UpdateStore(store);
            }
        }
    }

    /// <returns>(userId, wasCreated) — wasCreated is false when an existing account was found</returns>
    private async Task<(string UserId, bool WasCreated)> CreateOrFindUser(string email, string password)
    {
        using var scope = _serviceProvider.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var existingUser = await userManager.FindByEmailAsync(email);
        if (existingUser != null)
            return (existingUser.Id, false);

        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            RequiresEmailConfirmation = false,
            RequiresApproval = false,
            Approved = true,
            Created = DateTimeOffset.UtcNow
        };

        password ??= GeneratePassword();
        var createResult = await userManager.CreateAsync(user, password);
        if (!createResult.Succeeded)
        {
            var errorMessages = string.Join(", ", createResult.Errors.Select(e => e.Description));
            throw new InvalidOperationException(
                $"Failed to create user {email}: {errorMessages}");
        }

        return (user.Id, true);
    }

    private async Task<string> CreateStore(
        string adminUserId,
        ConferenceMerchant merchant,
        ConferenceSettings conferenceSettings)
    {
        var store = new StoreData { StoreName = merchant.StoreName };

        var blob = store.GetStoreBlob();
        blob.DefaultCurrency = merchant.Currency ?? conferenceSettings.DefaultCurrency;
        blob.Spread = (merchant.Spread ?? conferenceSettings.DefaultSpread) / 100.0m;
        store.SetStoreBlob(blob);

        // Set lightning payment method
        var connString = merchant.LightningConnectionString ??
                         conferenceSettings.DefaultLightningConnectionString;
        if (!string.IsNullOrEmpty(connString))
        {
            SetLightningPaymentMethod(store, connString);
        }

        await _storeRepository.CreateStore(adminUserId, store);
        return store.Id;
    }

    private async Task ApplyStoreSettings(
        ConferenceMerchant merchant,
        ConferenceSettings conferenceSettings)
    {
        var store = await _storeRepository.FindStore(merchant.StoreId);
        if (store == null) return;

        var blob = store.GetStoreBlob();
        blob.DefaultCurrency = merchant.Currency ?? conferenceSettings.DefaultCurrency;
        blob.Spread = (merchant.Spread ?? conferenceSettings.DefaultSpread) / 100.0m;
        store.SetStoreBlob(blob);

        var connString = merchant.LightningConnectionString ??
                         conferenceSettings.DefaultLightningConnectionString;
        if (!string.IsNullOrEmpty(connString))
        {
            SetLightningPaymentMethod(store, connString);
        }

        await _storeRepository.UpdateStore(store);
    }

    private async Task EnsureStoreUser(string storeId, string userId)
    {
        var result = await _storeRepository.AddOrUpdateStoreUser(
            storeId, userId, StoreRoleId.Employee);

        // DuplicateRole is fine — user already has the role
        if (result is StoreRepository.AddOrUpdateStoreUserResult.InvalidRole)
        {
            throw new InvalidOperationException("Employee role not found");
        }
    }

    private async Task<string> CreatePosApp(
        ConferenceMerchant merchant,
        ConferenceSettings conferenceSettings)
    {
        var currency = merchant.Currency ?? conferenceSettings.DefaultCurrency;
        var settings = new PointOfSaleSettings
        {
            Title = merchant.StoreName,
            Currency = currency,
            DefaultView = PosViewType.Light,
            ShowCustomAmount = true,
            ShowDiscount = false,
            EnableTips = false,
            Template = null
        };

        var appData = new AppData
        {
            StoreDataId = merchant.StoreId,
            Name = $"{merchant.StoreName} POS",
            AppType = PointOfSaleAppType.AppType
        };
        appData.SetSettings(settings);

        await _appService.UpdateOrCreateApp(appData);
        return appData.Id;
    }

    private static void SetLightningPaymentMethod(StoreData store, string connectionString)
    {
        var lnConfig = new LightningPaymentMethodConfig();

        if (connectionString == LightningPaymentMethodConfig.InternalNode)
        {
            lnConfig.InternalNodeRef = LightningPaymentMethodConfig.InternalNode;
        }
        else
        {
            lnConfig.ConnectionString = connectionString;
        }

        var paymentMethodId = PaymentMethodId.Parse("BTC-LN");
        store.SetPaymentMethodConfig(paymentMethodId, JToken.FromObject(lnConfig));
    }

    private static string GeneratePassword()
    {
        var bytes = RandomNumberGenerator.GetBytes(16);
        return Convert.ToBase64String(bytes).Replace("+", "x").Replace("/", "y")[..20] + "!1a";
    }
}

public class ProvisionResult
{
    public bool Success { get; set; }
    public string Error { get; set; }
}

public class MerchantHealthStatus
{
    public string UserStatus { get; set; }
    public string StoreStatus { get; set; }
    public string PosStatus { get; set; }

    public bool IsHealthy => UserStatus == "OK" && StoreStatus == "OK" && PosStatus == "OK";
    public bool IsNotProvisioned => UserStatus == "Not provisioned";

    public string Summary
    {
        get
        {
            if (IsHealthy) return "Ready";
            if (IsNotProvisioned) return "Not provisioned";
            var issues = new List<string>();
            if (UserStatus != "OK") issues.Add(UserStatus);
            if (StoreStatus != "OK") issues.Add(StoreStatus);
            if (PosStatus != "OK") issues.Add(PosStatus);
            return string.Join(", ", issues);
        }
    }
}

public class RepairResult
{
    public bool Success { get; set; }
    public string Error { get; set; }
    public List<string> RepairedComponents { get; set; }
}
