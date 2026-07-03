# Conference

Run many merchant stalls for a conference or event from a single place. The Conference app
turns a roster of merchants into fully working BTCPay setups and rolls all of their sales up
into one report.

## What it does

For each merchant (a name + email) the app provisions, in one step:

- a **user** account (or links an existing one),
- a **store** seeded with your default currency, spread and Lightning connection,
- a **Point of Sale** app,

and adds the merchant to their own store as an *Employee*. You then hand each merchant a
one-tap **login code** or their **POS QR**, and watch every stall's takings under **Reports**.

## Setup

1. Add the **Conference** app to your store (store nav → *Conference*).
2. **Settings tab** — set the defaults every merchant store inherits:
   - *Default Lightning Connection String* (use `Internal Node` for the server's node),
   - *Default Currency*,
   - *Default Spread %*.
3. **Merchants tab** — add merchants:
   - inline with **+ Add Row** (email + store name required; currency / spread / password optional), then **Add**, or
   - in bulk with **Import CSV** (download the **Template** first).
4. **Provision** each merchant, or **Provision All**, to create their user, store and POS.
5. Hand out access:
   - **Login** — a 60-second login-code QR that signs the merchant in and lands them on their POS,
   - **POS** — a link or QR straight to their Point of Sale.

## Per-merchant overrides

Currency, spread and Lightning connection can be set per merchant; leave a field blank to
inherit the conference default. **Re-apply Settings to All Stores** (Settings tab) pushes the
current defaults out to every provisioned store — tick a "force" box to overwrite per-merchant
values too.

## Health & repair

The Merchants tab shows each merchant's status. If a user, store or POS is deleted out from
under the app, the row turns red and a **Repair** action recreates only the missing pieces.

## Reports

Once merchants are provisioned, the **Reports** tab aggregates invoices, **Sales** (totalled in
each store's currency) and **Received** amounts (per payment currency) across all merchant
stores, for *Today* / *Yesterday* / *Last 7 days*.

## Security notes

- **Login codes are only issued for accounts this app created.** Adding a merchant whose email
  matches a pre-existing user links that user but never generates a login code for them — this
  prevents targeting an existing account (e.g. a server admin) through its email address.
- **Removing a merchant archives** their store and POS rather than deleting them, and **deleting
  the Conference app leaves every merchant store intact.**
