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
  LNURL **LUD-21 `verify`** endpoint. When the service also advertises **LUD-XX `verifyBatch`**, all
  pending invoices at that endpoint are checked with one request per poll cycle (up to 250 per request);
  services without it, or whose batch endpoint stops answering, are polled per invoice. One shared
  background poller serves every connection, and
  BTCPay's own status checks are answered from its last result rather than with more requests.
- **Payment options:** when the payRequest advertises LUD-XX **`paymentOptions`**, the plugin requests
  its `lightning` option explicitly and applies that option's own amount bounds. If the service reports
  Lightning as unavailable, invoice creation fails with that reason.
- **Send:** for an LNURL-withdraw, BTCPay pays an arbitrary invoice by submitting it to the withdraw
  callback (the linked wallet pays it), bounded by the withdraw's min/max and, when exposed, its
  balance.

## One Bitcoin checkout (LNURL payment options)

When the LNURL advertises a rail in LUD-XX `paymentOptions` that a BIP321 URI can carry, the plugin adds it to the store as a
payment method, automatically, and re-checks hourly. Today that is `arkade` (`LNURL-ARKADE`), and `onchain` (`LNURL-ONCHAIN`)
for stores without their own on-chain wallet: a store with an enabled wallet never requests the LNURL's on-chain option.
A store that already has the Arkade plugin's own `ARKADE` payment method gets no LNURL rails.

- **Checkout:** one "Bitcoin" tab replaces the Lightning and on-chain tabs. Its QR is one BIP321 URI carrying every active rail;
  a chip per rail switches the QR to that rail alone, and an "All" chip switches it back.
- **No cost at invoice creation:** a rail is requested from the LNURL when the checkout opens, or, with the store setting
  "Request every rail when the checkout opens" turned off (Integrations → LNURL rails), only when the payer taps it. Reopening
  the checkout does not request a rail the LNURL refused within the last hour; a payer's tap does.
- **Settlement:** a rail payment is recorded when the LNURL's `verify` reports it `settled` with a `paymentReference`, at the
  amount agreed with the LNURL. An underpayment never settles there, so BTCPay never sees it; recovering those funds is the
  LNURL service's job. An overpayment is recorded at the agreed amount. Late payments are recorded until the invoice stops
  being monitored.
- **Rails whose settlement cannot be detected:** an option the LNURL marks `verifiable: false` (proposed for LUD-XX in
  lnurl/luds#303) is never provisioned. A rail whose LNURL answers without a usable `verify` URL is left off new invoices for
  a day, so a checkout does not keep offering a payment method that cannot activate.
- **Turning a rail off:** switch it off on Integrations → LNURL rails.
- **Tokens on other networks:** see "Token rails" below.
- **Upgrading to 1.2.0:** stores whose LNURL advertises a rail switch to the single "Bitcoin" tab for invoices created after
  the upgrade. Nothing else changes for them.

## Token rails (EVM, Solana, Tron)

When the LNURL advertises a token on another network as a payment option, with a CAIP-19 `asset` and a `unit` from its
`units`, the payer can pay in that token. For example, USDT on Arbitrum, Solana or Tron. The invoice stays priced in BTC; the
LNURL quotes the token amount and settles it through `verify`, as for the Bitcoin rails.

- **Assets:** one checkout tab per configured unit code, as payment method `LNURL-<CODE>`. The codes come from the server
  setting `LNURLVERIFY_ASSETS` (environment `BTCPAY_LNURLVERIFY_ASSETS`), default `USDT,USDC`. A change takes effect after
  a restart, because BTCPay registers payment methods at startup. Integrations → LNURL rails lists any unit the LNURL offers
  that is not configured.
- **Networks:** one chip per advertised network. Any EVM chain works, as do Solana and Tron. Network names and explorer links
  come from a built-in list of chains; an unlisted chain shows its CAIP-2 id and no explorer link.
- **Checkout:** each network has a QR; EVM and Solana networks also have an "Open in wallet" link, which follows the store's
  "Pay in wallet" button setting (Checkout Appearance):
  - EIP-681 for EVM;
  - Solana Pay for Solana;
  - for Tron, the QR is the recipient, with the amount below it.

  With a WalletConnect project ID set in Server settings → LNURL Verify, "Connect wallet" pays from a wallet over WalletConnect.
  "Connect wallet" contacts Reown only once the payer presses it: its relay and API, plus some telemetry that carries the
  checkout URL (and with it the invoice id). AppKit's optional analytics are off.
- **Quotes:** a network is requested from the LNURL when the tab opens, or on a tap when "Request every rail when the
  checkout opens" is off. An expired quote hides its QR until the payer asks for a new one. A partial payment re-issues
  every quoted network for the remainder. A network the LNURL refuses outright stays hidden for the rest of that invoice;
  one whose request fails, that the LNURL marks unavailable, or that does not answer in time, stays offered and can be
  tried again.
- **Settlement:** recorded from `verify` at the BTC amount agreed for the destination. The payments list shows the network and
  links the transaction.

## Limitations

- **Payment detection across a restart** — tracked invoices are persisted to BTCPay settings and
  re-seeded on startup, so a graceful restart (e.g. an update) does not lose payment detection: the
  poller re-polls their verify URLs and catches anything that settled while BTCPay was down. Caveats: an
  *ungraceful* crash can lose up to ~10s of the most-recently-created invoices (the persist throttle),
  and the state is a single settings blob — fine for typical volumes; a per-row table would scale better
  (future work).
- **Clearnet only (no Tor).** Requests are not routed through BTCPay's onion proxy, so a `.onion` LNURL
  or Lightning address will not connect.
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
- **Do not uninstall the plugin, or downgrade it below 1.2.0, once invoices with LNURL rails exist.** BTCPay's checkout page
  needs the plugin's payment-method handler for every rail on an invoice, so without it the checkout page of every such
  invoice fails, whether it is open, paid or expired.
- **Do not remove a code from `LNURLVERIFY_ASSETS`, or uninstall the plugin, once invoices with that token's prompt exist.**
  As with rails, the checkout page of every such invoice, open, paid or expired, needs the payment method's handler.
- **Connect wallet on Solana sends classic SPL Token transfers,** so it does not pay Token-2022 mints.
- **A Solana destination must be a wallet (system account) address** — Connect wallet refuses a token account, or any other
  off-curve address, instead of paying it through a nested token account the LNURL would never watch. Its QR stays offered.
- **The merged QR's amount is the on-chain due** when on-chain is active, which can include a network-fee component; an Arkade
  payer scanning the merged QR may overpay by it. Each rail's own chip carries its exact amount.
- If re-issuing a rail destination after a partial payment fails, that rail drops out of the checkout; a payment to its
  previous destination is still recorded, at the amount agreed for it.

## Notes

Every BTCPay invoice that offers LNURL already exposes a LUD-21 `verify` URL (enabled by default in
the store's Lightning settings), so a BTCPay store on one server can receive into another via this
plugin with cryptographic settlement proof.
