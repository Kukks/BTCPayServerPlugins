# LNURL Verify — verification runbook

The plugin is unit-tested (38 tests) and reviewed, but three things can only be confirmed by running
it. This is the concrete checklist to gain that confidence, ordered cheapest-first.

## 1. Unit tests (seconds, no infra)

```
dotnet test BTCPayServer.Plugins.LNURLVerify.Tests
```
Expected: 38 passed, 0 warnings. Covers capability decode, verify-support probe, receive guards +
settled-cache, the shared poller (incl. a 60-invoice concurrent settle/error stress), the full send
chain (parse → k1-refresh → bounds/balance → submit), connection-scoped reconciliation, and persistence
save/restore (against a fake settings store).

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
