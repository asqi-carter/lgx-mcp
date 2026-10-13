#!/usr/bin/env bash
# cold-install-gate.sh — Release gate verifying the published artifact runs standalone.
#
# Copies the published binary (and, for SDK builds, the sdk/ subdirectory) into a
# clean temporary directory (no repo checkout, no .localfeed, no NuGet cache, no
# LOGIX_SDK_PATH) and exercises the following scenarios:
#
#   Base probe (all builds):
#     Asserts the server starts and logix_ping returns a valid response.
#
#   Scenario A — adapter present (SDK builds only):
#     Copies sdk/LogixMcp.SdkAdapter.dll alongside the binary, starts the server,
#     asserts logix_ping succeeds, then asserts logix_connect backend="sdk" returns
#     either a valid session (ConnectResult) or a structured unavailable error
#     (ConnectSdkNotAvailableError or ConnectError).  A dead process is never a
#     passing outcome.
#
#   Scenario B — adapter deliberately deleted (SDK builds only):
#     Deletes the adapter DLL from sdk/ in the gate workdir, starts a fresh server,
#     asserts the process is still alive after initialize, then asserts
#     logix_connect backend="sdk" returns ConnectSdkNotAvailableError (the payload
#     must contain a "probedPaths" field).  A dead process is never a passing
#     outcome.
#
# For SDK builds the gate also asserts that LogixMcp.SdkAdapter.dll is present in
# the publish output directory.  This catches the class of defect where dotnet
# publish emits only LogixMcp.exe/LogixMcp and LogixMcp.pdb without the adapter.
#
# A release that fails this gate is considered broken regardless of unit/integration
# test results.
#
# Usage:
#   scripts/cold-install-gate.sh [BINARY_PATH]
#
#   BINARY_PATH — optional path to the pre-built self-contained binary.
#                 Defaults to the linux-x64 no-SDK publish output (publish-nosdk).
#                 On Windows pass the path to LogixMcp.exe.
#
# SDK detection (controls whether the adapter DLL assertion is enforced).
# Decided the way Directory.Build.props decides it: EnableLogixSdk=true turns the
# SDK build on, anything else (including unset) is a no-SDK build.  The gate does
# not scan any directory.
#   EnableLogixSdk=true   scripts/cold-install-gate.sh   # MSBuild-style env var
#   ENABLE_LOGIX_SDK=true scripts/cold-install-gate.sh   # shell-style env var
#   (unset)                                               # no-SDK build
#
# CIP lane probe (optional):
#   LOGIX_CIP_GATEWAY=<host>  scripts/cold-install-gate.sh   # enable the CIP probe
#   LOGIX_CIP_PATH=1,0                                       # optional route path
#   (unset)                                                  # CIP probe is skipped
#   With no endpoint configured the CIP step prints a SKIP message and performs
#   no network I/O, so it cannot time out on a fresh machine.
#   With an endpoint configured but no controller behind it, Step 1 passes on the
#   structured ConnectionFailed error (no controller in the payload), so the gate
#   does not need a live controller.
#
# Probe timeout is 25 seconds (PROBE_TIMEOUT_SECS) to accommodate up to 15 s cold
# token acquisition when the Rockwell SDK is present on the target machine.
#
# Prerequisites: the binary must already be built (run 'make publish-linux-nosdk' or
# 'make publish-win-nosdk' first; this script does not invoke dotnet publish).

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

# Default binary path: linux-x64 no-SDK publish output (make publish-linux-nosdk).
DEFAULT_BINARY="$PROJECT_ROOT/src/LogixMcp/bin/Release/net10.0/linux-x64/publish-nosdk/LogixMcp"
BINARY="${1:-$DEFAULT_BINARY}"

# Publish directory — the directory that contains the binary (and any companion files
# such as LogixMcp.SdkAdapter.dll that are excluded from the single-file bundle).
PUBLISH_DIR="$(dirname "$BINARY")"

# Probe timeout in seconds.  Must be >= 20 s — a cold token acquisition can take ~15 s.
PROBE_TIMEOUT_SECS=25

