# Blink lightning support plugin

Allows to use a [Blink Wallet](https://blink.sv) account as the lightning provider for BTCPay Server.

Find detailed documentation on how to use this plugin at [dev.blink.sv/examples/btcpayserver-plugin](https://dev.blink.sv/examples/btcpayserver-plugin).

## Account types

The plugin supports two kinds of Blink accounts, selected by the connection string.

### Custodial account (send + receive)

Uses a Blink API key. Full functionality (create invoices, pay invoices, balance).

```
type=blink;server=https://api.blink.sv/graphql;api-key=blink_...;wallet-id=xyz;currency=BTC;
```

- `server` is optional (defaults to the public Blink instance).
- `wallet-id` is optional (defaults to the account's default wallet).
- `currency` is optional (`BTC` or `USD`).

Create an API key on the [Blink dashboard](https://dashboard.blink.sv). For BTCPay receiving you
need at least the `READ` and `RECEIVE` scopes; `WRITE` is additionally required to pay invoices.

### Non-custodial (Spark) account — receive only

The new Blink non-custodial accounts do not expose an API key or a GraphQL wallet id. Receiving is
brokered through the public LNURL-pay endpoint of your Blink lightning address, and settlement is
detected via the LNURL LUD-21 `verify` mechanism. No credentials are required.

```
type=blink;ln-address=yourname@blink.sv;
```

- A bare username is accepted and defaults to the `blink.sv` domain (`ln-address=yourname`).
- `username=` is accepted as an alias for `ln-address=` (e.g. `type=blink;username=yourname@blink.sv;`).
- Only **receiving** is supported. Sending, balance and channel operations are not available because
  they require the wallet seed, which BTCPay never holds.
- Only **mainnet** (`blink.sv`) is supported for non-custodial accounts.
- **USDB (non-custodial Dollar balance):** add `currency=USD`
  (`type=blink;ln-address=yourname@blink.sv;currency=USD;`). This is passed through so it works
  automatically once Blink enables non-custodial USD receiving; until then validation will report it
  as unavailable.

Note: because there is no websocket for non-custodial accounts, payment detection uses polling of the
LUD-21 verify URL. Settlement typically appears in BTCPay within a few seconds of payment.

## Top-up / amountless invoices

Top-up invoices (created with the amount left empty) are supported for both account types, but they
work differently:

- **Custodial (API key):** the plugin creates a true amountless BOLT11 invoice, so the checkout page
  shows a normal Lightning invoice QR and the payer chooses the amount in their wallet.
- **Lightning address (`ln-address=`):** LNURL-pay is inherently amount-driven, so an amountless
  BOLT11 cannot be minted. Instead BTCPay falls back to the **LNURL** payment option, where the
  payer's wallet supplies the amount and the plugin fetches a matching invoice from Blink. (This
  applies to any `ln-address=` connection, including one pointing at a custodial Blink account.) For
  this to work:

  > **Custodial vs non-custodial lightning addresses.** The plugin auto-detects (via Blink's public
  > API) whether an `ln-address=` points at a custodial Blink account or a non-custodial (Spark) one:
  > - **Custodial**: fixed-amount invoices are minted directly through Blink's public GraphQL API,
  >   committing to BTCPay's own description, so the payer's wallet shows the store description, and
  >   settlement is detected even when a Blink-to-Blink payment settles internally (intraledger). This
  >   avoids an issue where the Blink mobile app would pay a custodial lightning address intraledger
  >   and bypass the BTCPay invoice, leaving the payment unregistered.
  > - **Non-custodial (Spark)**: invoices are proxied from Blink's LNURL server; BTCPay mirrors Blink's
  >   metadata so strict wallets accept the invoice (the payer's wallet then shows the Blink identity).

  - the store's **LNURL** payment method must be enabled (it is by default when Lightning is set up);
  - the payer's wallet shows the Blink identity line (e.g. *"Pay to yourname@blink.sv"*) instead of
    the store description — this is unavoidable because the invoice's description hash is committed by
    Blink's LNURL server;
  - amounts must be whole satoshis and within Blink's per-address limits.

  On a top-up invoice you will see a red event line in the invoice log such as *"BTC-LN: Payment
  method unavailable (Blink lightning-address connections cannot create an amountless (top-up) bolt11
  invoice...)"*. **This is expected and harmless** — it is just BTCPay recording that it fell back
  from the plain BOLT11 method to LNURL. The invoice can still be paid via the LNURL QR.

### Wallet compatibility for amountless invoices

An amountless invoice has no amount encoded, so the paying wallet must let the user enter one. Most
wallets (e.g. Phoenix) prompt for the amount and pay successfully. Some wallets do **not** support
paying amountless BOLT11 invoices and will fail regardless of which lightning backend created the
invoice — for example, in our testing the **Alby browser extension** could not pay them (see the
long-standing amountless-invoice limitation tracked in
[getAlby/lightning-browser-extension#823](https://github.com/getAlby/lightning-browser-extension/issues/823)).
This is a wallet-side limitation, not a plugin issue. Use an amount-capable wallet, or a fixed-amount
invoice, in that case.

## Migration: custodial → non-custodial

If your Blink account is migrated from custodial to non-custodial, the existing BTCPay integration
that relies on an API key (`api-key=...`) will stop working once the custodial wallet is retired,
because non-custodial accounts have no API key or GraphQL wallet id.

To keep receiving payments after the migration, update the store's Lightning connection string to the
non-custodial (receive-only) form:

```
type=blink;ln-address=yourname@blink.sv;
```

- **No new API key is required** for receiving — the lightning address of your non-custodial account
  is sufficient.
- If you also need to **send** from BTCPay, a non-custodial account cannot do so through the plugin
  (sending requires the wallet seed). Keep a custodial account/API key for send-side use cases, or use
  a different Lightning backend for sending.
