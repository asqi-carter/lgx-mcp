#!/usr/bin/env bash
# scripts/verify-vendor-closure.sh
#
# Three publish-output assertions run in sequence:
#
#   1. Bootstrap-completeness guard — delegates to the sibling script
#      scripts/verify-bootstrap.sh, which asserts that setup.ps1,
#      cold-install-gate.ps1, and provision-sdk.ps1 are all present by name
#      in <publish-dir>/bootstrap/. The Makefile copies bootstrap/*.ps1 into
#      the publish tree immediately after `dotnet publish`, before this
#      script runs; a publish artifact missing any one of these scripts
#      leaves a cold-install operator with no way to provision or gate the
#      install, so this guard fails the build and names every absentee.
#
#   2. RockwellAutomation absence guard — recursively scans the entire
#      publish output tree (including sdk/vendor/) AND tools/ (e.g.
#      tools/echoctl) for any file whose name starts with "RockwellAutomation"
#      and fails the build if one is found. RockwellAutomation DLLs are
#      non-redistributable; they must come from the user's licensed Studio
#      5000 / FactoryTalk Logix Echo installation via LOGIX_SDK_PATH or the
#      Echo API install-path HintPath, never from the shipped artifact or the
#      repo itself.
#
#   3. Vendor-closure completeness guard — reads the authoritative list of
#      required assemblies from the SdkVendorHarvest project's .deps.json
#      (produced during the publish step), then confirms that every
#      non-RockwellAutomation DLL in the transitive runtime closure is present
#      by name in sdk/vendor/ of the publish output.
#
#      A count-only check (e.g. "directory is non-empty") is insufficient: a
#      partial closure of N-of-M assemblies still passes such a guard.  This
#      script enumerates actual assembly names from the transitive-closure
#      manifest and fails the build on the first missing file, naming the
#      absentee.
#
# Usage:
#   verify-vendor-closure.sh <publish-dir> <vendor-deps-json>
#
#   publish-dir        Path to the published output directory.
#                      e.g. src/LogixMcp/bin/Release/net10.0/win-x64/publish
#   vendor-deps-json   Path to LogixMcp.SdkVendorHarvest.deps.json produced by
#                      the SdkVendorHarvest publish step.
#                      e.g. src/LogixMcp.SdkVendorHarvest/bin/Release/net10.0/
#                               vendor-publish/LogixMcp.SdkVendorHarvest.deps.json

set -euo pipefail

PUBLISH_DIR="${1:?Usage: $0 <publish-dir> <vendor-deps-json>}"
DEPS_JSON="${2:?Usage: $0 <publish-dir> <vendor-deps-json>}"
VENDOR_DIR="$PUBLISH_DIR/sdk/vendor"

if [[ ! -f "$DEPS_JSON" ]]; then
    echo "ERROR: Vendor deps.json not found at: $DEPS_JSON" >&2
    echo "       The SdkVendorHarvest publish step may not have run." >&2
    exit 1
fi

if [[ ! -d "$VENDOR_DIR" ]]; then
    echo "ERROR: sdk/vendor/ directory not found at: $VENDOR_DIR" >&2
    echo "       The IncludeSdkAdapterInPublish MSBuild target may not have run." >&2
    exit 1
fi

# ── Guard 1: bootstrap-completeness (delegates to sibling script) ───────────
#
# Asserts setup.ps1, cold-install-gate.ps1, and provision-sdk.ps1 are all
# present by name in "$PUBLISH_DIR/bootstrap/". See scripts/verify-bootstrap.sh
# for the full rationale; it is invoked here (rather than duplicated) so the
# win-x64 SDK build and the linux-x64 build share one source of truth for
# which scripts are required.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
bash "$SCRIPT_DIR/verify-bootstrap.sh" "$PUBLISH_DIR"

# ── Guard 2: assert zero RockwellAutomation* binaries anywhere in publish ────
#
# Recursively scan the entire publish tree (including sdk/vendor/) for any file
# whose name starts with "RockwellAutomation".  The IncludeSdkAdapterInPublish
# MSBuild target already excludes RockwellAutomation*.dll from the vendor
# harvest; this guard provides a defence-in-depth assertion that no such file
# escaped the filter and landed anywhere in the publish artifact.
#
# The same scan also covers tools/ (e.g. tools/echoctl, which references the
# Echo API DLLs by install-path HintPath only): no Rockwell binary may ever be
# committed there either.
SCAN_DIRS=("$PUBLISH_DIR")
if [[ -d "tools" ]]; then
    SCAN_DIRS+=("tools")
fi
ROCKWELL_FILES=$(find "${SCAN_DIRS[@]}" -name "RockwellAutomation*" 2>/dev/null || true)

if [[ -n "$ROCKWELL_FILES" ]]; then
    echo "ERROR: RockwellAutomation binaries found in publish output or tools/." >&2
    echo "       These non-redistributable DLLs must never appear in the artifact tree." >&2
    echo "       Found:" >&2
    while IFS= read -r f; do
        echo "         $f" >&2
    done <<< "$ROCKWELL_FILES"
    echo "" >&2
    echo "       Check the IncludeSdkAdapterInPublish Exclude glob in LogixMcp.csproj," >&2
    echo "       and confirm no Rockwell DLL was committed under tools/." >&2
    exit 1
fi

echo "RockwellAutomation guard OK: no RockwellAutomation* files found under ${SCAN_DIRS[*]}"

# ── Guard 3: vendor-closure completeness ─────────────────────────────────────

# Extract required DLL names from the deps.json runtime entries.
# Excludes RockwellAutomation* (non-redistributable, never shipped with the artifact).
REQUIRED_DLLS=$(python3 - "$DEPS_JSON" <<'PYEOF'
import sys, json

deps_path = sys.argv[1]
with open(deps_path) as f:
    data = json.load(f)

target_key = list(data['targets'].keys())[0]
required = set()
for _pkg, info in data['targets'][target_key].items():
    for dll_path in info.get('runtime', {}).keys():
        dll_name = dll_path.split('/')[-1]
        if dll_name.endswith('.dll') and not dll_name.startswith('RockwellAutomation'):
            required.add(dll_name)

for dll in sorted(required):
    print(dll)
PYEOF
)

if [[ -z "$REQUIRED_DLLS" ]]; then
    echo "ERROR: No required assemblies extracted from deps.json at: $DEPS_JSON" >&2
    echo "       The file may be malformed or the SdkVendorHarvest publish step failed." >&2
    exit 1
fi

MISSING=0
echo "Checking vendor closure against: $DEPS_JSON"
while IFS= read -r dll; do
    if [[ ! -f "$VENDOR_DIR/$dll" ]]; then
        echo "  MISSING: $dll" >&2
        MISSING=$((MISSING + 1))
    fi
done <<< "$REQUIRED_DLLS"

if [[ $MISSING -gt 0 ]]; then
    echo "" >&2
    if [[ $MISSING -eq 1 ]]; then
        echo "ERROR: 1 required vendor assembly is absent from sdk/vendor/." >&2
    else
        echo "ERROR: $MISSING required vendor assemblies are absent from sdk/vendor/." >&2
    fi
    echo "       This indicates an incomplete transitive closure." >&2
    echo "       SdkAssemblyLoadContext requires every listed assembly to be" >&2
    echo "       present on disk at runtime — missing entries cause load failures." >&2
    exit 1
fi

TOTAL=$(echo "$REQUIRED_DLLS" | wc -l | tr -d ' ')
echo "Vendor closure OK: all $TOTAL required assemblies present in $VENDOR_DIR"
echo "All publish-output guards passed."
