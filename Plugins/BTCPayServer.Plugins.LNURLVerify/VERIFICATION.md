# LNURL Verify — verification runbook

The plugin is unit-tested (368 tests) and reviewed, but three things can only be confirmed by running
it. This is the concrete checklist to gain that confidence, ordered cheapest-first.

## 1. Unit tests (seconds, no infra)

```
dotnet test BTCPayServer.Plugins.LNURLVerify.Tests
```
Expected: 368 passed, and no warnings from plugin or test code. Covers capability decode, verify-support probe, receive guards +
settled-cache, the shared poller (incl. a 60-invoice concurrent settle/error stress), the full send
chain (parse → k1-refresh → bounds/balance → submit), connection-scoped reconciliation, persistence
save/restore (against a fake settings store), paymentOptions selection, verifyBatch batching (chunking,
414 halving, unsupported fallback, backoff), the GetInvoice cache, a self-signed BOLT11 pinning the
payment-hash byte order, the rail table and BIP321 merging, rail destination requests and their refusals,
destination polling through verifyBatch, provisioning rules, the rail payment-method handler, activation
planning, the checkout re-skin, CAIP-19 parsing and the per-namespace URIs, token options and their provisioning, token quotes and their refusals, the token payment method (per-network quotes, expiry, re-issue), token activation planning (on a tap only, never past a live quote), the asset tab's model, token providers and destination tags, and lnurl-server's FixedFloat rails as a
stock install sees them.

## 2. Receive integration — real BTCPay LNURL + regtest LN (ServerTester)

`BTCPayServer.Plugins.Tests/LNURLVerifyIntegrationTests.cs::Receives_via_lnurl_pay_and_lud21_verify`
uses a ServerTester store's own LN address as the verify-capable endpoint (BTCPay core serves LUD-21
verify by default) and drives the plugin client directly.

Bring up the tester stack (same as the other ServerTester tests), then:
```
dotnet test BTCPayServer.Plugins.Tests --filter "FullyQualifiedName~LNURLVerifyIntegrationTests"
```
Confirms: create invoice via the LN address → pay from the regtest node → the plugin reports `Paid` with
a validated preimage. **This is the single highest-value check** — it exercises the receive + verify path
end-to-end. The test compiles against the harness today but has not been executed.

## 2b. Settlement against a real lnurl-server (no Lightning node)

`BTCPayServer.Plugins.Tests/LNURLVerifyLnurlServerTests.cs` plays an lnurl-server wallet: it answers invoice
requests with self-signed regtest invoices and reports their preimages, and asserts the plugin learns both
settlements through `verifyBatch` with zero per-invoice verify requests. It needs an lnurl-server checkout at
or after `a4150f7` (no released image has verifyBatch yet):

```
cd <lnurl-server>; pnpm build:server
$env:PORT='3999'; $env:BASE_URL='http://127.0.0.1:3999'; pnpm start
# second terminal, from this repo:
$env:LNURL_SERVER_URL='http://127.0.0.1:3999'
dotnet test BTCPayServer.Plugins.Tests -p:StaticWebAssetsEnabled=false --filter "FullyQualifiedName~LNURLVerifyLnurlServerTests"
```

## 3. Send integration — needs LNbits (currently a `[Skip]` scaffold)

`Sends_via_lnurl_withdraw` is skipped: its LNbits bootstrap (`CreateLnbitsWithdrawWithPayLink`) is a stub.
To enable:
1. Add LNbits to the stack via `BTCPayServer.Plugins.Tests/docker-compose.lnurlverify.yml` (best-effort;
   the LND-backend cert/macaroon wiring is the part most likely to need adjustment).
2. Implement the stub: create a reusable LNURL-withdraw link **with a `payLink`** via the LNbits API and
   return its `lnurl`.
3. Remove the `[Skip]`; the test then pays a merchant-node invoice through the plugin's withdraw and
   asserts the merchant received it.

## 4. Live BTCPay — manual (the real acceptance test)

