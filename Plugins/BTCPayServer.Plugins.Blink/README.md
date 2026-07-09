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