# ── Detect SDK build mode ─────────────────────────────────────────────────────
# Directory.Build.props turns the SDK build on only when EnableLogixSdk resolves to
# "true"; any other value (or none) is a no-SDK build.  The gate decides the same
# way from the caller's environment and looks at no directory.
#
#   1. EnableLogixSdk env var (MSBuild-style, set by CI / callers that know the build config)
#   2. ENABLE_LOGIX_SDK env var (shell-style alias)

_sdk_env="${EnableLogixSdk:-${ENABLE_LOGIX_SDK:-}}"

if [[ "$_sdk_env" == "true" ]]; then
    SDK_BUILD=true
else
    SDK_BUILD=false
fi

# ── Validate binary exists ────────────────────────────────────────────────────

if [[ ! -f "$BINARY" ]]; then
    echo "ERROR: binary not found at: $BINARY" >&2
    echo "" >&2
    echo "Build it first:" >&2
    echo "  make publish-linux-nosdk     # Linux/CI" >&2
    echo "  make publish-win-nosdk       # Windows .exe" >&2
    exit 1
fi

echo "==> cold-install gate"
echo "    binary : $BINARY"
echo "    sdk    : $SDK_BUILD"
echo "    timeout: ${PROBE_TIMEOUT_SECS}s"

# ── Assert adapter DLL is present in publish output (SDK builds only) ─────────
# When EnableLogixSdk=true the IncludeSdkAdapterInPublish MSBuild target must have
# copied LogixMcp.SdkAdapter.dll into the publish directory alongside the binary.
# A publish output that lacks this file means the SDK lane can never activate, which
# is exactly the class of defect this gate exists to catch.

if [[ "$SDK_BUILD" == "true" ]]; then
    # The IncludeSdkAdapterInPublish target places the adapter in the sdk/
    # subdirectory of the publish dir (RelativePath=sdk/LogixMcp.SdkAdapter.dll
    # in LogixMcp.csproj), mirroring the layout SdkAdapterLoader.TryLoad() expects
    # at runtime (AppContext.BaseDirectory/sdk/LogixMcp.SdkAdapter.dll).
    ADAPTER_DLL="$PUBLISH_DIR/sdk/LogixMcp.SdkAdapter.dll"
    if [[ ! -f "$ADAPTER_DLL" ]]; then
        echo "ERROR: SDK build detected (EnableLogixSdk=true) but sdk/LogixMcp.SdkAdapter.dll" >&2
        echo "       is missing from the publish output directory." >&2
        echo "       Expected: $ADAPTER_DLL" >&2
        echo "" >&2
        echo "       dotnet publish must have produced only LogixMcp(.exe)/LogixMcp.pdb." >&2
        echo "       Verify that the IncludeSdkAdapterInPublish MSBuild target ran and" >&2
        echo "       that the adapter built successfully with EnableLogixSdk=true." >&2
        exit 1
    fi
    echo "    adapter: $ADAPTER_DLL  [present]"
fi

# ── Create a clean temporary directory ───────────────────────────────────────
# The temp dir has no repo files, no .localfeed, no NuGet cache, no SDK env.

GATE_TMPDIR="$(mktemp -d)"
trap 'rm -rf "$GATE_TMPDIR"' EXIT

echo "    workdir: $GATE_TMPDIR"

# Preserve the .exe extension when copying so the binary can be executed on
# Windows (Git Bash / WSL), where the OS requires the extension to launch PE
# executables.  On Linux the extension is absent and BINARY_EXT stays empty.
BINARY_EXT=""
if [[ "$BINARY" == *.exe ]]; then
    BINARY_EXT=".exe"
fi

cp "$BINARY" "$GATE_TMPDIR/LogixMcp${BINARY_EXT}"
chmod +x "$GATE_TMPDIR/LogixMcp${BINARY_EXT}"

