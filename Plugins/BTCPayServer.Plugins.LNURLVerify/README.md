# LNURL Verify

Use any **LNURL** or **Lightning address** as a BTCPay Server Lightning backend — no node, no API key, no custody by BTCPay.

## Connection strings

The capability is decided by decoding the value you provide:

- **Receive only** — a Lightning address or an LNURL-pay:
  ```
  type=lnurl;value=you@example.com
  type=lnurl;value=LNURL1DP68...
  ```
- **Send & receive** — an LNURL-withdraw whose response carries a `payLink` (LUD-19):
  ```
  type=lnurl;value=LNURL1DP68...
  ```

An LNURL-withdraw **without** a `payLink` is rejected: it can send but cannot create checkout
invoices, so it is unusable as a store's Lightning backend.

## How it works

- **Receive:** BTCPay asks the LNURL-pay callback for an invoice and detects settlement via the
  LNURL **LUD-21 `verify`** endpoint. A single shared background poller watches every tracked invoice
  across every connection (grouped by verify-host, bounded concurrency, capped back-off), so it scales
  to many invoices and many addresses without a poll loop per connection.
- **Send:** for an LNURL-withdraw, BTCPay pays an arbitrary invoice by submitting it to the withdraw
  callback (the linked wallet pays it), bounded by the withdraw's min/max and, when exposed, its
  balance.

## Limitations

- **Send has no preimage** for the payer in general — the *payee* receives the preimage, not BTCPay.
  A preimage is surfaced only when the withdraw service returns one in its callback response
  (non-standard); it is validated (`SHA256(preimage) == payment hash`) before being reported.
- Repeated sends against one withdraw link are **serialized** — a fresh `k1` (and current balance) is
  fetched before each send, so reusable links support repeated payouts.
- **Uncertain sends can't auto-reconcile.** Sends are tracked, so `GetPayment`/`ListPayments` report
  Complete/Failed/Pending. But if the withdraw-callback request fails *after* submission, the outcome is
  genuinely unknowable (a bare-bolt11 send has no callback and no verify URL), so it is recorded as
  pending and **cannot** be auto-resolved — such a payout stays in-progress in BTCPay and may need
  manual review. (Reporting unknown rather than failed is deliberate — a blind retry could double-pay.)
- **Validating the connection creates one throwaway probe invoice** on the receiver — this is how
  LUD-21 verify support is checked (verify is only advertised in the callback response, not metadata).
- Amountless / top-up invoices are not supported (LNURL-pay is amount-driven).
- Node, channel and on-chain operations are not available — this client holds no Lightning node.

## Notes

Every BTCPay invoice that offers LNURL already exposes a LUD-21 `verify` URL (enabled by default in
the store's Lightning settings), so a BTCPay store on one server can receive into another via this
plugin with cryptographic settlement proof.
