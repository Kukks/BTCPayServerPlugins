# Prism Plugin: Blazor to MVC Conversion

**Date:** 2026-02-14
**Status:** Approved
**Scope:** Replace Prism plugin's Blazor UI with standard BTCPay MVC pattern

## Problem

The Prism plugin uses a complex Blazor Server UI (~3,500 lines across 10 components) that suffers from:
- Validation not capturing form field values reliably
- UI state getting out of sync with the model (stale data, select elements not binding)
- Sync-over-async hacks (`Task.Run`) for validation through plugin hooks
- Complex inline editing state management across multiple component layers

These are structural Blazor issues — the framework requires careful state synchronization that's hard to get right in a plugin context.

## Decision

Convert to standard ASP.NET Core MVC with server-side rendering, matching the pattern used by every other BTCPay plugin (Stripe, NIP05, Blink, etc.). Full-page forms with the `command` parameter pattern. No client-side state management.

## Architecture

### Approach: Single-Page MVC with Command Pattern

One main settings page with sections for global settings, splits, and balances/payouts. A separate page for destination alias management. All interactions are form POSTs with Post-Redirect-Get.

### What Changes (UI Only)

**Backend stays untouched:**
- `SatBreaker.cs` — zero modifications
- All validators (`StoreDestinationValidator`, `LNURLPrismDestinationValidator`, `OnChainPrismDestinationValidator`, `OpenSatsDestinationValidator`)
- All claim creators (`StorePrismClaimCreate`, `LNURLPrismClaimCreate`, `OnChainPrismClaimCreate`, `OpenSatsPrismClaimCreate`)
- Data models (`PrismSettings`, `Split`, `PrismSplit`, `PendingPayout`)
- Plugin hooks (`prism-destination-validate`, `prism-claim-create`)
- Data format in database — 100% compatible, no migration needed

**Delete (Blazor components):**
- `PrismEdit.razor/.razor.cs` (870 lines)
- `PrismSplit.razor/.razor.cs` (597 lines)
- `PrismBalances.razor/.razor.cs` (176 lines)
- `DestinationPicker.razor/.razor.cs` (498 lines)
- `DestinationInput.razor/.razor.cs` (276 lines)
- `DestinationDisplay.razor/.razor.cs` (227 lines)
- `SourceEditor.razor/.razor.cs` (236 lines)
- `DestinationManager.razor/.razor.cs` (250 lines)
- `PrismDestinationEditor.razor/.razor.cs` (140 lines)
- `ValidationMessage2.razor/.razor.cs` (38 lines)

**Create (MVC):**
- `PrismController.cs` — full controller replacing minimal existing one
- `ViewModels/PrismViewModel.cs` — main view model + sub-models
- `Views/Shared/Prism/Edit.cshtml` — main settings page
- `Views/Shared/Prism/_SplitEditor.cshtml` — partial for one split card
- `Views/Shared/Prism/_BalancesSection.cshtml` — partial for balances/payouts
- `Views/Shared/Prism/EditDestination.cshtml` — destination alias create/edit page
- `Views/Shared/Prism/Nav.cshtml` — navigation (adapt existing)

**Modify:**
- `PrismPlugin.cs` — remove Blazor registrations

## View Models

```csharp
public class PrismViewModel
{
    // Global settings
    public bool Enabled { get; set; }
    public long SatThreshold { get; set; } = 100;
    public decimal Reserve { get; set; } = 2;

    // Splits (indexed form binding)
    public List<SplitViewModel> Splits { get; set; } = new();

    // Balances & payouts (display)
    public Dictionary<string, long> DestinationBalances { get; set; } = new();
    public Dictionary<string, PendingPayout> PendingPayouts { get; set; } = new();

    // Destination aliases (for dropdowns)
    public Dictionary<string, PrismDestination> Destinations { get; set; } = new();

    // Display-only (populated by controller)
    public List<SelectListItem> AvailableStores { get; set; } = new();
    public List<SelectListItem> AvailableLnAddresses { get; set; } = new();
    public List<SelectListItem> AvailableApps { get; set; } = new();
    public Dictionary<string, List<SelectListItem>> AppProducts { get; set; } = new();
    public bool HasLnProcessor { get; set; }
    public bool HasChainProcessor { get; set; }
}

public class SplitViewModel
{
    public string Source { get; set; }
    public string SourceType { get; set; } // "catchall", "lnaddress", "pos", "wallettransfer"

    // Catch-all fields
    public string CatchAllType { get; set; } // "All", "LN", "CHAIN"

    // LN address fields
    public string LnAddress { get; set; }

    // POS fields
    public string PosAppId { get; set; }
    public string PosProductId { get; set; }
    public string PosPaymentFilter { get; set; }

    // Wallet transfer fields
    public string TransferPaymentMethod { get; set; }
    public string TransferAmount { get; set; }
    public string TransferFrequency { get; set; } // "D", "W", "M"
    public string TransferDay { get; set; }

    // Destinations
    public List<SplitDestinationViewModel> Destinations { get; set; } = new();
}

public class SplitDestinationViewModel
{
    public string Destination { get; set; } // alias ID or raw destination
    public decimal Percentage { get; set; }
}
```

## Controller Routes