# ── Copy SDK adapter for SDK builds ──────────────────────────────────────────
# When SDK_BUILD=true, copy sdk/LogixMcp.SdkAdapter.dll into the gate workdir
# alongside the binary so SdkAdapterLoader.TryLoad() can find the adapter at
# the expected path (AppContext.BaseDirectory/sdk/LogixMcp.SdkAdapter.dll).
# Without this copy the adapter is never probed and the SDK lane can never
# activate — the exact class of defect scenario A is designed to catch.

ADAPTER_GATE_PATH=""
if [[ "$SDK_BUILD" == "true" ]]; then
    _SDK_SRC="$PUBLISH_DIR/sdk/LogixMcp.SdkAdapter.dll"
    if [[ ! -f "$_SDK_SRC" ]]; then
        echo "ERROR: SDK build detected but sdk/LogixMcp.SdkAdapter.dll is missing" >&2
        echo "       Expected: $_SDK_SRC" >&2
        echo "" >&2
        echo "       The IncludeSdkAdapterInPublish MSBuild target places the adapter" >&2
        echo "       in sdk/ alongside the binary.  Verify the SDK build succeeded." >&2
        exit 1
    fi
    mkdir -p "$GATE_TMPDIR/sdk"
    cp "$_SDK_SRC" "$GATE_TMPDIR/sdk/LogixMcp.SdkAdapter.dll"
    ADAPTER_GATE_PATH="$GATE_TMPDIR/sdk/LogixMcp.SdkAdapter.dll"
    echo "    sdk/adapter: $ADAPTER_GATE_PATH  [copied to gate workdir]"
fi

echo "==> probing server (logix_ping; SDK scenarios: $SDK_BUILD) ..."

# ── Python probe ─────────────────────────────────────────────────────────────
# Sends MCP JSON-RPC over stdio (NDJSON transport) and validates the response.
# Written inline so the gate has no external Python dependencies beyond stdlib.
#
# When ADAPTER_GATE_PATH is set (SDK builds) the probe exercises two scenarios:
#
#   Scenario A — adapter present:
#     Asserts logix_ping succeeds AND logix_connect backend="sdk" returns a
#     valid structured response (ConnectResult, ConnectError, or
#     ConnectSdkNotAvailableError).  A dead process is never a passing outcome.
#
#   Scenario B — adapter deliberately deleted from sdk/:
#     Deletes the adapter DLL from the gate workdir, starts a fresh server,
#     asserts the process is still alive after initialize, and asserts
#     logix_connect backend="sdk" returns ConnectSdkNotAvailableError
#     (the payload must contain a "probedPaths" field).  A dead process is
#     never a passing outcome.

python3 - "$GATE_TMPDIR" "$PROBE_TIMEOUT_SECS" "$ADAPTER_GATE_PATH" <<'PYEOF'
import json
import os
import select
import subprocess
import sys
import time
from typing import Optional

gate_dir         = sys.argv[1]
probe_timeout    = int(sys.argv[2])
adapter_dll_path = sys.argv[3] if len(sys.argv) > 3 and sys.argv[3] else None

# Detect the binary name: LogixMcp.exe (Windows) or LogixMcp (Linux/macOS).
# The shell copies the binary with its original extension preserved so that
# Windows can launch it without a .exe suffix missing.  We detect by probing
# both names rather than hard-coding so this works for both platforms.
_exe_path = os.path.join(gate_dir, "LogixMcp.exe")
_bin_path = os.path.join(gate_dir, "LogixMcp")
binary    = _exe_path if os.path.exists(_exe_path) else _bin_path

# Strip SDK path so the probe is truly cold (no Rockwell SDK influence).
env = {k: v for k, v in os.environ.items() if k != "LOGIX_SDK_PATH"}


# ── Helper: start the server process ─────────────────────────────────────────

def start_proc() -> subprocess.Popen:
    """Launch the server binary and return its Popen handle."""
    return subprocess.Popen(
        [binary],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        cwd=gate_dir,
        env=env,
    )


# ── Helper: send / receive NDJSON frames ─────────────────────────────────────

