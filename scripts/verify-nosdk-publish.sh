#!/usr/bin/env bash
# scripts/verify-nosdk-publish.sh
#
# Post-publish guard for the no-SDK (EnableLogixSdk=false) artifact. Fails and
# names every violation when, in <publish-dir>:
#
#   1. any file whose name starts with "RockwellAutomation" exists in the
#      publish tree or in tools/ (same rule as the SDK publish guards: these
#      DLLs are non-redistributable and must never ship);
#   2. an sdk/ directory exists anywhere in the publish tree (the no-SDK
#      artifact carries no adapter and no vendor closure);
#   3. bootstrap/provision-sdk.ps1 exists (it provisions the SDK, which this
#      artifact does not have);
#   4. a required script is missing: bootstrap/{setup,cold-install-gate,
#      setup-mcp-client}.ps1 (bench/ is not part of the no-SDK artifact).
#
# Usage: verify-nosdk-publish.sh <publish-dir>

set -euo pipefail

PUBLISH_DIR="${1:?Usage: $0 <publish-dir>}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
FAIL=0

if [[ ! -d "$PUBLISH_DIR" ]]; then
    echo "ERROR: publish dir not found: $PUBLISH_DIR" >&2
    exit 1
fi

scan_dirs=("$PUBLISH_DIR")
[[ -d "$ROOT/tools" ]] && scan_dirs+=("$ROOT/tools")
hits=$(find "${scan_dirs[@]}" -iname 'RockwellAutomation*' 2>/dev/null || true)
if [[ -n "$hits" ]]; then
    echo "ERROR: RockwellAutomation binaries found:" >&2
    echo "$hits" | sed 's/^/  /' >&2
    FAIL=1
fi

sdk_dirs=$(find "$PUBLISH_DIR" -type d -iname sdk 2>/dev/null || true)
if [[ -n "$sdk_dirs" ]]; then
    echo "ERROR: sdk/ directory present in no-SDK publish output:" >&2
    echo "$sdk_dirs" | sed 's/^/  /' >&2
    FAIL=1
fi

if [[ -e "$PUBLISH_DIR/bootstrap/provision-sdk.ps1" ]]; then
    echo "ERROR: bootstrap/provision-sdk.ps1 must not ship in the no-SDK artifact." >&2
    FAIL=1
fi

for f in bootstrap/setup.ps1 bootstrap/cold-install-gate.ps1 bootstrap/setup-mcp-client.ps1; do
    if [[ ! -f "$PUBLISH_DIR/$f" ]]; then
        echo "  MISSING: $f" >&2
        FAIL=1
    fi
done

if [[ $FAIL -ne 0 ]]; then
    echo "FAIL: no-SDK publish guard ($PUBLISH_DIR)" >&2
    exit 1
fi
echo "No-SDK publish guard OK: $PUBLISH_DIR"
