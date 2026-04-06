# Electrum Plugin Design Spec

**Date:** 2026-04-06
**Status:** Draft
**Author:** Andrew Camilleri + Claude

## Overview

A BTCPay Server plugin that replaces NBXplorer with an Electrum server (ElectrumX/Fulcrum) as the blockchain backend. The plugin shadows and replaces all NBXplorer service registrations with Electrum-backed implementations, achieving full wallet parity.

## Goals

- Full wallet parity with NBXplorer: tracking, UTXOs, balance, tx history, fee estimation, broadcasting, address generation, PSBT support, PayJoin
- Server-level Electrum configuration (one server per BTCPay instance)
- Postgres for all persistent state
- Custom Electrum protocol client (no external dependency)
- Handle arbitrary server downtime via state diffing on reconnect
- Minimal upstream BTCPay PR: make NBXplorer registration conditional

## Architecture: Shadow & Replace

The plugin removes NBXplorer DI registrations and re-registers Electrum-backed implementations of the same concrete types. BTCPay code consumes them without modification.

### Upstream PR (Minimal)

Wrap NBXplorer service registrations in `BTCPayServerServices.cs` behind a configuration flag:

```csharp
if (!configuration.GetValue<bool>("disable-nbxplorer", false))
{
    // existing NBXplorer registrations
}
```

When `disable-nbxplorer=true`, none of the NBXplorer services are registered.

### Service Replacement Matrix