def send(proc: subprocess.Popen, msg: dict) -> None:
    """Write a single NDJSON line to the server's stdin."""
    line = json.dumps(msg, separators=(",", ":")) + "\n"
    proc.stdin.write(line.encode())
    proc.stdin.flush()


def recv(proc: subprocess.Popen, timeout: float) -> Optional[dict]:
    """Read one NDJSON line from the server's stdout within *timeout* seconds."""
    deadline = time.monotonic() + timeout
    buf = b""
    while time.monotonic() < deadline:
        remaining = max(0.0, deadline - time.monotonic())
        r, _, _ = select.select([proc.stdout], [], [], min(remaining, 0.5))
        if not r:
            if proc.poll() is not None:
                # Server exited unexpectedly.
                stderr = proc.stderr.read().decode(errors="replace")
                print(f"FAIL: server exited early (rc={proc.returncode})", file=sys.stderr)
                if stderr:
                    print(f"      stderr: {stderr[:500]}", file=sys.stderr)
                return None
            continue
        chunk = proc.stdout.read1(4096)  # non-blocking read of available bytes
        if not chunk:
            continue
        buf += chunk
        if b"\n" in buf:
            line, _, buf = buf.partition(b"\n")
            try:
                return json.loads(line)
            except json.JSONDecodeError as exc:
                print(f"FAIL: invalid JSON from server: {exc}", file=sys.stderr)
                return None
    return None


def terminate_proc(proc: subprocess.Popen) -> None:
    """Terminate the server process gracefully, escalating to kill on timeout."""
    proc.terminate()
    try:
        proc.wait(timeout=3)
    except subprocess.TimeoutExpired:
        proc.kill()


# ── Helper: MCP handshake steps ──────────────────────────────────────────────

def do_initialize(proc: subprocess.Popen, timeout: float, label: str = "") -> bool:
    """
    Send the MCP initialize request and validate the response.

    Returns True on success, False on any failure.  On failure prints a
    FAIL line to stderr so the shell caller can report the gate broken.
    """
    send(proc, {
        "jsonrpc": "2.0",
        "id": 1,
        "method": "initialize",
        "params": {
            "protocolVersion": "2024-11-05",
            "capabilities": {},
            "clientInfo": {"name": "cold-gate", "version": "0.0.1"},
        },
    })
    resp = recv(proc, timeout)
    if resp is None:
        print(f"FAIL{label}: no response to initialize within {timeout}s"
              f" — server may not have started", file=sys.stderr)
        return False
    if resp.get("error"):
        print(f"FAIL{label}: initialize returned error: {resp['error']}", file=sys.stderr)
        return False
    if "result" not in resp:
        print(f"FAIL{label}: initialize response missing 'result': {resp}", file=sys.stderr)
        return False
    server_info = resp["result"].get("serverInfo", {})
    print(f"     initialize OK  {label}— server: {server_info.get('name', '?')} "
          f"v{server_info.get('version', '?')}")
    return True


def do_notifications_initialized(proc: subprocess.Popen) -> None:
    """Send the notifications/initialized handshake frame (no response expected)."""
    send(proc, {"jsonrpc": "2.0", "method": "notifications/initialized", "params": {}})


def do_logix_ping(proc: subprocess.Popen, timeout: float, label: str = "") -> bool:
    """
    Call logix_ping and assert the response carries status="ok".

    Returns True on success, False on any failure.
    """
    send(proc, {
        "jsonrpc": "2.0",
        "id": 2,
        "method": "tools/call",
        "params": {"name": "logix_ping", "arguments": {}},
    })
    resp = recv(proc, timeout)
    if resp is None:
        print(f"FAIL{label}: no response to logix_ping within {timeout}s", file=sys.stderr)
        return False
    if resp.get("error"):
        print(f"FAIL{label}: logix_ping returned JSON-RPC error: {resp['error']}", file=sys.stderr)
        return False
    # Result content is a list of blocks; the first block's 'text' is the
    # JSON-serialised PingResponse.
    content = resp.get("result", {}).get("content", [])
    text    = content[0].get("text", "") if content else ""
    try:
        ping = json.loads(text)
    except json.JSONDecodeError:
        ping = {}
    if ping.get("status") != "ok":
        print(f"FAIL{label}: logix_ping payload unexpected: {text!r}", file=sys.stderr)
        return False
    print(f"     logix_ping OK  {label}— {text}")
    return True


