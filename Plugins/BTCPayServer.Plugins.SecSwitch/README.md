# SecSwitch

SecSwitch checks an advisory feed every hour and sends matching advisories through BTCPay Server's notification bell to server admins. It evaluates the running BTCPay Server version and the versions of loaded plugins on every poll.

## Admin settings

| Setting | Default | Behavior |
| --- | --- | --- |
| Check advisories and notify server admins | On | Poll the configured feed and report matching advisories and actions. |
| Apply the advisory kill switch | On | Disable an affected plugin or put an affected BTCPay Server in recovery-only mode. |
| Automatically update BTCPay Server | Off | Allow updates through the deployment's `btcpay-host update` command when available. |
| Automatically update a plugin | Off for each plugin | Allow updates of that plugin through BTCPay's Plugin Builder. |

A component's opt-in covers advisory fixes and routine releases. A failed plugin fix falls back to disabling the plugin while the kill switch is on. With the kill switch off, admins still get notifications and the updates they opted into can still run. Routine updates pause if any feed entry is skipped, even when the valid advisories are still evaluated. See the skipped-entry warnings in the server log. Routine updates also skip components with an applicable advisory. An advisory fix uses the advisory's newer fixed version when it clears the known affected ranges. BTCPay Server's host command follows the deployment's update channel. SecSwitch checks the running version again after the update. Routine core updates use the latest version found by BTCPay Server's own release check.

BTCPay's notification preferences apply. SecSwitch records delivery work in the audit ledger and retries when sending fails. Suppress a specific advisory in the audit log to exempt the instance from its actions.

## Advisory publication

The proposed public `btcpayserver/advisories` repository would publish GitHub Pages on every push to its protected default branch. A single reviewed JSON source would produce the public index and advisory pages, an RSS feed at `rss.xml`, and the exact JSON file at `advisories.json`. The proposed machine URL is `https://btcpayserver.github.io/advisories/advisories.json`.

The official feed is pending publication. Set a trusted, direct HTTPS URL under SecSwitch settings to start polling. The settings page shows the last poll result. Enable the official default after the published endpoint serves valid JSON directly. SecSwitch refuses redirects, caps the response at 1 MiB and allows 30 seconds for a fetch.

The feed is the authority. Protect the advisory repository and its Pages deployment with required reviews. The feed identifies affected components and versions. Local settings determine the action.

## Feed format

```json
{
  "advisories": [
    {
      "id": "example-core",
      "identifier": "BTCPayServer",
      "affectedVersions": ">=2.4.0 && <2.4.7",
      "fixedVersion": "2.4.7",
      "severity": "high",
      "title": "Example core advisory"
    }
  ],
  "conditionalAdvisories": [
    {
      "id": "example-plugin-pair",
      "identifier": "BTCPayServer.Plugins.Example",
      "affectedVersions": ">=1.0.0 && <1.2.3",
      "dependencies": [
        {
          "identifier": "BTCPayServer.Plugins.ExampleDependency",
          "condition": ">=3.0.0 && <3.1.0"
        }
      ],
      "fixedVersion": "1.2.3",
      "severity": "high",
      "title": "Example plugin interaction advisory"
    }
  ]
}
```

- Use `advisories` for BTCPay Server itself and for a plugin affected independently. Use the plugin's manifest identifier or `BTCPayServer` for core.
- Use `conditionalAdvisories` for a plugin affected only in certain installations. Copy a plugin manifest's `dependencies` array, with `identifier` and `condition` on each entry. SecSwitch accepts the casing emitted by PluginPacker. The target range and every dependency condition must match. A blank or omitted dependency condition means the dependency must be loaded at any version, and `!` means it must be absent. Use `BTCPayServer` to match the running core version as a dependency. The advisory's top-level identifier is the action target.
- SecSwitch still accepts the earlier `requires` object when reading preview feeds. Use `dependencies` in new advisories.
- Use globally unique, stable IDs and BTCPay's `VersionCondition` syntax. Missing version components compare as zero, so `1.2.3` equals `1.2.3.0`. Severity is informational.
- On a valid poll, removing an advisory withdraws it. A feed error preserves active lockdowns. A malformed entry keeps its known ID listed. An unidentified malformed entry prevents lifting a lockdown solely because its advisory disappeared.

## Applying actions

Plugin changes apply on restart. SecSwitch asks the supervisor to restart BTCPay Server after queuing changes. A dispatched core update takes precedence over that restart during the same poll. Each core update dispatch has a 24-hour cooldown.

During core lockdown, signed-in server admins can review advisories, suppress one after acknowledging the warning, or request an update where the deployment supports the host update command. Ordinary routes return HTTP 503, including for server admins. SecSwitch closes ordinary web connections already in progress when lockdown starts.

SecSwitch keeps polling advisories and holds routine updates, plugin changes and pending plugin restarts during core lockdown. It records and activates the lock before dispatching an opted-in advisory fix for BTCPay Server. The lock survives a restart. It lifts when a successful poll shows the running version is outside the affected range, the publisher withdraws the advisory, or an admin suppresses the last advisory holding the lock. A withdrawn plugin advisory leaves re-enabling the plugin to an admin.

BTCPay's built-in background services keep their existing lifecycle. This plugin enforces the web recovery boundary and its own actions. A deployment that needs to stop core or third-party background work can enforce that independently at the host.

A bundled plugin without an on-disk package requires manual action. BTCPay Server may also disable externally installed plugins after an unrelated startup failure. A disabled SecSwitch stops checking the feed.
