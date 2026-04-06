# Electrum Plugin Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a BTCPay Server plugin that replaces NBXplorer with ElectrumX/Fulcrum as the blockchain backend via Shadow & Replace DI pattern.

**Architecture:** Plugin removes NBXplorer DI registrations and re-registers Electrum-backed implementations of the same concrete types. A custom TCP/TLS JSON-RPC client speaks the Electrum protocol. All state persisted in Postgres under `electrum` schema.

**Tech Stack:** C# / .NET 10, EF Core + Npgsql, NBitcoin, NBXplorer.Client (model types only), Razor views

---

### Task 1: Project Scaffolding

**Files:**
- Create: `Plugins/BTCPayServer.Plugins.Electrum/BTCPayServer.Plugins.Electrum.csproj`
- Create: `Plugins/BTCPayServer.Plugins.Electrum/ElectrumPlugin.cs`
- Create: `Plugins/BTCPayServer.Plugins.Electrum/ElectrumSettings.cs`

### Task 2: Electrum Protocol Client

**Files:**
- Create: `Plugins/BTCPayServer.Plugins.Electrum/Electrum/ElectrumClient.cs`
- Create: `Plugins/BTCPayServer.Plugins.Electrum/Electrum/ElectrumModels.cs`

### Task 3: EF Core Database Schema

**Files:**
- Create: `Plugins/BTCPayServer.Plugins.Electrum/Data/ElectrumDbContext.cs`
- Create: `Plugins/BTCPayServer.Plugins.Electrum/Data/ElectrumDbContextFactory.cs`
- Create: `Plugins/BTCPayServer.Plugins.Electrum/Data/Models.cs`

### Task 4: Wallet Tracking Engine

**Files:**
- Create: `Plugins/BTCPayServer.Plugins.Electrum/Electrum/ElectrumWalletTracker.cs`
- Create: `Plugins/BTCPayServer.Plugins.Electrum/Electrum/ScriptHashUtility.cs`

### Task 5: Shadow Services

**Files:**
- Create: `Plugins/BTCPayServer.Plugins.Electrum/Services/ElectrumExplorerClientProvider.cs`
- Create: `Plugins/BTCPayServer.Plugins.Electrum/Services/ElectrumBTCPayWallet.cs`
- Create: `Plugins/BTCPayServer.Plugins.Electrum/Services/ElectrumBTCPayWalletProvider.cs`
- Create: `Plugins/BTCPayServer.Plugins.Electrum/Services/ElectrumListener.cs`
- Create: `Plugins/BTCPayServer.Plugins.Electrum/Services/ElectrumStatusMonitor.cs`
- Create: `Plugins/BTCPayServer.Plugins.Electrum/Services/ElectrumFeeProvider.cs`
- Create: `Plugins/BTCPayServer.Plugins.Electrum/Services/ElectrumSyncSummaryProvider.cs`
- Create: `Plugins/BTCPayServer.Plugins.Electrum/Services/ElectrumConnectionFactory.cs`

### Task 6: Admin UI

**Files:**
- Create: `Plugins/BTCPayServer.Plugins.Electrum/Views/Electrum/Settings.cshtml`
- Create: `Plugins/BTCPayServer.Plugins.Electrum/Views/Shared/Electrum/NavExtension.cshtml`
- Create: `Plugins/BTCPayServer.Plugins.Electrum/Views/Shared/Electrum/ElectrumSyncSummary.cshtml`
- Create: `Plugins/BTCPayServer.Plugins.Electrum/Views/_ViewImports.cshtml`
- Create: `Plugins/BTCPayServer.Plugins.Electrum/Controllers/UIElectrumController.cs`

### Task 7: Plugin DI Registration & Wiring

**Files:**
- Modify: `Plugins/BTCPayServer.Plugins.Electrum/ElectrumPlugin.cs`

### Task 8: Build Verification

- Verify the plugin builds
- Fix any compilation errors