def do_logix_connect_sdk(
    proc: subprocess.Popen,
    timeout: float,
    req_id: int,
    label: str = "",
    require_probed_paths: bool = False,
) -> bool:
    """
    Call logix_connect with backend="sdk" and validate the response.

    This helper enforces the invariant that a dead process is never a passing
    outcome: it checks proc.poll() both before sending the request and after
    receiving the response.

    When *require_probed_paths* is True (Scenario B — adapter absent) the
    response payload must be a ConnectSdkNotAvailableError, identified by the
    presence of a "probedPaths" field.  Any other payload is a FAIL.

    When *require_probed_paths* is False (Scenario A — adapter present) any
    structured tool-level response is accepted:
      • ConnectResult         — {"status": "connected", "backend": "sdk", ...}
      • ConnectSdkNotAvailableError — {"error": "...", "probedPaths": [...]}
      • ConnectError          — {"error": "No project loaded..."}

    A JSON-RPC protocol error or an unrecognised payload is always a FAIL.

    Returns True on success, False on any failure.
    """
    # Assert the process is alive before we even send the request.
    if proc.poll() is not None:
        print(f"FAIL{label}: server is dead before logix_connect call "
              f"(rc={proc.returncode})", file=sys.stderr)
        return False

    send(proc, {
        "jsonrpc": "2.0",
        "id": req_id,
        "method": "tools/call",
        "params": {
            "name": "logix_connect",
            "arguments": {"gateway": "127.0.0.1", "path": "1,0", "backend": "sdk"},
        },
    })

    resp = recv(proc, timeout)

    # Assert the process is still alive after receiving the response.
    if proc.poll() is not None:
        print(f"FAIL{label}: server died while handling "
              f"logix_connect backend='sdk' (rc={proc.returncode})", file=sys.stderr)
        return False

    if resp is None:
        print(f"FAIL{label}: no response to logix_connect within {timeout}s"
              f" — server may have crashed", file=sys.stderr)
        return False

    if resp.get("error"):
        # JSON-RPC protocol-level error (e.g. method not found).
        print(f"FAIL{label}: logix_connect returned JSON-RPC error: {resp['error']}",
              file=sys.stderr)
        return False

    # Unpack the tool result payload.
    content = resp.get("result", {}).get("content", [])
    text    = content[0].get("text", "") if content else ""
    try:
        payload = json.loads(text)
    except json.JSONDecodeError:
        print(f"FAIL{label}: logix_connect result is not valid JSON: {text!r}",
              file=sys.stderr)
        return False

    if require_probed_paths:
        # Scenario B: adapter absent → response must be ConnectSdkNotAvailableError.
        if "probedPaths" not in payload:
            print(
                f"FAIL{label}: expected ConnectSdkNotAvailableError (with 'probedPaths') "
                f"but got: {text!r}",
                file=sys.stderr,
            )
            return False
        n_paths = len(payload["probedPaths"])
        print(f"     logix_connect backend='sdk' OK  {label}— "
              f"structured unavailable error ({n_paths} probed path(s))")
    else:
        # Scenario A: adapter present → accept any structured tool-level response.
        if "status" in payload:
            # ConnectResult — SDK session opened (requires Rockwell DLLs present).
            print(f"     logix_connect backend='sdk' OK  {label}— "
                  f"session connected (status={payload['status']!r})")
        elif "probedPaths" in payload:
            # ConnectSdkNotAvailableError — adapter loaded but Rockwell DLLs absent.
            n_paths = len(payload["probedPaths"])
            print(f"     logix_connect backend='sdk' OK  {label}— "
                  f"structured unavailable error ({n_paths} probed path(s))")
        elif "error" in payload:
            # ConnectError — adapter present and factory registered, but no project
            # loaded (or similar recoverable error).
            snippet = payload["error"][:80]
            print(f"     logix_connect backend='sdk' OK  {label}— "
                  f"structured error: {snippet!r}")
        else:
            print(
                f"FAIL{label}: logix_connect backend='sdk' returned unrecognised payload: "
                f"{text!r}",
                file=sys.stderr,
            )
            return False

    return True


