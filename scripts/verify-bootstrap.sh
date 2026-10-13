#!/usr/bin/env bash
# scripts/verify-bootstrap.sh
#
# Bootstrap-and-bench completeness guard.
#
# The Makefile copies bootstrap/*.ps1 (and bootstrap/README* if present) into
# <publish-dir>/bootstrap/, and bench/*.ps1 into <publish-dir>/bench/,
# immediately after `dotnet publish` runs, for both publish-win and
# publish-linux (the same used for both directories). This
# script is the outcome assertion for those copy steps: it asserts, by name,
# that every PowerShell script a cold-install operator (bootstrap/) or the
# the session-1 script runner (bench/studio-edits.ps1, see
# Session1ScriptRunner / an earlier change) needs is actually present in the
# published tree -- not merely that *a* bootstrap/ or bench/ directory
# exists.
#
# A publish artifact that is missing setup.ps1, cold-install-gate.ps1, or
# provision-sdk.ps1 leaves the operator with a working executable and no way
# to provision or gate a cold install. A publish artifact missing
# bench/studio-edits.ps1 leaves Session1ScriptRunner unable to find the
# script beside the binary at all, silently falling through to (and, in a
# standalone publish output with no accompanying source tree, failing) its
# repo-root dev fallback. Both failure modes are silent until a real user
# hits them, so this guard fails the build immediately and names every
# absent script (never a count-only "directory is non-empty" check
# for why that class of guard is insufficient).
#
# An entirely absent bootstrap/ or bench/ directory is treated the same as a
# directory present but missing every required file within it: every
# required script in that directory is reported as MISSING (rather than a
# separate "directory not found" short-circuit), so a single run always
# names every absentee across both directories.
#
# This is a sibling guard to scripts/verify-vendor-closure.sh: the win-x64
# SDK build invokes it from within verify-vendor-closure.sh (which already
# knows the publish-dir), and the linux-x64 build -- which has no vendor
# closure to check -- invokes it directly from the Makefile.
#
# Usage:
#   verify-bootstrap.sh <publish-dir>
#
#   publish-dir   Path to the published output directory, e.g.
#                 src/LogixMcp/bin/Release/net10.0/win-x64/publish

set -euo pipefail

PUBLISH_DIR="${1:?Usage: $0 <publish-dir>}"
BOOTSTRAP_DIR="$PUBLISH_DIR/bootstrap"
BENCH_DIR="$PUBLISH_DIR/bench"

REQUIRED_SCRIPTS=(
    "setup.ps1"
    "cold-install-gate.ps1"
    "provision-sdk.ps1"
    "setup-mcp-client.ps1"
)

REQUIRED_BENCH_SCRIPTS=(
    "studio-edits.ps1"
)

MISSING=0
for script in "${REQUIRED_SCRIPTS[@]}"; do
    if [[ ! -f "$BOOTSTRAP_DIR/$script" ]]; then
        echo "  MISSING: bootstrap/$script" >&2
        MISSING=$((MISSING + 1))
    fi
done

for script in "${REQUIRED_BENCH_SCRIPTS[@]}"; do
    if [[ ! -f "$BENCH_DIR/$script" ]]; then
        echo "  MISSING: bench/$script" >&2
        MISSING=$((MISSING + 1))
    fi
done

if [[ $MISSING -gt 0 ]]; then
    echo "" >&2
    if [[ $MISSING -eq 1 ]]; then
        echo "ERROR: 1 required bootstrap/bench script is absent from $PUBLISH_DIR." >&2
    else
        echo "ERROR: $MISSING required bootstrap/bench scripts are absent from $PUBLISH_DIR." >&2
    fi
    echo "       A cold-install operator needs all of: ${REQUIRED_SCRIPTS[*]} (in bootstrap/)." >&2
    echo "       The session-1 script runner needs: ${REQUIRED_BENCH_SCRIPTS[*]} (in bench/)." >&2
    exit 1
fi

echo "Bootstrap guard OK: all required scripts present in $BOOTSTRAP_DIR and $BENCH_DIR"
