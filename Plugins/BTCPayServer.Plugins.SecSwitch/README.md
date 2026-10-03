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

**SecSwitch refuses to hold fewer trusted keys than `QuorumThreshold` while it is
enabled.** A trust store smaller than the quorum can never verify anything: every
advisory fails quorum and is filed as "unverified", which is deliberately not notified,
so the settings page would look perfectly healthy while the instance had no protection
at all. Three places enforce this:

- The settings page refuses to **enable** SecSwitch — or to raise `QuorumThreshold`
  above the number of keys you hold — while the store is below the threshold, and warns
  on the page itself whenever it is (not only when it is empty).
- The manual **Remove** button refuses a removal that would drop an *enabled* store
  below the threshold. While SecSwitch is disabled you can still remove anything,
  including the last key, so a mistakenly-added key is never permanently stuck — you
  simply cannot turn SecSwitch back on until the store is at quorum again.
- A quorum-signed **key-rotation document** is refused if it would leave the store below
  the threshold (once rotation is wired up and reachable — see above).

Lower the quorum threshold first if you genuinely intend to run with fewer trusted
signers.

Keys are also screened when you add them: a key carrying a revocation, or one that has
already expired, is refused rather than silently admitted. Note this is a check on the
key material you paste — see "Important limitations" for what it does *not* cover.

## Actions

| Target | Fixed version | Condition | Action |
|---|---|---|---|
| Plugin | yes | prefer-update on | Download + queue update, then stop for restart |
| Plugin | no | — | Queue disable, then stop for restart |
| Core | yes | SSH **verified** working | Run `btcpay-update.sh` over SSH |
| Core | yes | SSH configured but **not yet verified** | Defer — re-evaluated on the next poll |
| Core | no, or SSH not configured at all | — | Stop the server |

"SSH verified working" means BTCPayServer's own connectivity check
(`CheckConfigurationHostedService`) has actually succeeded at least once since this
process started, not merely that SSH settings are present. That check runs in the
background and can still be in progress — or endlessly retrying — when SecSwitch's own
poll runs, which is exactly the "configured but not yet verified" row above:
**SecSwitch deliberately waits rather than guessing.** Guessing wrong in either
direction is bad — stopping a server whose SSH genuinely does work, or trying to run an
update over SSH that cannot actually connect — so a fixable core advisory reached in
this state is deferred instead, and re-evaluated on every later poll. If SSH is
misconfigured (a rotated key, a wrong host, container networking trouble) and never
starts working, **this deferral can persist indefinitely** — the server keeps running,
unpatched, against that specific core advisory, until the SSH connection issue is fixed
or the advisory is otherwise addressed. SecSwitch surfaces this state via an admin bell
notification and the alert banner from the very first poll it happens on (not buried in
the audit log alone) specifically so an indefinite deferral is never a silent one — see
"Needs attention" below.

Manual mode, a notify-only pin, or an advisory below the severity gate never takes any
of the actions above automatically — SecSwitch only records a notification and leaves
the decision to an admin in the audit log.

**"Needs attention" vs. "needs decision".** The audit log (and the alert banner, and
bell notifications) can show two different holds, and they mean different things. A
"needs decision" advisory has a computed action SecSwitch is holding open for you to
approve (manual mode, a notify-only pin, or the severity gate). A "needs attention"
advisory means SecSwitch did **not** apply an action, for one of:

- it cannot tell whether your installed version is affected;
- (the SSH case above) it cannot yet tell whether to update or stop the server;
- it resolved an action, tried it, and the attempt **failed** — for example the plugin
  identifier did not resolve to an installed plugin directory (which is what happens for
  a plugin bundled with BTCPay Server itself, since those have no directory of their
  own), the update could not be queued, or the SSH connection for a core update could
  not be made;
- the feed's index listed the advisory under a different id than the advisory itself is
  signed for, so SecSwitch refuses to act until the feed is consistent.

The audit log's Reason column always says which. "Needs attention" is deliberately **not
final**: the advisory is re-fetched and re-evaluated on every later poll, and it will act
normally once the underlying cause clears. Anything shown as **"Acted"** genuinely
succeeded — a failed action is never recorded as acted on.

A stuck "needs attention" advisory sends a bell notification when it first appears and
again whenever its reason changes, but not once per poll for as long as it persists.
It stays visible in the alert banner and the audit log the entire time.

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
- **A "needs decision" advisory cannot actually be decided from the UI, and turning
  automatic mode on later will not apply it.** When manual mode, a notify-only pin, or
  the severity gate holds an action open, SecSwitch records *what it would have done* in
  the audit log's Reason column and notifies you — but the only buttons the audit log
  offers are Suppress and Un-suppress. There is no "Apply now". Worse, "needs decision"
  is a **final** state, so the advisory's content hash is cached and the advisory is
  never re-downloaded: **switching `AutoApply` back on afterwards does not cause the
  held action to be applied.** Act on it yourself (update or disable the plugin from the
  Manage Plugins page, or update/stop the server), or use Un-suppress-style
  re-evaluation by suppressing and then un-suppressing the advisory, which clears the
  cached hash and makes SecSwitch fetch and re-evaluate it on the next poll.
- **A mis-published advisory cannot be withdrawn ecosystem-wide.** The advisory format
  has `supersedes` and `revoked` fields and SecSwitch parses both, but only `revoked` is
  ever read, and only for the advisory that carries it — a revoked advisory is treated as
  not applicable to you. Neither field affects any *other* advisory. Because a
  correction or a withdrawal is published as a **new** advisory under a **new id**, and
  SecSwitch's ledger is keyed by id, the original advisory's already-recorded outcome is
  untouched by it: if the original already caused a plugin to be disabled or the server
  to be stopped, publishing a superseding or revoking advisory will not undo that, and
  will not stop an instance that has not yet polled from acting on the original. Withdraw
  by removing the advisory from the feed index and, on affected instances, suppressing it
  from the audit log.
- **A "not applicable" verdict is never revisited when your local state changes, so
  installing a vulnerable plugin *after* its advisory was published leaves you
  unprotected — permanently and silently.** "Not applicable" is a final state: the
  advisory's content hash is cached and it is never fetched or re-evaluated again. If an
  advisory against plugin X is processed while X is not installed (or is installed at an
  unaffected version), and you later install X — or downgrade it into the affected range
  — SecSwitch will **not** notice, will not act, and will show nothing in the banner or
  the bell. There is no periodic re-check of past advisories against current state. If
  you install a plugin that has had advisories published against it, check the SecSwitch
  audit log yourself, and use Suppress-then-Un-suppress on the relevant advisory to force
  SecSwitch to fetch and re-evaluate it on the next poll.