def do_logix_cip_probe(
    proc: subprocess.Popen,
    timeout: float,
    req_id_connect: int,
    req_id_read: int,
    label: str = "",
) -> bool:
    """
    CIP lane probe: verifies the CIP stack initialises cleanly in the
    single-file binary against a configured CIP endpoint.

    The endpoint comes from the LOGIX_CIP_GATEWAY environment variable
    (optionally LOGIX_CIP_PATH, default "1,0").  When LOGIX_CIP_GATEWAY is
    unset or empty the whole probe is SKIPPED with a message: no request is
    sent, no connection is attempted and nothing can time out.  The gate does
    not guess a loopback address, because nothing listens there on a fresh
    machine and the connection attempt would stall until the probe timeout.

    Step 1 — logix_connect backend='cip' gateway=<LOGIX_CIP_GATEWAY>:
      The connect handler probes the gateway after storing it.  With a
      controller behind the gateway the result is status='connected'.  With no
      controller it is the structured ConnectCipProbeError (kind
      'ConnectionFailed', plus the gateway and path tried, and no controller), which also passes: the gate
      needs no live controller.
      Asserts: no JSON-RPC error, process alive, and either
      status='connected' or a structured kind='ConnectionFailed' error that
      carries no controller.

    Step 2 — logix_read 'CipProbeTag':
      Sends a read for a tag that does not exist.  Whether step 1 connected or
      failed, the read must return a structured tool-level payload: either a
      ReadTagError ({"error": ..., "kind": ...}, for example no session, tag
      not found or connection failure) or, unexpectedly, a ReadTagResult
      (has "value").  Either is accepted; the invariant is structured output,
      not a specific error.  A JSON-RPC protocol error, no response, or a dead
      process fails.  When step 1 connected, the read also exercises the CIP
      stack initialisation (LibPlcTagNativeInit.EnsureInitialized(), the
      single-file CIP bootstrap).

    The distinction being tested:
      PASS — structured payload:   {"error": "...", "kind": "..."} or {"value": ...}
      FAIL — JSON-RPC error:       {"error": {"code": ..., "message": ...}}
      FAIL — process dies during or after the call

    A JSON-RPC protocol-level error indicates the CIP stack could not
    initialise (e.g. NotSupportedException from Assembly.CodeBase in a
    single-file binary), meaning the error never reached the handler's
    catch-all and propagated as an unhandled exception.

    Returns True on success or skip, False on any failure.
    """
    gateway = os.environ.get("LOGIX_CIP_GATEWAY", "").strip()
    if not gateway:
        print(f"     SKIP{label}: CIP lane probe — LOGIX_CIP_GATEWAY not set "
              f"(no endpoint configured; no network I/O attempted)")
        return True
    cip_path = os.environ.get("LOGIX_CIP_PATH", "1,0").strip() or "1,0"

    # Assert the process is alive before we send anything.
    if proc.poll() is not None:
        print(f"FAIL{label}: server is dead before CIP probe "
              f"(rc={proc.returncode})", file=sys.stderr)
        return False

    # ── Step 1: logix_connect backend='cip' ──────────────────────────────────
    send(proc, {
        "jsonrpc": "2.0",
        "id": req_id_connect,
        "method": "tools/call",
        "params": {
            "name": "logix_connect",
            "arguments": {"gateway": gateway, "path": cip_path, "backend": "cip"},
        },
    })

    resp = recv(proc, timeout)

    if proc.poll() is not None:
        print(f"FAIL{label}: server died during CIP logix_connect "
              f"(rc={proc.returncode})", file=sys.stderr)
        return False

    if resp is None:
        print(f"FAIL{label}: no response to logix_connect backend='cip' "
              f"within {timeout}s", file=sys.stderr)
        return False

    if resp.get("error"):
        print(f"FAIL{label}: logix_connect backend='cip' returned JSON-RPC "
              f"error: {resp['error']}", file=sys.stderr)
        return False

    content = resp.get("result", {}).get("content", [])
    text    = content[0].get("text", "") if content else ""
    try:
        payload = json.loads(text)
    except json.JSONDecodeError:
        print(f"FAIL{label}: logix_connect backend='cip' result not valid "
              f"JSON: {text!r}", file=sys.stderr)
        return False

    if payload.get("status") == "connected":
        print(f"     logix_connect backend='cip' OK  {label}— "
              f"gateway={gateway}")
    elif payload.get("kind") == "ConnectionFailed" and "controller" not in payload:
        # Structured connect failure with no controller: the handler ran and the
        # CIP stack answered with a tool-level error.  No live controller needed.
        snippet = str(payload.get("error", ""))[:80]
        print(f"     logix_connect backend='cip' OK  {label}— structured "
              f"ConnectionFailed (no controller): {snippet!r}")
    else:
        print(f"FAIL{label}: logix_connect backend='cip' returned neither "
              f"status='connected' nor a structured ConnectionFailed error: "
              f"{text!r}", file=sys.stderr)
        return False

    # ── Step 2: logix_read — triggers CIP stack init + connection attempt ────
    # libplctag will attempt to connect to the configured gateway (EtherNet/IP
    # port 44818).  If no PLC answers, the attempt fails.  The response MUST
    # be a structured tool-level ReadTagError (has "error" field) rather than
    # a JSON-RPC protocol error.  A protocol error or dead process indicates
    # the CIP stack (LibPlcTagNativeInit) failed to initialise.
    send(proc, {
        "jsonrpc": "2.0",
        "id": req_id_read,
        "method": "tools/call",
        "params": {
            "name": "logix_read",
            "arguments": {"tag": "CipProbeTag"},
        },
    })

    resp = recv(proc, timeout)

    if proc.poll() is not None:
        print(f"FAIL{label}: server died during CIP logix_read "
              f"(rc={proc.returncode})", file=sys.stderr)
        return False

    if resp is None:
        print(f"FAIL{label}: no response to logix_read within {timeout}s "
              f"— server may have crashed or stalled during CIP initialisation",
              file=sys.stderr)
        return False

    if resp.get("error"):
        # JSON-RPC protocol-level error: the CIP stack threw an unhandled
        # exception (e.g. NotSupportedException from Assembly.CodeBase in a
        # single-file binary) that was not caught by the handler's catch-all.
        print(f"FAIL{label}: logix_read returned JSON-RPC error — CIP stack "
              f"init failure: {resp['error']}", file=sys.stderr)
        return False

    # Unpack the tool result and verify it is a structured payload.
    content = resp.get("result", {}).get("content", [])
    text    = content[0].get("text", "") if content else ""
    try:
        payload = json.loads(text)
    except json.JSONDecodeError:
        print(f"FAIL{label}: logix_read result is not valid JSON: {text!r}",
              file=sys.stderr)
        return False

    # Accept ReadTagError (has "error" field) — the expected outcome when no
    # PLC is reachable.  Also accept an unexpected ReadTagResult (has "value")
    # without failing — the invariant is structured output, not a specific error.
    if "error" not in payload and "value" not in payload:
        print(f"FAIL{label}: logix_read CIP payload is neither a ReadTagResult "
              f"nor a ReadTagError: {text!r}", file=sys.stderr)
        return False

    if "error" in payload:
        kind_str = payload.get("kind", "?")
        snippet  = payload["error"][:80]
        print(f"     logix_read CIP OK  {label}— structured error "
              f"(kind={kind_str!r}): {snippet!r}")
    else:
        print(f"     logix_read CIP OK  {label}— unexpected success: {text!r}")

    return True


