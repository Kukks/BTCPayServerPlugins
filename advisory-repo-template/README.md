# SecSwitch advisories

Signed security advisories consumed by the BTCPay Server SecSwitch plugin.

## Publishing an advisory

1. Create `advisories/<YYYY-MM-DD-slug>/advisory.json`.
   `affectedVersions` uses BTCPay Server's `VersionCondition` syntax — conjunctions need
   `&&` (`">=1.0.0 && <1.2.3"`). Space-separated ranges do not parse.
   `severity` must be exactly one of `low`, `medium`, `high`, `critical` (case-insensitive) —
   any other value fails to parse and the whole advisory is rejected.
2. Have each signer produce a detached, armored signature over the **exact bytes**:
   `gpg --detach-sign --armor --output signatures/<FINGERPRINT>.asc advisory.json`
3. Run `./build-index.sh` and commit the regenerated index files.
4. Open a PR. Merging to `main` publishes to GitHub Pages.

`advisory.json` must never be edited after publication — that invalidates every signature.
To correct an advisory, publish a new one and set `supersedes`, or set `revoked` on a
replacement. Both need their own quorum.

## Rotating keys

Create `keys/rotate-<date>/rotation.json` with `{"add": ["<armored pubkey>"], "remove": ["<fingerprint>"]}`,
signed by a quorum of the **currently trusted** keys. A key cannot authorise its own addition,
and a rotation that would empty the trust store is refused.

**Current limitation:** this format is forward-looking. The SecSwitch plugin implements and
tests rotation (`TrustStore.TryApplyRotation`), but nothing in the plugin's production code
calls it yet — no fetch path polls a `rotation.json` from the feed today. Publishing a
rotation document here has no effect on any running SecSwitch instance until that wiring
ships. Until then, distribute added or removed keys to operators out-of-band (for example, a
`trust-root.json` update shipped in a plugin release).

## Limits

The plugin's fetcher enforces these bounds; publishing within them keeps every advisory
reachable:

- `index.json` ≤ 1 MB, `advisory.json` ≤ 256 KB, `signatures/index.json` ≤ 16 KB, each
  `.asc` file ≤ 64 KB.
- At most 64 signature files are read per advisory — list only the ones you actually publish.
- At most 50 new advisories are fetched per poll; a larger backlog is simply picked up over
  subsequent polls, not dropped.
- The feed URL must be `https`. GitHub Pages already serves over https, so this is automatic
  unless you point a custom domain at the feed without TLS.
