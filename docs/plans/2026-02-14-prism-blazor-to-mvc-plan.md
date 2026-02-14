# Prism Blazor-to-MVC Conversion Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Replace the Prism plugin's 10 Blazor components (~3,500 lines) with standard BTCPay MVC views, eliminating all client-side state bugs while preserving every feature.

**Architecture:** Single-page MVC with command pattern. One main settings page with global settings, splits, and balances sections. Separate page for destination alias CRUD. All validation server-side. Minimal JS for template-clone list management and source-type field toggling.

**Tech Stack:** ASP.NET Core MVC, Razor views, BTCPay tag helpers, vanilla JS (~60 lines)

**Design Doc:** `docs/plans/2026-02-14-prism-blazor-to-mvc-design.md`

---

## Key Reference Files

- **Existing controller:** `Plugins/BTCPayServer.Plugins.Prism/PrismController.cs`
- **Plugin entry:** `Plugins/BTCPayServer.Plugins.Prism/PrismPlugin.cs`
- **Backend service:** `Plugins/BTCPayServer.Plugins.Prism/SatBreaker.cs`
- **Data models:** `PrismSettings.cs`, `Split.cs`, `PrismSplit.cs`, `PendingPayout.cs`
- **Existing Blazor host view:** `Views/Prism/Edit.cshtml`
- **Existing nav:** `Views/Shared/PrismNav.cshtml`
- **Existing _ViewImports:** `Views/_ViewImports.cshtml`
- **Reference MVC plugin:** Stripe plugin (`Plugins/BTCPayServer.Plugins.Stripe/`)
- **Reference dynamic lists:** NIP05 plugin (`Plugins/BTCPayServer.Plugins.NIP05/Views/Nip5/Edit.cshtml`)

## Critical Context for Implementer

