#!/usr/bin/env bash
# Regenerates index.json and each advisory's signatures/index.json.
set -euo pipefail

cd "$(dirname "$0")"

entries=()
for dir in advisories/*/; do
    id="$(basename "$dir")"
    [ -f "$dir/advisory.json" ] || continue

    hash="$(sha256sum "$dir/advisory.json" | cut -d' ' -f1)"
    entries+=("$(jq -n --arg id "$id" --arg path "advisories/$id" --arg hash "$hash" \
        '{id: $id, path: $path, contentHash: $hash}')")

    mkdir -p "$dir/signatures"
    find "$dir/signatures" -maxdepth 1 -name '*.asc' -printf '%f\n' \
        | sort | jq -R . | jq -s . > "$dir/signatures/index.json"
done

printf '%s\n' "${entries[@]}" | jq -s . > index.json
echo "Wrote index.json with ${#entries[@]} advisories."
