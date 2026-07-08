# Electrum

Run your BTCPay Server's on-chain Bitcoin wallet through an **Electrum** server (ElectrumX or
Fulcrum) that works **alongside** NBXplorer — not instead of it. NBXplorer stays in charge
whenever it's healthy; Electrum steps in to get you running before NBXplorer has finished
syncing, and keeps you running if it ever falls behind or goes down.

> **Mainnet only (by default).** The built-in server list is public **mainnet** Electrum
> servers, so the plugin only activates on a mainnet BTCPay Server. See *Requirements*.

## Why use it

- **Start accepting payments sooner** — a fresh NBXplorer + Bitcoin node can take a long time to
  sync. Electrum lets your store receive and send while that catch-up happens.
- **Stay up if NBXplorer struggles** — if NBXplorer falls out of sync or goes offline, each
  wallet automatically falls back to Electrum and returns to NBXplorer once it recovers.
- **No extra bookkeeping for you** — every wallet is tracked in *both* backends and kept in sync,
  and the two never hand out the same address, so switching back and forth is safe.

## Requirements

- BTCPay Server **2.3.7+**, running on **mainnet**.
- A reachable Electrum server. The plugin ships a list of public servers (sourced from Sparrow
  Wallet), or you can point it at your own ElectrumX/Fulcrum.
- *(Advanced / testing only)* To run on regtest/testnet/signet, set the environment variable
  `BTCPAY_ELECTRUM_ALLOWNONMAINNET=true` and use your own Electrum server for that network.

## Setup

1. Install the **Electrum** plugin and restart BTCPay when prompted.
2. As a server admin, open **Server Settings → Electrum** (`/server/electrum`).
3. Choose a server:
   - **Random** — picks a different trusted public server on each connection (simplest), or
   - pick a specific server from the list, or
   - choose **Custom server…** and enter your own `host:port` (e.g. `electrum.example.com:50002`).
4. Leave **Use TLS/SSL** on for public servers (they listen with TLS, usually on port `50002`).
5. Click **Test Connection** to confirm it works, then **Save**.

That's it — your store's Bitcoin wallet now runs through the plugin. Create and use BTCPay
wallets exactly as normal.

## How coexistence works

You don't configure any of this — it's automatic and decided **per wallet**:

- **NBXplorer is authoritative** whenever it's fully synced and tracking a wallet; that wallet's
  reads and writes go to NBXplorer.
- **Electrum is the bootstrap + fallback.** A wallet uses Electrum while NBXplorer isn't ready
  for it (still syncing, not yet tracking it, or unreachable), then switches to NBXplorer once it
  is — and switches back if NBXplorer later falls behind.
- **Switchovers are damped**, so a brief blip won't flip a wallet back and forth.
- **No address reuse.** Whichever backend is active, the plugin remembers the highest address
  each has handed out and fast-forwards the other past it on a switch, so you never reissue an
  address the other side already gave out.

## What happens when NBXplorer is down

- **Receiving keeps working** — payments are detected via Electrum and your invoices settle.
- **Sending keeps working** — spends and other calls that would normally go to NBXplorer fall
  back to Electrum automatically, and fail over quickly even if NBXplorer is hung rather than
  cleanly offline.
- **Your dashboard stays "Ready"** as long as Electrum is connected.
- **When NBXplorer comes back**, the plugin asks it to rescan your wallets so anything that
  arrived during the outage is picked up, and wallets return to NBXplorer once it's synced again.
- **If both backends are down**, BTCPay correctly reports Bitcoin as unavailable.

## Checking status

The **Coexistence Status** section at the bottom of the Electrum settings page shows:

- the effective status (**Ready** / **Not Connected**), the connected Electrum server, its
  version, and the current tip height; and
- a per-wallet table — which backend each wallet is **currently using**, how many consecutive
  checks agree on that choice, and the highest reserved receive/change address index.

## Settings reference

| Setting | What it does |
|---|---|
| **Electrum Server** | `host:port` of your server, or `random` to rotate through the trusted list on each connection. |
| **Use TLS/SSL** | Connect over TLS. Leave on for public servers (usually port `50002`). |
| **Gap Limit** | How many unused addresses to keep watched per chain (default **20**). |

A server or TLS change takes effect on the **next reconnect** — no full restart needed.

## Turning it off

On the Electrum settings page, **Disable Electrum on Next Restart** reverts BTCPay to plain
NBXplorer at the next server restart. Your wallet data and settings are preserved.
