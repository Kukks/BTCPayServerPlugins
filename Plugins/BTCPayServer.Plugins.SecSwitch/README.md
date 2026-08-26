# SecSwitch

Subscribes to a feed of GPG-quorum-signed security advisories and, per local policy,
updates or disables affected plugins — or updates/stops BTCPay Server itself.

SecSwitch is **disabled by default** (opt-in). While disabled it never fetches the feed
and never acts on anything, but it still records a startup heartbeat and bootstraps its
trust store (both described below), so it is ready to enable as soon as you turn it on.

## How it works

1. An hourly task fetches the advisory index from the configured feed (GitHub Pages).
2. Each new advisory must carry valid detached PGP signatures from at least
   `QuorumThreshold` (default 2) **distinct** trusted keys over the exact bytes of
   `advisory.json`. Anything less is logged and ignored.
3. Verified advisories that apply to this instance are resolved to an action by local
   policy. Advisories never dictate the action — only the facts.
4. Actions are recorded in a local ledger so nothing is ever acted on twice.

## Trusted keys

SecSwitch has a mechanism to bootstrap `QuorumThreshold` trusted keys from a trust-root
bundle embedded in the plugin: on first run it adds any bundled key that is not already
in your trust store, then remembers each key it has offered so it never re-adds one you
deliberately removed, even across restarts and upgrades. **The bundle shipped in this
build is currently empty** (`Resources/trust-root.json`'s `keys` array has nothing in
it), so out of the box SecSwitch has no trusted keys and cannot verify anything — an
admin must paste at least one key by hand (below) before SecSwitch can be enabled at
all. This is expected to change in a future release once founding signer keys are
published; when it does, existing instances will pick up any newly-bundled key
automatically on their next restart.

You can add or remove keys by hand from the settings page — each add re-derives the
key's fingerprint from the pasted key material itself, never from anything typed. The
plugin also has a separate, fully-implemented and tested mechanism for rotating keys via
a quorum-signed key-rotation document (co-signed by the existing trusted signers,
refusing to shrink the trust store below `QuorumThreshold` - see below) but **it is not
currently wired to anything**: nothing in this version of the plugin fetches, accepts, or
applies a rotation document from the feed or anywhere else, so today the only way to
change trusted keys on a running instance is the manual add/remove UI.

**A quorum-signed key-rotation document is refused if it would leave the trust store with
fewer keys than `QuorumThreshold`** (once rotation is wired up and reachable — see
above). The remaining keys could never reach quorum again for any future rotation, so the
whole rotation would be rejected outright — lower the quorum threshold first if you
genuinely intend to run with fewer trusted signers. The settings page's own manual
"Remove" button has no such guard today regardless: it only stops you from re-enabling
SecSwitch with zero trusted keys, not from leaving fewer keys than your quorum threshold
actually requires, so take care when removing keys by hand.

## Actions

| Target | Fixed version | Condition | Action |
|---|---|---|---|
| Plugin | yes | prefer-update on | Download + queue update, then stop for restart |
| Plugin | no | — | Queue disable, then stop for restart |
| Core | yes | SSH configured | Run `btcpay-update.sh` over SSH |
| Core | no or no SSH | — | Stop the server |

Manual mode, a notify-only pin, or an advisory below the severity gate never takes any
of the actions above automatically — SecSwitch only records a notification and leaves
the decision to an admin in the audit log.

**Disabling and updating a plugin only take effect on the next start, and BTCPay Server
does not restart itself.** Stopping the process is the only enforcement point SecSwitch
has; your deployment's restart policy (Docker, systemd, or another process supervisor)
is what actually brings the server back up in the new state. If nothing restarts it, the
server simply stays down until an operator notices.

## Important limitations

- **SecSwitch can be silently disabled by a failure that has nothing to do with it.**
  BTCPayServer treats every externally-installed plugin, SecSwitch included, as
  replaceable: a startup failure it cannot cleanly attribute to one specific component
  falls back to disabling every externally-installed plugin at once, not only the one
  actually at fault. If SecSwitch appears in the disabled-plugins list on the Manage
  Plugins page, protection is off until you re-enable it — regardless of why it got
  there. SecSwitch records a heartbeat each time it starts specifically so this kind of
  silence is at least detectable from the persisted state, though no page in the UI
  surfaces that timestamp directly today.
- **Automatic mode can restart or stop an unattended server.** That is the point, but it
  means a compromised signer quorum or an over-broad advisory version range has real
  blast radius. Pin business-critical plugins to notify-only if that matters to you.
- **A feed outage means *missed* advisories, never forged ones.** SecSwitch only ever
  acts on an advisory that meets GPG quorum; an attacker who can merely block, delay, or
  corrupt the feed cannot make SecSwitch act on anything by doing so — only delay a real
  advisory from reaching it.