### BTCPay MVC Conventions
- Controllers inherit from `Controller`, use `[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]`
- Routes: `[Route("plugins/{storeId}/prism")]` or `[Route("stores/{storeId}/plugins/prism")]`
- Views go in `Views/Shared/Prism/` (shared so they're discoverable by the view engine across plugin boundaries)
- Status messages: `TempData.SetStatusMessageModel(new StatusMessageModel { Severity = ..., Message = ... })`
- Sticky header pattern: `<div class="sticky-header-setup"></div>` + `<div class="sticky-header">`
- Page activation: `ViewData.SetActivePage("Prism", "Prism", "Prism")`
- Navigation: `layout-menu-item="Prism"` attribute on nav links
- Command pattern: `<button name="command" type="submit" value="save">` with `string command` parameter in POST action
- Dynamic lists: HTML5 `<template>` element + JS clone + reindex `name` attributes

### Source Format Reference
- Catch-all: `*` (LN only), `*CHAIN` (on-chain only), `*All` (all payments)
- Lightning address: `user@domain.com` or just `username`
- POS product: `pos:{appId}:{productId}` or `pos:{appId}:{productId}:{paymentMethod}`
- Wallet transfer: `storetransfer:{paymentMethod}:{amount}:{frequency}:{dayValue}`

### Destination Format Reference
- Raw: `user@domain.com`, `lnurl1...`, `bc1...`, `xpub...`
- Store transfer: `store-prism:{storeId}:{paymentMethod}`
- Alias: just the alias name string (resolved via `PrismSettings.Destinations` dictionary)

### Services Available via DI
- `SatBreaker` — Get/Update settings, ParseSource/EncodeSource
- `LightningAddressService` — Get lightning addresses for store
- `PayoutProcessorService` — Check configured processors
- `AppService` — Get POS apps and parse product templates
- `UserManager` — Get current user's stores
- `IPluginHookService` — Run `prism-destination-validate` filter
- `PullPaymentHostedService` — Cancel payouts
- `EventAggregator` — Publish `ScheduleDayEvent` for send-now
- `StoreRepository` — Load store data
- `IScopeProvider` — Get current store ID in views

---

## Task 1: Create View Models

**Files:**
- Create: `Plugins/BTCPayServer.Plugins.Prism/ViewModels/PrismViewModel.cs`

**Step 1: Create the view model file**

```csharp
#nullable enable
using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace BTCPayServer.Plugins.Prism.ViewModels;

public class PrismViewModel
{
    // Global settings
    public bool Enabled { get; set; }
    public long SatThreshold { get; set; } = 100;
    public decimal Reserve { get; set; } = 2;

    // Splits (indexed form binding)
    public List<SplitViewModel> Splits { get; set; } = new();

    // Balances & payouts (display only, not form-bound)
    public Dictionary<string, long> DestinationBalances { get; set; } = new();
    public Dictionary<string, PendingPayout> PendingPayouts { get; set; } = new();

    // Destination aliases (for dropdowns + display)
    public Dictionary<string, PrismDestination> Destinations { get; set; } = new();

    // Display-only data (populated by controller, not bound from form)
    public List<SelectListItem> AvailableStores { get; set; } = new();
    public List<SelectListItem> AvailableLnAddresses { get; set; } = new();
    public List<SelectListItem> AvailableApps { get; set; } = new();
    public Dictionary<string, List<SelectListItem>> AppProducts { get; set; } = new();
    public bool HasLnProcessor { get; set; }
    public bool HasChainProcessor { get; set; }

    // For version tracking (optimistic concurrency)
    public ulong Version { get; set; }
    public string? StoreId { get; set; }
}

public class SplitViewModel
{
    // Encoded source string (populated on load, rebuilt on save from type-specific fields)
    public string? Source { get; set; }

    // Source type selector
    public string SourceType { get; set; } = "catchall";

    // Catch-all fields
    public string CatchAllType { get; set; } = "*All";

    // LN address fields
    public string? LnAddress { get; set; }

    // POS fields
    public string? PosAppId { get; set; }
    public string? PosProductId { get; set; }
    public string? PosPaymentFilter { get; set; }

    // Wallet transfer fields
    public string? TransferPaymentMethod { get; set; }
    public string? TransferAmount { get; set; }
    public string TransferFrequency { get; set; } = "M";
    public string? TransferDay { get; set; }

    // Destinations
    public List<SplitDestinationViewModel> Destinations { get; set; } = new();
}

public class SplitDestinationViewModel
{
    public string? Destination { get; set; }
    public decimal Percentage { get; set; }
}

public class EditDestinationViewModel
{
    public string? Id { get; set; }
    public string? OriginalId { get; set; }
    public string DestinationType { get; set; } = "address";
    public string? AddressValue { get; set; }
    public string? SelectedStoreId { get; set; }
    public string? StorePaymentMethod { get; set; }
    public long? SatThreshold { get; set; }
    public decimal? Reserve { get; set; }
    public string? StoreId { get; set; }

    // Display-only
    public List<SelectListItem> AvailableStores { get; set; } = new();
    public bool IsInUse { get; set; }
}
```

**Step 2: Commit**

```bash
git add Plugins/BTCPayServer.Plugins.Prism/ViewModels/PrismViewModel.cs
git commit -m "feat(prism): add MVC view models for Blazor-to-MVC conversion"
```

---

## Task 2: Create the MVC Controller

**Files:**
- Modify: `Plugins/BTCPayServer.Plugins.Prism/PrismController.cs` (replace contents)

The controller needs to:
1. Load `PrismSettings` from `SatBreaker` and map to `PrismViewModel`
2. Populate display data (stores, LN addresses, apps, products, processors)
3. Handle command dispatch (save, add-split, remove-split, add-destination, remove-destination)
4. Map `PrismViewModel` back to `PrismSettings` and save via `SatBreaker`
5. Validate sources (uniqueness, format) and destinations (percentages, compatibility)
6. Handle balance update, payout cancel, and send-now as separate endpoints
7. Handle destination alias CRUD on a separate page

**Step 1: Write the controller**

The controller should have these action methods:

```csharp
// GET /plugins/{storeId}/prism — main settings page
[HttpGet("")]
public async Task<IActionResult> Edit(string storeId)

// POST /plugins/{storeId}/prism — command dispatch
[HttpPost("")]
public async Task<IActionResult> Edit(string storeId, PrismViewModel vm, string command)

// GET /plugins/{storeId}/prism/destination — edit destination alias page
[HttpGet("destination")]
public async Task<IActionResult> EditDestination(string storeId, string? id)

// POST /plugins/{storeId}/prism/destination — save/delete destination alias
[HttpPost("destination")]
public async Task<IActionResult> EditDestination(string storeId, EditDestinationViewModel vm, string command)

// POST /plugins/{storeId}/prism/update-balance — update single balance
[HttpPost("update-balance")]
public async Task<IActionResult> UpdateBalance(string storeId, string destinationId, long newBalance)

// POST /plugins/{storeId}/prism/cancel-payout — cancel pending payout
[HttpPost("cancel-payout")]
public async Task<IActionResult> CancelPayout(string storeId, string payoutId)

// POST /plugins/{storeId}/prism/send-now — trigger immediate wallet transfer
[HttpPost("send-now")]
public async Task<IActionResult> SendNow(string storeId, string splitSource)
```

**Key implementation details:**

**Mapping PrismSettings → PrismViewModel (in GET Edit):**
- Copy `Enabled`, `SatThreshold`, `Reserve`, `Version` directly
- For each `Split` in `settings.Splits`, create a `SplitViewModel`:
  - Parse the `Source` string to determine `SourceType` and populate type-specific fields
  - For wallet transfers: use `SatBreaker.ParseSource()` to extract payment method, amount, schedule
  - For POS: split on `:` to get appId, productId, paymentFilter
  - For catch-all: map `*` → LN, `*CHAIN` → CHAIN, `*All` → All
  - For LN address: store in `LnAddress`
  - Map `Split.Destinations` to `SplitDestinationViewModel` list
- Copy `DestinationBalance`, `PendingPayouts`, `Destinations` directly

**Mapping PrismViewModel → PrismSettings (in POST save command):**
- Copy `Enabled`, `SatThreshold`, `Reserve`, `Version` back
- For each `SplitViewModel`, rebuild the `Source` string:
  - `catchall`: use `CatchAllType` value directly (`*`, `*CHAIN`, `*All`)
  - `lnaddress`: use `LnAddress` value
  - `pos`: encode as `pos:{PosAppId}:{PosProductId}` + optional `:{PosPaymentFilter}`
  - `wallettransfer`: use `SatBreaker.EncodeSource()` with parsed schedule, payment method, amount
- Map `SplitDestinationViewModel` list back to `PrismSplit` list
- Preserve existing `DestinationBalance`, `PendingPayouts`, `Destinations` from current settings (not from form)

**Populating display data (shared helper method `PopulateDisplayData`):**
- `AvailableStores`: Query `UserManager.GetUserAsync()` → user's stores via EF, map to `SelectListItem`
- `AvailableLnAddresses`: Query `LightningAddressService.Get(storeId)`, map to `SelectListItem`
- `AvailableApps`: Query `AppService.GetApps(storeId)` filtered to PointOfSale type, map to `SelectListItem`
- `AppProducts`: For each app, parse `AppService.Parse()` template and map products to `SelectListItem`
- `HasLnProcessor`/`HasChainProcessor`: Check `PayoutProcessorService.GetProcessors(storeId)`

**Command handling in POST Edit:**
```
"save" → validate, rebuild sources, save via SatBreaker.UpdatePrismSettingsForStore()
"add-split" → ModelState.Clear(), vm.Splits.Add(new SplitViewModel()), return View
"remove-split:N" → ModelState.Clear(), vm.Splits.RemoveAt(N), return View
"add-destination:N" → ModelState.Clear(), vm.Splits[N].Destinations.Add(new()), return View
"remove-destination:N:M" → ModelState.Clear(), vm.Splits[N].Destinations.RemoveAt(M), return View
```

**Validation (in save command):**
1. Check each split has a valid source (not empty, correct format for type)
2. Check source uniqueness (no duplicate sources, except wallet transfers)
3. Check each split has at least one destination
4. Check destination percentages sum ≤ 100% per split
5. For each destination that isn't an alias, call `IPluginHookService.ApplyFilter("prism-destination-validate", destination)` to validate
6. For wallet transfer splits, check destination types are compatible with payment method
7. Normalize store destinations to `store-prism:{storeId}:{paymentMethod}` format
8. On validation failure: `PopulateDisplayData(vm)`, return `View(vm)`
9. On version mismatch from `UpdatePrismSettingsForStore()`: add error, return view

**Step 2: Commit**

```bash
git add Plugins/BTCPayServer.Plugins.Prism/PrismController.cs
git commit -m "feat(prism): implement full MVC controller with command dispatch"
```

---

## Task 3: Create the Main Edit View

**Files:**
- Create: `Plugins/BTCPayServer.Plugins.Prism/Views/Shared/Prism/Edit.cshtml`

**Step 1: Write the main view**

Structure (follow the BTCPay sticky-header pattern from Stripe):

```cshtml
@using BTCPayServer.Plugins.Prism.ViewModels
@model PrismViewModel
@{
    ViewData.SetActivePage("Prism", "Prism", "Prism");
}

<form method="post" asp-action="Edit" asp-route-storeId="@Model.StoreId">
    <input type="hidden" asp-for="Version" />
    <input type="hidden" asp-for="StoreId" />

    <div class="sticky-header-setup"></div>
    <div class="sticky-header d-sm-flex align-items-center justify-content-between">
        <h2 class="mb-0">@ViewData["Title"]</h2>
        <div class="d-flex gap-3 mt-3 mt-sm-0">
            <button name="command" type="submit" value="save" class="btn btn-primary" id="save-btn">Save</button>
        </div>
    </div>

    <partial name="_StatusMessage" />

    <div class="row">
        <div class="col-xl-8 col-xxl-constrain">

            <!-- GLOBAL SETTINGS SECTION -->
            <!-- Toggle for Enabled, inputs for SatThreshold and Reserve -->

            <!-- SPLITS SECTION -->
            <!-- Header with "Add Split" and "Add Wallet Transfer" buttons -->
            <!-- Loop over Model.Splits with index, render _SplitEditor partial for each -->
            <!-- Template elements for JS cloning -->

            <!-- DESTINATIONS SECTION -->
            <!-- Link to Manage Destinations page -->
            <!-- Brief list of existing aliases -->

        </div>
    </div>
</form>

<!-- BALANCES SECTION (outside main form, uses own mini-forms) -->
<partial name="Prism/_BalancesSection" model="@Model" />

<!-- Extension point for other plugins -->
<vc:ui-extension-point location="prism-edit" model="@Model" />
```

Key details for the splits section:
- Each split is rendered via `<partial name="Prism/_SplitEditor" model="..." />`
- Pass both the split and its index: use a tuple or ViewData for the index
- The "Add Split" button: `<button name="command" type="submit" value="add-split">`
- The "Add Wallet Transfer" button: same but value `add-wallettransfer` (controller creates a SplitViewModel with SourceType=wallettransfer and default schedule)

For destination dropdowns in each split, build `<select>` with options from `Model.Destinations`:
```cshtml
<select name="Splits[@splitIndex].Destinations[@destIndex].Destination" class="form-select">
    <option value="">-- Select destination --</option>
    @foreach (var dest in Model.Destinations)
    {
        <option value="@dest.Key">@dest.Key (@DisplayDestination(dest.Value.Destination))</option>
    }
    <option value="__new__">-- Create New --</option>
</select>
```

When `__new__` is selected, link to `EditDestination` page with a return URL.

**Step 2: Commit**

```bash
git add Plugins/BTCPayServer.Plugins.Prism/Views/Shared/Prism/Edit.cshtml
git commit -m "feat(prism): add main MVC Edit view with global settings and splits"
```

---

## Task 4: Create the Split Editor Partial

**Files:**
- Create: `Plugins/BTCPayServer.Plugins.Prism/Views/Shared/Prism/_SplitEditor.cshtml`

**Step 1: Write the partial view**

This partial renders one split card. It receives the split index via ViewData and uses indexed `name` attributes for model binding.

Key sections:

1. **Card header** with split number and remove button:
   ```cshtml
   <button name="command" type="submit" value="remove-split:@splitIndex" class="btn btn-link text-danger">Remove</button>
   ```

2. **Source type dropdown** with `data-source-type` trigger for JS toggling:
   ```cshtml
   <select name="Splits[@splitIndex].SourceType" class="form-select source-type-select" data-split-index="@splitIndex">
       <option value="catchall">Catch-all</option>
       <option value="lnaddress">Lightning Address</option>
       <option value="pos">POS Product</option>
       <option value="wallettransfer">Wallet Transfer</option>
   </select>
   ```

3. **Conditional field groups** (shown/hidden via `data-source-type` CSS class):
   - **Catch-all**: dropdown for `*`, `*CHAIN`, `*All`
   - **LN address**: text input with datalist for available LN addresses
   - **POS**: app dropdown → product dropdown → payment filter dropdown
   - **Wallet transfer**: payment method, amount, frequency, day

4. **Send Now button** (only for wallet transfers):
   ```cshtml
   <!-- Small separate form to avoid interfering with main save -->
   </form><!-- close main form temporarily -->
   <form method="post" asp-action="SendNow" asp-route-storeId="@Model.StoreId">
       <input type="hidden" name="splitSource" value="@split.Source" />
       <button type="submit" class="btn btn-secondary btn-sm">Send Now</button>
   </form>
   ```

   Actually, better approach: "Send Now" should be a command on the main form that only triggers the send, or a separate mini-form nested outside. Since HTML doesn't allow nested forms, use the `formaction` attribute:
   ```cshtml
   <button type="submit" formaction="@Url.Action("SendNow", "Prism", new { storeId = Model.StoreId })"
           name="splitSource" value="@split.Source" class="btn btn-secondary btn-sm">Send Now</button>
   ```

5. **Destinations list** with indexed binding:
   ```cshtml
   @for (var destIndex = 0; destIndex < split.Destinations.Count; destIndex++)
   {
       <div class="d-flex gap-2 mb-2" data-dest-index="@destIndex">
           <select name="Splits[@splitIndex].Destinations[@destIndex].Destination" class="form-select">
               <!-- destination options -->
           </select>
           <input name="Splits[@splitIndex].Destinations[@destIndex].Percentage" type="number"
                  min="0" max="100" step="0.01" class="form-control" style="max-width: 100px"
                  value="@split.Destinations[destIndex].Percentage" />
           <span class="input-group-text">%</span>
           <button name="command" type="submit" value="remove-destination:@splitIndex:@destIndex"
                   class="btn btn-link text-danger">Remove</button>
       </div>
   }
   <button name="command" type="submit" value="add-destination:@splitIndex" class="btn btn-link">
       + Add Destination
   </button>
   ```

**Step 2: Commit**

```bash
git add Plugins/BTCPayServer.Plugins.Prism/Views/Shared/Prism/_SplitEditor.cshtml
git commit -m "feat(prism): add split editor partial with all source types"
```

---

## Task 5: Create the Balances Section Partial

**Files:**
- Create: `Plugins/BTCPayServer.Plugins.Prism/Views/Shared/Prism/_BalancesSection.cshtml`

**Step 1: Write the partial**

This partial is rendered OUTSIDE the main settings form. It has its own mini-forms for each action.

```cshtml
@using BTCPayServer.Plugins.Prism.ViewModels
@model PrismViewModel

@{
    // Merge balances and pending payouts into unified list
    var unifiedRows = new List<(string destination, long amountSats, string status, string? payoutId, long feeCharged)>();

    foreach (var (dest, balance) in Model.DestinationBalances)
    {
        if (!Model.PendingPayouts.Values.Any(p => p.DestinationId == dest))
        {
            unifiedRows.Add((dest, balance / 1000, "Accumulating", null, 0));
        }
    }
    foreach (var (payoutId, payout) in Model.PendingPayouts)
    {
        var amount = payout.PayoutAmount > 0 ? payout.PayoutAmount : payout.ScheduledTransferAmount;
        unifiedRows.Add((payout.DestinationId, amount, "Payout pending", payoutId, payout.FeeCharged));
    }
}

@if (unifiedRows.Any())
{
    <div class="row mt-4">
        <div class="col-xl-8 col-xxl-constrain">
            <h3 class="mb-3">Balances & Payouts</h3>
            <div class="table-responsive">
                <table class="table">
                    <thead>
                        <tr>
                            <th>Destination</th>
                            <th>Amount (sats)</th>
                            <th>Status</th>
                            <th class="text-end">Action</th>
                        </tr>
                    </thead>
                    <tbody>
                        @foreach (var row in unifiedRows)
                        {
                            <tr>
                                <td><!-- resolve alias or truncate address --></td>
                                <td>
                                    @row.amountSats
                                    @if (row.feeCharged > 0)
                                    {
                                        <span class="text-muted">(fee: @row.feeCharged)</span>
                                    }
                                </td>
                                <td>
                                    <span class="badge @(row.status == "Accumulating" ? "bg-info" : "bg-warning")">
                                        @row.status
                                    </span>
                                </td>
                                <td class="text-end">
                                    @if (row.status == "Accumulating")
                                    {
                                        <form method="post" asp-action="UpdateBalance" asp-route-storeId="@Model.StoreId" class="d-inline-flex gap-2">
                                            <input type="hidden" name="destinationId" value="@row.destination" />
                                            <input type="number" name="newBalance" value="@row.amountSats" class="form-control form-control-sm" style="width:120px" />
                                            <button type="submit" class="btn btn-sm btn-outline-primary">Update</button>
                                        </form>
                                    }
                                    else
                                    {
                                        <form method="post" asp-action="CancelPayout" asp-route-storeId="@Model.StoreId" class="d-inline">
                                            <input type="hidden" name="payoutId" value="@row.payoutId" />
                                            <button type="submit" class="btn btn-sm btn-outline-danger"
                                                    onclick="return confirm('Cancel this payout?')">Cancel</button>
                                        </form>
                                    }
                                </td>
                            </tr>
                        }
                    </tbody>
                </table>
            </div>
        </div>
    </div>
}
```

**Step 2: Commit**

```bash
git add Plugins/BTCPayServer.Plugins.Prism/Views/Shared/Prism/_BalancesSection.cshtml
git commit -m "feat(prism): add balances and payouts section partial"
```

---

## Task 6: Create the Edit Destination Page

**Files:**
- Create: `Plugins/BTCPayServer.Plugins.Prism/Views/Shared/Prism/EditDestination.cshtml`

**Step 1: Write the view**

Separate page for creating/editing a destination alias. Follows standard BTCPay form layout.

```cshtml
@using BTCPayServer.Plugins.Prism.ViewModels
@model EditDestinationViewModel
@{
    var isNew = string.IsNullOrEmpty(Model.OriginalId);
    ViewData.SetActivePage("Prism", isNew ? "New Destination" : "Edit Destination", "PrismDestination");
}

<form method="post" asp-action="EditDestination" asp-route-storeId="@Model.StoreId">
    <input type="hidden" asp-for="OriginalId" />
    <input type="hidden" asp-for="StoreId" />

    <div class="sticky-header-setup"></div>
    <div class="sticky-header d-sm-flex align-items-center justify-content-between">
        <h2 class="mb-0">@ViewData["Title"]</h2>
        <div class="d-flex gap-3 mt-3 mt-sm-0">
            @if (!isNew && !Model.IsInUse)
            {
                <button name="command" type="submit" value="delete" class="btn btn-outline-danger"
                        onclick="return confirm('Delete this destination?')">Delete</button>
            }
            <a asp-action="Edit" asp-route-storeId="@Model.StoreId" class="btn btn-secondary">Cancel</a>
            <button name="command" type="submit" value="save" class="btn btn-primary">Save</button>
        </div>
    </div>

    <partial name="_StatusMessage" />

    <div class="row">
        <div class="col-xl-8 col-xxl-constrain">
            <!-- Alias Name -->
            <div class="form-group">
                <label asp-for="Id" class="form-label" data-required>Destination Name</label>
                <input asp-for="Id" class="form-control" placeholder="e.g. alice-lightning" />
                <span asp-validation-for="Id" class="text-danger"></span>
            </div>

            <!-- Type Selector -->
            <div class="form-group">
                <label asp-for="DestinationType" class="form-label">Type</label>
                <select asp-for="DestinationType" class="form-select" id="dest-type-select">
                    <option value="address">Address (Lightning / Bitcoin / LNURL)</option>
                    <option value="store">Store Transfer</option>
                </select>
            </div>

            <!-- Address mode -->
            <div class="form-group" data-dest-type="address">
                <label asp-for="AddressValue" class="form-label" data-required>Destination</label>
                <input asp-for="AddressValue" class="form-control"
                       placeholder="Lightning address, LNURL, Bitcoin address, or xpub" />
                <span asp-validation-for="AddressValue" class="text-danger"></span>
            </div>

            <!-- Store mode -->
            <div data-dest-type="store">
                <div class="form-group">
                    <label asp-for="SelectedStoreId" class="form-label" data-required>Store</label>
                    <select asp-for="SelectedStoreId" asp-items="Model.AvailableStores" class="form-select">
                        <option value="">-- Select store --</option>
                    </select>
                    <span asp-validation-for="SelectedStoreId" class="text-danger"></span>
                </div>
                <div class="form-group">
                    <label asp-for="StorePaymentMethod" class="form-label">Payment Method</label>
                    <select asp-for="StorePaymentMethod" class="form-select">
                        <option value="BTC-LN">Lightning</option>
                        <option value="BTC-CHAIN">On-chain</option>
                    </select>
                </div>
            </div>

            <!-- Optional overrides -->
            <div class="form-group">
                <label asp-for="SatThreshold" class="form-label">Sat Threshold (optional override)</label>
                <input asp-for="SatThreshold" class="form-control" type="number" min="1" placeholder="Default: use global" />
            </div>
            <div class="form-group">
                <label asp-for="Reserve" class="form-label">Reserve % (optional override)</label>
                <input asp-for="Reserve" class="form-control" type="number" min="0" max="100" step="0.01" placeholder="Default: use global" />
            </div>
        </div>
    </div>
</form>

@section PageFootContent {
    <script>
        document.addEventListener("DOMContentLoaded", function() {
            const typeSelect = document.getElementById("dest-type-select");
            function toggleDestType() {
                const val = typeSelect.value;
                document.querySelectorAll("[data-dest-type]").forEach(el => {
                    el.style.display = el.getAttribute("data-dest-type") === val ? "" : "none";
                });
            }
            typeSelect.addEventListener("change", toggleDestType);
            toggleDestType();
        });
    </script>
}
```

**Controller logic for EditDestination POST (save command):**
- If `DestinationType == "store"`: build destination as `store-prism:{SelectedStoreId}:{StorePaymentMethod}`
- If `DestinationType == "address"`: use `AddressValue` directly
- Validate destination via `IPluginHookService.ApplyFilter("prism-destination-validate", destination)`
- Check alias name uniqueness (if new or renamed)
- Save to `settings.Destinations[aliasId]` = new `PrismDestination { Destination, SatThreshold, Reserve }`
- If renamed (OriginalId != Id): remove old key, update all splits referencing old alias name

**Controller logic for EditDestination POST (delete command):**
- Check not in use (any split references this alias)
- Remove from `settings.Destinations`
- Save via `SatBreaker.UpdatePrismSettingsForStore()`

**Step 2: Commit**

```bash
git add Plugins/BTCPayServer.Plugins.Prism/Views/Shared/Prism/EditDestination.cshtml
git commit -m "feat(prism): add destination alias edit page"
```

---

## Task 7: Create the JavaScript for Dynamic Lists and Source Toggle

**Files:**
- Create: `Plugins/BTCPayServer.Plugins.Prism/Resources/js/prism.js` (or inline in Edit.cshtml `@section PageFootContent`)

**Step 1: Write the JavaScript**

Two concerns, ~60 lines total:

**1. Source type field toggling** (~15 lines):
When the source type dropdown changes, show/hide the relevant field groups.

```javascript
document.addEventListener("DOMContentLoaded", function () {
    // Source type toggle
    document.querySelectorAll(".source-type-select").forEach(function (select) {
        function toggle() {
            var card = select.closest("[data-split-card]");
            var type = select.value;
            card.querySelectorAll("[data-source-fields]").forEach(function (el) {
                el.style.display = el.getAttribute("data-source-fields") === type ? "" : "none";
            });
        }
        select.addEventListener("change", toggle);
        toggle();
    });
});
```

The split partial uses `data-source-fields="catchall"`, `data-source-fields="lnaddress"`, etc. on field groups.

**2. Template cloning for add split / add destination** (~40 lines):

Following the NIP05 pattern, but adapted for nested structures. Since we use server-side add/remove via command buttons (page reload), this JS is only needed if we want client-side add/remove without reload.

Per the design doc, add/remove uses command buttons (server-side). So the JS template cloning is **optional** — we can add it later as a UX enhancement. For now, all add/remove is via form submit.

The only JS needed immediately is the source type toggle and the destination type toggle on the EditDestination page.

**Step 2: Commit**

```bash
git add Plugins/BTCPayServer.Plugins.Prism/Views/Shared/Prism/Edit.cshtml
git commit -m "feat(prism): add source type toggle JS"
```

---

## Task 8: Update Plugin Registration and Navigation

**Files:**
- Modify: `Plugins/BTCPayServer.Plugins.Prism/PrismPlugin.cs`
- Modify: `Plugins/BTCPayServer.Plugins.Prism/Views/Shared/PrismNav.cshtml`

**Step 1: Update PrismPlugin.cs**

Remove `AddServerSideBlazor()` call. Update the nav extension if path changes.

Change:
```csharp
applicationBuilder.AddServerSideBlazor(o => o.DetailedErrors = true);
```
Remove this line entirely (or check if any other plugin in the solution uses Blazor — if so, keep it but it's not Prism's responsibility).

The nav extension `applicationBuilder.AddUIExtension("store-integrations-nav", "PrismNav");` stays the same.

**Step 2: Update PrismNav.cshtml**

Update the nav link to point to the new MVC action. Current nav already points to `asp-controller="Prism" asp-action="Edit"` which matches our new controller. Just verify the route works.

If the controller route changed from `stores/{storeId}/plugins/prism` to `plugins/{storeId}/prism`, update accordingly. The current controller uses `[Route("stores/{storeId}/plugins/prism")]` with `[HttpGet("edit")]`, so the nav links to `/stores/{storeId}/plugins/prism/edit`.

For the new MVC controller, change the route to `[Route("plugins/{storeId}/prism")]` with `[HttpGet("")]` so the URL is `/plugins/{storeId}/prism`. Update the nav partial to match:

```cshtml
<a asp-controller="Prism" asp-action="Edit" asp-route-storeId="@storeId"
   class="nav-link @ViewData.IsActivePage("Prism")" id="Nav-Prism"
   permission="@Policies.CanModifyStoreSettings">
```

Or use the `layout-menu-item` pattern from Stripe:
```cshtml
<a permission="@Policies.CanModifyStoreSettings"
   layout-menu-item="Prism"
   asp-controller="Prism"
   asp-action="Edit"
   asp-route-storeId="@storeId">
```

**Step 3: Commit**

```bash
git add Plugins/BTCPayServer.Plugins.Prism/PrismPlugin.cs
git add Plugins/BTCPayServer.Plugins.Prism/Views/Shared/PrismNav.cshtml
git commit -m "feat(prism): update plugin registration, remove Blazor dependency"
```

---

## Task 9: Update _ViewImports and Remove Old Blazor Host View

**Files:**
- Modify: `Plugins/BTCPayServer.Plugins.Prism/Views/_ViewImports.cshtml`
- Delete: `Plugins/BTCPayServer.Plugins.Prism/Views/Prism/Edit.cshtml` (the old Blazor host)

**Step 1: Update _ViewImports.cshtml**

Current content:
```cshtml
@using BTCPayServer.Abstractions.Services
@inject Safe Safe
@addTagHelper *, Microsoft.AspNetCore.Mvc.TagHelpers
@addTagHelper *, BTCPayServer
@addTagHelper *, BTCPayServer.Abstractions
```

Add the Prism ViewModels namespace and Extensions:
```cshtml
@using BTCPayServer.Abstractions.Services
@using BTCPayServer.Abstractions.Extensions
@using BTCPayServer.Plugins.Prism.ViewModels
@inject Safe Safe
@addTagHelper *, Microsoft.AspNetCore.Mvc.TagHelpers
@addTagHelper *, BTCPayServer
@addTagHelper *, BTCPayServer.Abstractions
```

**Step 2: Delete old Blazor host view**

Delete `Plugins/BTCPayServer.Plugins.Prism/Views/Prism/Edit.cshtml` which contained the `Html.RenderComponentAsync<PrismEdit>()` call.

**Step 3: Commit**

```bash
git add Plugins/BTCPayServer.Plugins.Prism/Views/_ViewImports.cshtml
git rm Plugins/BTCPayServer.Plugins.Prism/Views/Prism/Edit.cshtml
git commit -m "refactor(prism): update ViewImports, remove Blazor host view"
```

---

## Task 10: Delete All Blazor Components

**Files:**
- Delete: All files in `Plugins/BTCPayServer.Plugins.Prism/Components/`

**Step 1: Delete all Blazor component files**

```bash
git rm Plugins/BTCPayServer.Plugins.Prism/Components/PrismEdit.razor
git rm Plugins/BTCPayServer.Plugins.Prism/Components/PrismSplit.razor
git rm Plugins/BTCPayServer.Plugins.Prism/Components/PrismBalances.razor
git rm Plugins/BTCPayServer.Plugins.Prism/Components/DestinationPicker.razor
git rm Plugins/BTCPayServer.Plugins.Prism/Components/DestinationInput.razor
git rm Plugins/BTCPayServer.Plugins.Prism/Components/DestinationDisplay.razor
git rm Plugins/BTCPayServer.Plugins.Prism/Components/SourceEditor.razor
git rm Plugins/BTCPayServer.Plugins.Prism/Components/DestinationManager.razor
git rm Plugins/BTCPayServer.Plugins.Prism/Components/PrismDestinationEditor.razor
git rm Plugins/BTCPayServer.Plugins.Prism/Components/ValidationMessage2.razor
```

Also delete any `.razor.cs` code-behind files if they exist as separate files (the exploration showed they may be inline `@code` blocks).

**Step 2: Commit**

```bash
git commit -m "refactor(prism): delete all Blazor components (replaced by MVC views)"
```

---

## Task 11: Build and Smoke Test

**Step 1: Build the project**

```bash
dotnet build Plugins/BTCPayServer.Plugins.Prism/BTCPayServer.Plugins.Prism.csproj
```

Fix any compilation errors (missing usings, type mismatches, etc.)

**Step 2: Commit any build fixes**

```bash
git add -A
git commit -m "fix(prism): resolve build errors from MVC conversion"
```

---

## Task 12: Manual Integration Testing Checklist

Verify each of these flows works correctly by running BTCPay Server locally:

1. **Navigation**: Prism appears in store integrations nav, clicking navigates to settings page
2. **Global settings**: Toggle enabled, change threshold and reserve, save → values persist on reload
3. **Add catch-all split**: Add split → select catch-all → select "All payments" → add destination from dropdown → set percentage → save
4. **Add LN address split**: Add split → select LN address → enter username → add destination → save
5. **Add POS split**: Add split → select POS Product → select app → select product → add destination → save
6. **Add wallet transfer**: Add wallet transfer → set payment method, amount, frequency, day → add destination → save
7. **Remove split**: Remove button on existing split → confirm removed after save
8. **Add/remove destinations on split**: Add multiple destinations, remove one, verify percentages
9. **Destination management**: Navigate to Manage Destinations → create new alias → verify appears in dropdowns
10. **Edit destination**: Edit existing alias → change type/address → save → verify updated in dropdowns
11. **Delete destination**: Delete unused alias → confirm removed
12. **Balance editing**: If balances exist, edit amount → update → verify changed
13. **Payout cancellation**: If pending payout exists, cancel → verify balance credited back
14. **Send Now**: On wallet transfer split, click Send Now → verify event triggered
15. **Version conflict**: Open settings in two tabs, save in one, try save in other → should show error
16. **Validation**: Try saving with duplicate sources, percentages > 100%, empty destinations → should show errors

---

## Summary

| Task | Description | Key Files |
|------|-------------|-----------|
| 1 | Create view models | `ViewModels/PrismViewModel.cs` |
| 2 | Create MVC controller | `PrismController.cs` |
| 3 | Create main Edit view | `Views/Shared/Prism/Edit.cshtml` |
| 4 | Create split editor partial | `Views/Shared/Prism/_SplitEditor.cshtml` |
| 5 | Create balances section partial | `Views/Shared/Prism/_BalancesSection.cshtml` |
| 6 | Create edit destination page | `Views/Shared/Prism/EditDestination.cshtml` |
| 7 | Add JavaScript | Source type toggle, dest type toggle |
| 8 | Update plugin registration & nav | `PrismPlugin.cs`, `PrismNav.cshtml` |
| 9 | Update ViewImports, remove Blazor host | `_ViewImports.cshtml` |
| 10 | Delete Blazor components | `Components/*.razor` |
| 11 | Build and fix | Compilation |
| 12 | Manual integration testing | All flows |