1. Load the plugin, configure a store's Lightning with `type=lnurl;value=<a verify-capable LN address>`
   (e.g. another BTCPay store's LN address). Confirm the store's Lightning **connect/validate** succeeds
   (this exercises `Validate()` + the verify-support probe).
2. Take a checkout; pay it; confirm the invoice settles with a preimage.
3. **Persistence across restart:** create a checkout, leave it unpaid, **restart BTCPay**, then pay it.
   Confirm the invoice still settles (this is the restart-survival path — re-seed on first `Create`).
4. If using a withdraw connection: run a payout and confirm it goes out; note that an uncertain
   (transport-failed) send stays in-progress by design (see README).

## Known caveats to keep in mind while verifying

- Clearnet only (no Tor). Ungraceful crash can lose ~10s of the newest invoices (persist throttle).
- The verify host must actually implement LUD-21 (BTCPay ≥ 2.x and blink-lnurl-server do); a non-verify
  endpoint is rejected at `Validate()`.

## 5. One Bitcoin checkout — acceptance runbook

Proves what the unit tests cannot: the checkout in a browser, payments recorded in the database, and the provisioner,
recorder and restart rebuild in a live host. Needs Docker, and an lnurl-server checkout with its `regtest` submodule.

Steps 2-4 run in the lnurl-server checkout; steps 1 and 5 run in this repository.

1. BTCPay: `docker compose -f submodules/btcpayserver/BTCPayServer.Tests/docker-compose.yml up -d dev`. Then write
   `submodules/btcpayserver/BTCPayServer/appsettings.dev.json` as `{"DEBUG_PLUGINS":"<built plugin dll path>"}` and run
   `dotnet run --project submodules/btcpayserver/BTCPayServer --launch-profile Bitcoin-HTTPS`.
2. Arkade regtest, from the lnurl-server checkout:
   `ARKD_VTXO_TREE_EXPIRY=6144 ARKD_UNILATERAL_EXIT_DELAY=512 ARKD_PUBLIC_UNILATERAL_EXIT_DELAY=512 ARKD_BOARDING_EXIT_DELAY=2048 ARKD_CHECKPOINT_EXIT_DELAY=1536 AUTOMINE_INTERVAL=0 PRICEFEED_PORT=18088 node regtest/regtest.mjs start --profile emulator`
   If another arkade-regtest stack already holds its ports, put `REGTEST_PROJECT`, `REGTEST_CONTAINER_PREFIX` and the
   port variables in a file and pass `--env <file>` to `start`; container names then carry the prefix (`lv-arkd` for
   `lv-`), and lnurl-server's `ARK_SERVER_URL` and `OFFLINE_EMULATOR_URL` take the `ARKD_PORT` and `EMULATOR_PORT`
   you chose.
3. Payer: `docker exec arkd ark receive` gives `offchain_address` and `boarding_address`. Fund it if `docker exec arkd ark balance`
   shows nothing offchain: `node regtest/regtest.mjs faucet <boarding_address> 0.01`, `node regtest/regtest.mjs mine 1`,
   `docker exec arkd ark settle --password secret`.
4. lnurl-server on port 80, because an address's callback URL drops the port: `pnpm build:server`, then
   `PORT=80 BASE_URL=http://localhost DB_PATH=<file> ALLOW_INSECURE_TOKEN_STORAGE=1 BOOTSTRAP_DOMAIN=localhost OFFLINE_COVENANT_DESTINATIONS=true ARK_SERVER_URL=http://localhost:7070 OFFLINE_EMULATOR_URL=http://localhost:7073 pnpm start`
5. The address `alice`, held open by a test-project helper that answers Lightning requests with self-signed invoices:
   `LNURL_SERVER_URL=http://localhost LNURL_ARKADE_ADDRESS=<offchain_address> LNURL_BOARDING_ADDRESS=<boarding_address> LNURL_RUNBOOK_MINUTES=120 dotnet test BTCPayServer.Plugins.Tests -p:StaticWebAssetsEnabled=false --filter "FullyQualifiedName~Serves_an_arkade_address_for_the_checkout_runbook"`
6. In BTCPay, create a store with default currency BTC and Lightning `type=lnurl;value=http://localhost/.well-known/lnurlp/alice`.
   Integrations → LNURL rails then lists the provisioned rails.

Count lnurl-server's destination requests with
`SELECT payment_option, COUNT(*) FROM settlements WHERE payment_option<>'lightning' GROUP BY payment_option` on its database.

| Scenario | Expected |
|---|---|
| Setup | Integrations → LNURL rails lists the provisioned rails. The store's Lightning setup page renders its LNURL section with no CSP error in the browser console: plugin views get their inline-script nonce only through `@addTagHelper *, BTCPayServer.Abstractions` in `_ViewImports.cshtml`. |
| A. Setting on, no wallet | Chips Lightning and Arkade. On-chain is refused, since lnurl-server's on-chain rail has no `verify`, and reloading never repeats that request. The QR carries `lightning=` and `ark=`. `docker exec arkd ark send --to <ark destination> --amount <sats> --password secret` settles the invoice and adds a row under "LNURL rail payments". |
| B. Setting off | Only Lightning is active on open; tapping Arkade costs exactly one destination request. |
| C. Store with a wallet | The on-chain rail is dropped. The checkout opens on-chain, the QR's path is the store's address, and the LNURL's on-chain option is never requested. |
| D. Partial payment | Pay part on-chain with cheat mode and mine a block. The Arkade chip is re-issued for the remainder with exactly one request, and paying it settles the invoice. |
| E. Restart | Pay an activated Arkade destination while BTCPay is stopped; after the restart the invoice settles. |
| F. Rail switched off | Switch Arkade off on Integrations → LNURL rails. The next invoice's checkout shows no Arkade chip, and lnurl-server sees no arkade request for it. |

## 6. Token rails — acceptance

**Automated:** the asset tab in Chromium against a stub LNURL, on a `ServerTester` host with the plugin loaded. It needs the
BTCPay test stack (postgres, nbxplorer, bitcoind) and runs alone:

```
export TESTS_POSTGRES="User ID=postgres;Include Error Detail=true;Host=127.0.0.1;Port=39372;Database=btcpayserver"
dotnet build BTCPayServer.Plugins.Tests -p:StaticWebAssetsEnabled=false
pwsh BTCPayServer.Plugins.Tests/bin/Debug/net10.0/playwright.ps1 install chromium
dotnet test BTCPayServer.Plugins.Tests --no-build -p:StaticWebAssetsEnabled=false --filter "FullyQualifiedName~TokenCheckoutBrowserTests.A_payer_sees_each_network"
WALLETCONNECT_PROJECT_ID=<Reown project id> dotnet test BTCPayServer.Plugins.Tests --no-build -p:StaticWebAssetsEnabled=false --filter "FullyQualifiedName~TokenCheckoutBrowserTests.Connect_wallet"
dotnet test BTCPayServer.Plugins.Tests --no-build -p:StaticWebAssetsEnabled=false --filter "FullyQualifiedName~TokenCheckoutBrowserTests.A_fixedfloat"
```

Point the `TESTS_*` variables at the stack's real ports (`TESTS_POSTGRES`, `TESTS_EXPLORER_POSTGRES`, `TESTS_BTCNBXPLORERURL`).
Each test uses a fresh database.

The FixedFloat test has the stub answer in lnurl-server's FixedFloat shape. Its options name a `provider`, each callback is
a new order with only a short `paymentQuote.expiresAt`, and `verify` reports the payer's deposit transaction as the
`paymentReference`. The test checks five things:
- the default assets provision USDT and USDC;
- opening the tab orders no quote, and a tap orders exactly one for the network tapped;
- the chips and amount read "via FixedFloat", next to the provider notice;
- an expired quote is re-quoted at a new deposit address;
- the invoice settles through `verifyBatch` with the deposit transaction linked.

It does not run against lnurl-server's simulated provider; that run still needs its regtest stack.

**Wallet module:** `cd Plugins/BTCPayServer.Plugins.LNURLVerify/walletconnect && npm ci && npm test`, then
`npm run build` to rebuild `Resources/lnurlverify/wallet.js` after changing `src/`.

**Manual, on testnets** (a wallet must approve). Use an LNURL that issues token destinations, with BTCPay set to it and a
project ID set. Confirm each once, and that the invoice settles with the network and transaction in the payments list:

| Network | By QR | By deeplink (phone) | By WalletConnect (desktop) |
|---|---|---|---|
| Arbitrum Sepolia | MetaMask scans the EIP-681 QR | "Open in wallet" opens MetaMask | Connect wallet, scan with MetaMask |
| Solana devnet | Phantom scans the Solana Pay QR | "Open in wallet" opens Phantom | Connect wallet, scan with a WalletConnect Solana wallet |
| Tron Nile | TronLink scans the recipient; enter the amount shown | none | Connect wallet, scan with TronLink |

On a provider's network (lnurl-server's FixedFloat rails, or its simulated provider), confirm the notice next to the QR. It
must say that the deposit address belongs to the provider and not the merchant, that the quote expires, and that a late,
short or excess deposit is resolved with the provider, not with the merchant or BTCPay.
