# SecSwitch

An opt-in kill switch and auto-updater for BTCPay Server. SecSwitch polls an advisory feed hosted in a
repository under the `btcpayserver` GitHub organisation. When a version installed on this server is listed,
it disables or updates the affected plugin, or updates BTCPay Server, or locks the server down so that only
server admins can reach it.

SecSwitch is **disabled by default**. While disabled it never fetches the feed and never acts.

## Trust model

The feed is treated as the authority. There are no signatures yet: SecSwitch fetches the feed over HTTPS,
refuses redirects, and acts on what it says. Whoever can change the feed repository can make every opted-in
server disable plugins, update itself or go offline, so protect that repository (branch protection, required
reviews). Advisories state facts (target, affected versions, fixed version); the local settings alone decide
the action.

## Feed format

The default feed is `advisories.json` in the `btcpayserver/advisories` repository
(`https://raw.githubusercontent.com/btcpayserver/advisories/master/advisories.json`). It can be changed on the
settings page to any absolute `https://` URL.

```json
{
  "advisories": [
    {
      "id": "2026-11-04-example-plugin",
      "identifier": "BTCPayServer.Plugins.Example",
      "affectedVersions": ">=1.0.0 && <1.2.3",
      "fixedVersion": "1.2.3",
      "severity": "high",
      "title": "Short description shown in the audit log"
    }
  ]
}
```

- `id` must be unique; it keys the audit log.
- `identifier` is a plugin identifier, or `BTCPayServer` for the core.
- `affectedVersions` uses BTCPay's `VersionCondition` syntax. Combine bounds with `&&` (`">=1.0.0 && <1.2.3"`);
  space-separated ranges and blank values, including blank `||` clauses, are rejected. Versions are compared with
  missing parts as `0`, so `1.2.3` and the published `1.2.3.0` are the same version.
- `fixedVersion` is optional. It is only used when it is newer than the installed version and not itself inside
  `affectedVersions`; otherwise it is ignored (the plugin is disabled, or the core stays locked down), so a typo
  cannot downgrade a plugin or reinstall a vulnerable version. `severity` is `low`, `medium`, `high` or `critical`
  and is informational.
- To withdraw an advisory, delete it from the file.
- Malformed entries are skipped and logged without affecting the others, and an entry that cannot be read still
  counts as listed: a typo never lifts a lockdown, only deleting the entry does. The file is limited to 1 MB and a
  fetch gives up after 30 seconds.

## What it does

Every hour SecSwitch evaluates every advisory against this server.

| Target | Fixed version | Condition | Action |
|---|---|---|---|
| Plugin | yes | update preferred (default) | Download and queue the update; if that fails, disable instead |
| Plugin | no | | Queue a disable |
| BTCPay Server | yes | host integration offers `update` | Run `btcpay-host update`, and lock down until fixed |
| BTCPay Server | no, or no host integration | | Lock down |

Queued plugin changes apply on the next start. BTCPay Server does not restart itself, so after queueing,
SecSwitch stops the process once per poll and relies on your supervisor (Docker, systemd) to start it again. When a
core update was dispatched in the same poll it does not stop the process: the update restarts BTCPay Server and
applies the queued changes. `btcpay-host update` is not awaited to completion, so while a core advisory still applies
it is dispatched again once every 24 hours.

**Lockdown** keeps BTCPay Server running but answers `503` to everyone except server admins signed in through
the web UI. The sign-in flow (the login pages and the LNURL-auth login endpoints) and the static files that exist under
`wwwroot` stay reachable so an admin can sign in. Greenfield API keys, checkout, storefronts and webhooks are all
refused. Background work (payment detection, payouts) is not
affected. The lockdown is persisted, so it survives restarts, and it lifts by itself when the advisory stops
applying (the core was updated) or is removed from the feed. An admin can lift it earlier by suppressing the
advisory in the audit log, or by disabling SecSwitch.

## Settings

- **Apply actions automatically.** When off, SecSwitch only records and notifies; nothing is changed.
- **Prefer updating over disabling.** Use the advisory's `fixedVersion` when it names one.
- **Also update to any newer release.** Updates every installed plugin, and BTCPay Server through the host integration, to newer
  releases whether or not an advisory exists. Each plugin version is attempted once, and a core update at most once every 24 hours.
  Plugins with a change already queued are skipped. Requires automatic actions.
  BTCPay Server is only updated when the server's "check for new versions" policy is on, because SecSwitch reads the
  release that core's own daily check found.

The settings page shows whether the last poll of the feed succeeded; a feed that cannot be fetched means this
server is not being checked. The audit log shows every advisory that applied, what was done, and lets an admin
suppress one. Admins get a bell
notification for each action and a banner while a lockdown or an undecided advisory is open.

## Limitations

- **SecSwitch can be disabled by a failure unrelated to it.** BTCPay Server falls back to disabling all
  externally installed plugins when a startup failure cannot be pinned on one of them. If SecSwitch appears in the
  disabled plugins list, protection is off.
- Removing an advisory lifts its lockdown but does not re-enable a plugin it disabled or undo an update.
- A plugin bundled with BTCPay Server has no plugin directory, so it cannot be disabled or updated; the audit log
  shows it as needing attention.
- Core updates need the deployment's `btcpay-host` integration (core 2.4.4 and later) to offer the `update` command; without it a core advisory can only lock the server down.
- Automatic mode can restart or take offline an unattended payment server. That is the point, but a mistaken
  `affectedVersions` range has the same blast radius.