# ── Scenario A: adapter present (SDK build) OR base probe (non-SDK build) ─────
#
# Always runs.  For SDK builds the adapter DLL has been copied to sdk/ inside
# the gate workdir so SdkAdapterLoader.TryLoad() can find it.
#
# Assertions:
#   • logix_ping returns status="ok"
#   • (SDK only) logix_connect backend="sdk" returns a valid structured response
#     AND the process is still alive after the call
#   • (all builds, only when LOGIX_CIP_GATEWAY is set; otherwise skipped with a
#     message) CIP lane probe: logix_connect backend="cip" returns
#     ConnectResult AND logix_read returns a structured ReadTagError (not a
#     JSON-RPC error or dead process) — verifies CIP stack initialises cleanly
#     in the single-file binary without a live controller

exit_code = 0
label_a   = "[scenario-A] " if adapter_dll_path else ""

proc = start_proc()
try:
    if not do_initialize(proc, probe_timeout, label_a):
        exit_code = 1
    else:
        do_notifications_initialized(proc)
        if not do_logix_ping(proc, probe_timeout, label_a):
            exit_code = 1
        elif adapter_dll_path:
            # SDK build: probe logix_connect with adapter present, then CIP lane.
            if not do_logix_connect_sdk(
                proc, probe_timeout, req_id=3, label=label_a,
                require_probed_paths=False,
            ):
                exit_code = 1
            elif not do_logix_cip_probe(
                proc, probe_timeout,
                req_id_connect=4, req_id_read=5, label=label_a,
            ):
                exit_code = 1
        else:
            # Non-SDK build: CIP is the primary backend; probe it directly.
            if not do_logix_cip_probe(
                proc, probe_timeout,
                req_id_connect=3, req_id_read=4, label=label_a,
            ):
                exit_code = 1