```
GET  /plugins/{storeId}/prism                    → Edit (main page)
POST /plugins/{storeId}/prism                    → Edit (command dispatch)
GET  /plugins/{storeId}/prism/destination?id=xxx → EditDestination
POST /plugins/{storeId}/prism/destination        → SaveDestination / DeleteDestination
POST /plugins/{storeId}/prism/update-balance     → Update balance
POST /plugins/{storeId}/prism/cancel-payout      → Cancel payout
POST /plugins/{storeId}/prism/send-now           → Trigger immediate wallet transfer
```

### Main Form Commands

| Command | Action |
|---------|--------|
| `save` | Validate all, save settings |
| `add-split` | Append empty split, return view |
| `remove-split:{index}` | Remove split at index, return view |
| `add-destination:{splitIndex}` | Append empty destination to split |
| `remove-destination:{splitIndex}:{destIndex}` | Remove destination from split |

### Separate Action Endpoints

Balance/payout actions are separate POST endpoints (not commands on the main form) so they work independently from settings save:

- `update-balance` — updates a single destination balance, redirects back
- `cancel-payout` — cancels payout and credits balance, redirects back
- `send-now` — triggers immediate wallet transfer, redirects back

## Page Layout

### Main Settings Page (Edit.cshtml)

```
┌─────────────────────────────────────────────────┐
│ [sticky header]  Prism Settings         [Save]  │
├─────────────────────────────────────────────────┤
│ _StatusMessage                                  │
├─────────────────────────────────────────────────┤
│ GLOBAL SETTINGS                                 │
│   [toggle] Enabled                              │
│   Sat Threshold: [____]                         │
│   Reserve %:     [____]                         │
├─────────────────────────────────────────────────┤
│ SPLITS                          [+ Add Split]   │
│ ┌─────────────────────────────────────────────┐ │
│ │ Split #1                         [Remove]   │ │
│ │ Source Type: [dropdown]                     │ │
│ │ (type-specific fields)                      │ │
│ │ Destinations:        [+ Add Destination]    │ │
│ │   [alias-dropdown] [__%] [Remove]           │ │
│ │   [alias-dropdown] [__%] [Remove]           │ │
│ └─────────────────────────────────────────────┘ │
├─────────────────────────────────────────────────┤
│ DESTINATIONS               [Manage Destinations]│
├─────────────────────────────────────────────────┤
│ BALANCES & PAYOUTS                              │
│ ┌────────────┬────────┬─────────┬─────────────┐ │
│ │ Dest       │ Amount │ Status  │ Action      │ │
│ │ alice@...  │ 5,200  │ Accum.  │ [edit form] │ │
│ │ bob@...    │ 15,000 │ Pending │ [Cancel]    │ │
│ └────────────┴────────┴─────────┴─────────────┘ │
└─────────────────────────────────────────────────┘
```

### Destination Edit Page (EditDestination.cshtml)

Separate page for creating/editing a destination alias:
- Alias name field
- Type selector: Address vs Store Transfer
- Address mode: text input for LN address / LNURL / BTC address / xpub
- Store mode: store dropdown + payment method dropdown
- Optional threshold and reserve overrides
- Save / Delete / Cancel buttons

## JavaScript

Minimal JS (~60 lines total), no frameworks:

1. **Template cloning** (~40 lines) — standard BTCPay pattern for `[+ Add Split]` and `[+ Add Destination]` buttons. Clones `<template>` elements and rewrites `name` attribute indices.

2. **Source type toggle** (~15 lines) — shows/hides source-type-specific fields based on dropdown selection using `data-source-type` attributes. No server round-trip.

## Validation

All validation moves to the controller (server-side):

1. **Model binding** — ASP.NET binds indexed form fields to view model lists
2. **Source uniqueness** — controller checks no duplicate sources
3. **Percentage validation** — sum of destination percentages per split <= 100%
4. **Type compatibility** — LN destinations for LN sources, on-chain for on-chain
5. **Async destination validation** — controller calls `IPluginHookService.ApplyFilter("prism-destination-validate", ...)` — works naturally in async controller action, no sync-over-async hack needed
6. **ModelState errors** — rendered via `asp-validation-for` tag helpers

## Migration Strategy

### Phase 1: Build MVC Alongside Blazor
1. Create view models
2. Create controller (temporary route `/prism2`)
3. Create all views and partials
4. Create JS for template cloning and source toggle
5. Test against same backend

### Phase 2: Validate & Switch
6. Test all flows end-to-end
7. Switch route from `/prism2` to `/prism`
8. Update `PrismPlugin.cs` — remove Blazor registrations
9. Update navigation

### Phase 3: Clean Up
10. Delete all `.razor` and `.razor.cs` files
11. Delete Blazor host page
12. Remove Blazor-only CSS/JS
13. Remove `AddServerSideBlazor()` if unused by other plugins

## Risk Assessment

**Low risk:**
- Backend is completely untouched
- Data format is identical — no migration
- Standard MVC patterns proven across all other BTCPay plugins
- Parallel development with temporary route allows comparison testing

**Known trade-offs:**
- Every add/remove action on splits requires page reload (acceptable for a settings page)
- No real-time balance updates (page shows data at load time — refresh to see changes)
- Destination creation requires navigating to separate page and back
