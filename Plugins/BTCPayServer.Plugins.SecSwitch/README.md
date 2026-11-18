# SecSwitch

SecSwitch checks an advisory feed every hour and sends matching advisories through BTCPay Server's notification bell to server admins. It evaluates the running BTCPay Server version and the versions of loaded plugins on every poll.

## Admin settings

| Setting | Default | Behavior |
| --- | --- | --- |
| Check advisories and notify server admins | On | Poll the configured feed and report matching advisories and actions. |
| Apply the advisory kill switch | On | Disable an affected plugin or restrict an affected BTCPay Server to signed-in server admins. |
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
      "requires": {
        "identifier": "BTCPayServer.Plugins.ExampleDependency",
        "affectedVersions": ">=3.0.0 && <3.1.0"
      },
      "fixedVersion": "1.2.3",
      "severity": "high",
      "title": "Example plugin interaction advisory"
    }
  ]
}
```

- Use `advisories` for BTCPay Server itself and for a plugin affected independently. Use the plugin's manifest identifier or `BTCPayServer` for core.
- Use `conditionalAdvisories` when one plugin is unsafe only alongside a specific version of another loaded plugin. Both ranges must match. The first plugin is the action target. Older readers of the direct array ignore conditional entries.
- Use globally unique, stable IDs and BTCPay's `VersionCondition` syntax. Missing version components compare as zero, so `1.2.3` equals `1.2.3.0`. Severity is informational.
- On a valid poll, removing an advisory withdraws it. A feed error preserves active lockdowns. A malformed entry keeps its known ID listed. An unidentified malformed entry prevents lifting a lockdown solely because its advisory disappeared.

## Applying actions

Plugin changes apply on restart. SecSwitch asks the supervisor to restart BTCPay Server after queuing changes. A dispatched core update takes precedence over that restart during the same poll. Each core update dispatch has a 24-hour cooldown.

During core lockdown, signing in as a server admin remains possible through the standard web UI. Checkout, storefronts, webhooks, and API-key requests receive HTTP 503. Background services continue. Lockdown lifts when SecSwitch observes a running version outside the affected range, the publisher withdraws the advisory, or an admin turns off the kill switch or suppresses the advisory. A withdrawn plugin advisory leaves re-enabling the plugin to an admin.

A bundled plugin without an on-disk package requires manual action. BTCPay Server may also disable externally installed plugins after an unrelated startup failure. A disabled SecSwitch stops checking the feed.