except Exception as exc:
    print(f"FAIL {label_a}probe exception: {exc}", file=sys.stderr)
    exit_code = 1
finally:
    terminate_proc(proc)

if exit_code:
    sys.exit(exit_code)

# ── Scenario B: adapter deliberately deleted (SDK build only) ─────────────────
#
# Deletes LogixMcp.SdkAdapter.dll from sdk/ in the gate workdir, then starts a
# fresh server and verifies:
#   • The server starts successfully (initialize OK) — missing adapter must not
#     crash the process.
#   • The process is still alive after initialize.
#   • logix_connect backend="sdk" returns ConnectSdkNotAvailableError, identified
#     by the presence of "probedPaths" in the payload.
#   • The process is still alive after the logix_connect call.

if adapter_dll_path:
    label_b = "[scenario-B] "
    print(f"\n==> Scenario B: deleting adapter DLL from sdk/ and starting fresh server ...")

    try:
        os.remove(adapter_dll_path)
        print(f"     deleted: {adapter_dll_path}")
    except OSError as exc:
        print(f"FAIL {label_b}could not delete adapter DLL: {exc}", file=sys.stderr)
        sys.exit(1)

    proc = start_proc()
    try:
        if not do_initialize(proc, probe_timeout, label_b):
            exit_code = 1
        else:
            do_notifications_initialized(proc)

            # Explicitly assert the process is alive — adapter absence at startup
            # must not kill the server.
            if proc.poll() is not None:
                print(
                    f"FAIL {label_b}server died after startup with adapter absent "
                    f"(rc={proc.returncode})",
                    file=sys.stderr,
                )
                exit_code = 1
            else:
                # logix_connect backend="sdk" must return ConnectSdkNotAvailableError.
                if not do_logix_connect_sdk(
                    proc, probe_timeout, req_id=2, label=label_b,
                    require_probed_paths=True,
                ):
                    exit_code = 1
    except Exception as exc:
        print(f"FAIL {label_b}probe exception: {exc}", file=sys.stderr)
        exit_code = 1
    finally:
        terminate_proc(proc)

sys.exit(exit_code)
PYEOF

echo "==> cold-install gate PASSED"