| NBXplorer Service | Electrum Replacement | Role |
|---|---|---|
| `ExplorerClientProvider` / `IExplorerClientProvider` | `ElectrumExplorerClientProvider` | Client factory |
| `BTCPayWallet` | `ElectrumBTCPayWallet` | UTXO, balance, tx history, broadcast |
| `BTCPayWalletProvider` | `ElectrumBTCPayWalletProvider` | Wallet factory |
| `NBXplorerListener` | `ElectrumListener` | Payment detection via subscriptions |
| `NBXplorerWaiter` + `NBXplorerDashboard` | `ElectrumStatusMonitor` | Health/sync status |
| `NBXplorerConnectionFactory` | `ElectrumConnectionFactory` | DB access (plugin's own Postgres tables) |
| `NBXSyncSummaryProvider` | `ElectrumSyncSummaryProvider` | UI sync status |
| `NBxplorerFeeProvider` | `ElectrumFeeProvider` | Fee estimation |

### Internal Engine (not exposed to BTCPay)

```
┌─────────────────────────────────────────┐
│  ElectrumClient (TCP/TLS, JSON-RPC)     │
│  - Connection lifecycle, reconnect      │
│  - Request/response correlation         │
│  - Subscription dispatch                │
├─────────────────────────────────────────┤
│  ElectrumWalletTracker                  │
│  - Derivation & gap limit management    │
│  - scripthash.subscribe management      │
│  - UTXO state diffing                   │
│  - Postgres persistence                 │
├─────────────────────────────────────────┤
│  ElectrumDbContext (EF Core / Postgres) │
│  - TrackedWallets, Transactions, UTXOs  │
│  - SyncState, AddressPool              │
└─────────────────────────────────────────┘
```

## Electrum Protocol Client

### Wire Protocol

JSON-RPC 2.0 over TCP with `\n` delimiters. Optional TLS wrapping.

### Required Methods

| Method | Purpose |
|---|---|
| `server.version` | Protocol negotiation (require 1.4+) |
| `server.features` | Network validation (mainnet/testnet/regtest) |
| `server.ping` | Keep-alive |
| `blockchain.headers.subscribe` | New block notifications |
| `blockchain.scripthash.subscribe` | Address activity notifications |
| `blockchain.scripthash.get_history` | Full tx history for an address |
| `blockchain.scripthash.listunspent` | UTXOs for an address |
| `blockchain.scripthash.get_balance` | Balance for a scripthash |
| `blockchain.transaction.get` | Raw transaction by txid |
| `blockchain.transaction.broadcast` | Broadcast raw tx |
| `blockchain.estimatefee` | Fee estimation |

### Connection Lifecycle

- Single `TcpClient` + optional `SslStream`
- `StreamReader`/`StreamWriter` with `\n` framing
- Request IDs via `Interlocked.Increment` for correlation
- `ConcurrentDictionary<int, TaskCompletionSource<JsonElement>>` for pending requests
- Dedicated read loop task that dispatches responses (has `id`) vs notifications (no `id`)
- Auto-reconnect with exponential backoff: 1s, 2s, 4s, 8s... max 60s
- On reconnect: re-subscribe all active scripthashes and headers
- `server.ping` every 60s as keepalive

## Postgres Schema

All tables live in the `electrum` schema.

### `electrum.tracked_wallets`

| Column | Type | Description |
|---|---|---|
| `id` | TEXT PK | Derivation strategy string |
| `crypto_code` | TEXT | e.g. "BTC" |
| `derivation_strategy` | TEXT | Full derivation strategy |
| `receive_gap_index` | INT | Highest derived receive index |
| `change_gap_index` | INT | Highest derived change index |
| `created_at` | TIMESTAMPTZ | Creation timestamp |

### `electrum.tracked_addresses`

| Column | Type | Description |
|---|---|---|
| `scripthash` | TEXT PK | Electrum scripthash (SHA256 of scriptpubkey, reversed hex) |
| `wallet_id` | TEXT FK | References tracked_wallets |
| `key_path` | TEXT | e.g. "0/5" or "1/3" |
| `script_pubkey` | BYTEA | Raw script |
| `address` | TEXT | Human-readable address |
| `is_change` | BOOLEAN | Change chain flag |
| `is_used` | BOOLEAN | Whether address has been used |

### `electrum.utxos`

| Column | Type | Description |
|---|---|---|
| `outpoint` | TEXT PK | txid:vout |
| `wallet_id` | TEXT FK | References tracked_wallets |
| `scripthash` | TEXT FK | References tracked_addresses |
| `txid` | TEXT | Transaction ID |
| `vout` | INT | Output index |
| `value` | BIGINT | Satoshis |
| `script_pubkey` | BYTEA | Output script |
| `key_path` | TEXT | Derivation path |
| `block_height` | BIGINT | NULL = unconfirmed |
| `block_hash` | TEXT | Block hash |
| `seen_at` | TIMESTAMPTZ | First seen |
| `is_spent` | BOOLEAN | Spent flag |
| `spending_txid` | TEXT | Spending transaction |

### `electrum.transactions`

| Column | Type | Description |
|---|---|---|
| `txid` | TEXT | Transaction ID (composite PK) |
| `wallet_id` | TEXT FK | References tracked_wallets (composite PK) |
| `raw_tx` | BYTEA | Raw transaction bytes |
| `block_height` | BIGINT | NULL = unconfirmed, -1 = conflicted |
| `block_hash` | TEXT | Block hash |
| `fee` | BIGINT | Fee in satoshis |
| `seen_at` | TIMESTAMPTZ | First seen |
| `balance_change` | BIGINT | Net satoshi change for this wallet |

### `electrum.sync_state`

| Column | Type | Description |
|---|---|---|
| `key` | TEXT PK | State key |
| `value` | TEXT | State value |
| `updated_at` | TIMESTAMPTZ | Last updated |

### Indexes

```sql
CREATE INDEX idx_utxos_wallet ON electrum.utxos(wallet_id) WHERE NOT is_spent;
CREATE INDEX idx_utxos_scripthash ON electrum.utxos(scripthash);
CREATE INDEX idx_transactions_wallet ON electrum.transactions(wallet_id);
CREATE INDEX idx_tracked_addresses_wallet ON electrum.tracked_addresses(wallet_id);
```

## Wallet Tracking Engine

### Initial Tracking

1. `TrackAsync(DerivationStrategyBase)` called → store wallet in `tracked_wallets`
2. Derive first 20 receive addresses (m/0/0 through m/0/19) + 20 change addresses (m/1/0 through m/1/19)
3. Compute Electrum scripthash for each: `SHA256(scriptpubkey)` with bytes reversed, hex-encoded
4. Store in `tracked_addresses`
5. Subscribe each via `blockchain.scripthash.subscribe`
6. Fetch initial state: `listunspent` + `get_history` for each scripthash
7. Store UTXOs and transactions in Postgres

### On scripthash.subscribe Notification

1. Fetch `get_history` for the notified scripthash
2. Diff against stored transactions for that address
3. For new transactions: fetch raw tx via `transaction.get`, parse with NBitcoin, compute balance change
4. Update UTXOs via `listunspent`, mark spent outputs
5. If the address is near the gap limit edge → derive more addresses, subscribe them
6. Mark address as used in `tracked_addresses`
7. Fire `NewOnChainTransactionEvent` via BTCPay's `EventAggregator`

### On headers.subscribe (New Block)

1. Update stored tip height in `sync_state`
2. Re-check all unconfirmed transactions for confirmation status
3. Update UTXO confirmation heights
4. Fire `NewBlockEvent` via `EventAggregator`

### Reconnect / Restart Recovery

1. Load all tracked wallets from Postgres
2. Re-derive all addresses within gap window
3. Re-subscribe all scripthashes
4. For each scripthash: fetch current history, diff against stored state
5. Process any missed transactions (new UTXOs, spent UTXOs, confirmations)
6. This handles arbitrary downtime — we always diff current vs stored

### Gap Limit Management

- Default gap limit: 20 (configurable)
- Track `highest_used_index` per chain (receive/change) per wallet
- Always maintain derived addresses up to `highest_used_index + GAP_LIMIT`
- When address at index `i` is used and `i >= highest_used_index`:
  - Set `highest_used_index = i`
  - Derive new addresses up to `i + GAP_LIMIT`
  - Subscribe new scripthashes
- Both receive (m/0/i) and change (m/1/i) chains tracked independently

## Shadow Service Implementations

### ElectrumExplorerClientProvider : IExplorerClientProvider

- Returns a shimmed `ExplorerClient` that throws `NotSupportedException` for direct method calls
- Exists to satisfy DI resolution — most BTCPay code goes through `BTCPayWallet`, not `ExplorerClient` directly
- `IsAvailable()` delegates to `ElectrumStatusMonitor` state

### ElectrumBTCPayWallet (subclasses BTCPayWallet, overrides virtual methods where possible; for non-virtual methods, the base class constructor receives a shimmed ExplorerClient that delegates to our Electrum engine)

| Method | Electrum Implementation |
|---|---|
| `ReserveAddressAsync` | Derive next unused address from tracked wallet, mark reserved in DB |
| `GetChangeAddressAsync` | Derive next change address |
| `TrackAsync` | Delegate to `ElectrumWalletTracker` |
| `GetUnspentCoins` | Query `electrum.utxos` WHERE NOT is_spent, map to `ReceivedCoin[]` |
| `GetBalance` | Sum UTXOs (confirmed/unconfirmed), construct `GetBalanceResponse` |
| `FetchTransactionHistory` | Query `electrum.transactions`, map to `TransactionHistoryLine[]` |
| `FetchTransaction` | Query DB by txid+wallet, map to `TransactionHistoryLine` |
| `GetTransactionAsync` | Query DB or fetch via `transaction.get`, construct `TransactionResult` |
| `BroadcastTransactionsAsync` | `transaction.broadcast` via `ElectrumClient`, construct `BroadcastResult[]` |
| `GetBumpableTransactions` | Query unconfirmed txs, return `BumpableTransactions` (limited — see Limitations) |
| `InvalidateCache` | Clear any in-memory caches |

### ElectrumListener : IHostedService

- Replaces `NBXplorerListener`
- On start: initializes `ElectrumWalletTracker`, begins subscriptions
- Processes subscription callbacks → matches against open invoices → records payments via `PaymentService`
- Reuses same invoice-matching logic patterns from `NBXplorerListener`
- Polls for pending invoice payments periodically

### ElectrumStatusMonitor : IHostedService

- Replaces both `NBXplorerWaiter` (per-network) and populates `NBXplorerDashboard`
- Implements same state machine: `NotConnected` → `Synching` → `Ready`
- Constructs `StatusResult` from Electrum `server.features` + current tip
- Publishes `NBXplorerStateChangedEvent` on state transitions
- Constructs `GetMempoolInfoResponse` (limited — Electrum doesn't expose full mempool stats)

### ElectrumFeeProvider : IFeeProvider

- `GetFeeRateAsync(blockTarget)` → `blockchain.estimatefee(blockTarget)`
- Converts BTC/kB (Electrum response format) to `FeeRate` (sat/vB)
- Caches results for 30 seconds
- Falls back to hardcoded minimum (1 sat/vB) if Electrum returns -1 (can't estimate)

### ElectrumSyncSummaryProvider : ISyncSummaryProvider

- Returns sync status based on `ElectrumStatusMonitor` state
- Provides chain height from Electrum tip
- Custom Razor partial for sync status display

### ElectrumConnectionFactory : IHostedService

- Provides `OpenConnection()` returning `NpgsqlConnection` to BTCPay's Postgres (same DB, `electrum` schema)
- Replaces `NBXplorerConnectionFactory` which connects to NBXplorer's separate Postgres

## Plugin Registration

```csharp
public class ElectrumPlugin : BaseBTCPayServerPlugin
{
    public override void Execute(IServiceCollection services)
    {
        // Remove NBXplorer services (safe if they don't exist)
        RemoveServiceByImplementation<ExplorerClientProvider>(services);
        RemoveServiceByType<IExplorerClientProvider>(services);
        RemoveServiceByImplementation<NBXplorerConnectionFactory>(services);
        RemoveServiceByImplementation<NBXplorerDashboard>(services);
        RemoveHostedService<NBXplorerListener>(services);
        RemoveHostedService<NBXplorerWaiters>(services);
        RemoveServiceByType<ISyncSummaryProvider>(services);
        RemoveServiceByType<IFeeProvider>(services);
        // Remove BTCPayWalletProvider and BTCPayWallet registrations

        // Register Electrum engine
        services.AddSingleton<ElectrumClient>();
        services.AddSingleton<ElectrumWalletTracker>();
        services.AddSingleton<ElectrumStatusMonitor>();

        // Register shadow services
        services.AddSingleton<NBXplorerDashboard>();
        services.AddSingleton<IExplorerClientProvider, ElectrumExplorerClientProvider>();
        services.AddSingleton<ElectrumBTCPayWalletProvider>();
        services.AddHostedService<ElectrumListener>();
        services.AddHostedService<ElectrumStatusMonitor>();
        services.AddSingleton<ISyncSummaryProvider, ElectrumSyncSummaryProvider>();
        services.AddSingleton<IFeeProvider, ElectrumFeeProvider>();

        // DB context
        services.AddDbContext<ElectrumDbContext>((provider, o) =>
        {
            var connectionString = provider.GetRequiredService<IConfiguration>()
                .GetConnectionString("Default");
            o.UseNpgsql(connectionString);
        });

        // Admin UI
        services.AddUIExtension("server-nav", "Electrum/NavExtension");
    }
}
```

## Configuration

### Server-Level Settings

```csharp
public class ElectrumSettings
{
    public string Server { get; set; }      // host:port (e.g. "electrum.example.com:50002")
    public bool UseTls { get; set; }        // default: true
    public int GapLimit { get; set; }       // default: 20
    public string CryptoCode { get; set; }  // default: "BTC"
}
```

Stored via BTCPay's `SettingsRepository`.

### Admin UI

Settings page under Server Settings → Electrum:
- Server URL input (host:port)
- TLS toggle
- Gap limit (advanced)
- Connection test button
- Status display (connected/syncing/block height/error)

## Limitations & Known Gaps

### 1. Mempool Entry Details
Electrum protocol has no `getmempoolentry` equivalent. RBF fee bump calculations in `GetBumpableTransactions` will be limited — we can identify unconfirmed transactions but cannot get ancestor/descendant fee information. `BumpableTransactions.Support` will return `NotCompatible` when RBF data isn't available.

### 2. PSBT Server-Side Validation
Electrum protocol doesn't handle PSBTs. BTCPay constructs PSBTs client-side using UTXO data (which we provide), so this mostly works. Server-side PSBT validation via RPC (`testmempoolaccept`) won't be available.

### 3. PayJoin
Requires UTXO locking and `testmempoolaccept`. UTXO locking works (we control UTXO state). `testmempoolaccept` is not available — PayJoin will work but without pre-broadcast validation.

### 4. Mempool Statistics
`GetMempoolInfoResponse` fields (size, bytes, usage, etc.) cannot be populated from Electrum. We'll provide zeroed/estimated values. This affects the dashboard mempool display only.

### 5. Reorg Handling
On a reorg, scripthash history changes. We detect this on the next `get_history` call and reconcile — transactions that disappeared are marked conflicted, new transactions are added. Deep reorgs (>6 blocks) may require a full rescan of affected wallets.

### 6. Fee Estimation Accuracy
Electrum's `blockchain.estimatefee` quality depends on the server implementation. Fulcrum provides good estimates; some ElectrumX instances return -1. Fallback to minimum fee rate (1 sat/vB) when unavailable.

## NuGet Dependencies

- `NBXplorer.Client` (5.0.5) — for model types (`UTXOChanges`, `TransactionResult`, `StatusResult`, etc.) and `DerivationStrategyBase`
- `Npgsql.EntityFrameworkCore.PostgreSQL` — EF Core provider
- `NBitcoin` — transaction parsing, derivation, script handling (already a transitive dependency)
- No external Electrum client library — we build our own

## Testing Strategy

- **Unit tests** for `ElectrumClient` protocol parsing (mock TCP stream)
- **Unit tests** for scripthash computation, gap limit logic, UTXO diffing
- **Integration tests** against a local Fulcrum instance (Docker)
- **End-to-end tests** via BTCPay's existing test infrastructure with Electrum backend swapped in
