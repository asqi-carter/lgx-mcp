# Troubleshooting

Symptom-indexed fixes. See [`docs/errors.md`](errors.md) for the `kind`
taxonomy this page cross-references.

## Echo activation grace period

**Symptom:** an emulated controller (FactoryTalk Logix Echo) that was
working stops responding after some number of days with no configuration
change.

**Cause:** Echo activation runs on a grace period. A controller that is
already running survives the grace period lapsing, but the Echo service will
not *restart* a stopped controller once the grace period has expired without
a fresh activation.

**Fix:** re-activate Echo per your license terms; do not assume a working
Echo install stays working indefinitely with no activation check-in.

## `logix_connect` (cip) fails with `CipPathInvalid`

**Cause:** no backplane `path` was given. ControlLogix and CompactLogix 5x80
controllers usually need `1,<slot>`, for example `1,0`. The server does not
guess the slot.

**Fix:** reconnect with `path` set. If a path was given and you get
`ConnectionFailed`, verify the address, path and that the controller is running.

## `logix_connect` (cip) returns `ConnectionFailed` after about 15 seconds

**Symptom:** `logix_connect backend="cip"` to an address with nothing
listening, or to an unroutable or firewalled address, used to appear to hang
with no response. It now returns `ConnectionFailed` within the connect
probe's hard 15-second deadline, with a message like "timed out connecting to
gateway '<ip>' using path '<path>' after N s (hard deadline 15 s); the
controller did not respond". The server keeps answering the next request.

**Cause:** the controller did not answer at all: wrong address, controller
powered off, no route, or a firewall dropping TCP port 44818. This is a
network-level failure, not a comm-path formatting problem.

**Fix:** check the gateway address, ping it, and confirm port 44818 is
reachable from the machine running the server. Run `logix_doctor` for the
reachability check. If the message is instead about the CIP library not
loading, see the next entry.

## `logix_connect` (cip) fails with "Dll was not found" (`CipRuntimeMissing`)

**Symptom:** on a clean Windows machine, `logix_connect backend="cip"` fails
immediately. Older builds reported "Connection failed: could not reach a
controller ... Dll was not found." together with advice to check the
controller's power and network. Current builds return `CipRuntimeMissing`.

**Cause:** the CIP library's native component (`plctag.dll`, extracted to
`%TEMP%\libplctag_<pid>\`) needs `vcruntime140.dll` and the Universal C
runtime, which come with the Microsoft Visual C++ 2015–2022 Redistributable
(x64). A clean Windows install does not have it; Studio 5000 installs it. No
controller was contacted, so the controller and network are not the problem.

**Fix:** install `vc_redist.x64.exe` from Microsoft, then restart the MCP
server. `bootstrap/setup.ps1` warns when `vcruntime140.dll` is missing, and
the `cipRuntime` check in `logix_doctor` is Ok once the library loads. The
runtime is not bundled with lgx-mcp. After installing it, a connect to an
address with nothing listening returns `ConnectionFailed` (see the previous
entry).

## `logix_import_datatype` returns `NewDatatypeUnsupported`

This tool changes existing data types only; it cannot create one. A dry run
previews the fragment and reports `willCreate: true`. Create the type in
Studio 5000, then use this tool for later changes to it.

## `logix_import_datatype` returns `TargetExists`

**Symptom:** a real run (`dryRun=false`) is refused with `kind: "TargetExists"`.

**Cause:** the `saveAs` file already exists and `overwrite=true` was not passed.
Nothing was written and the existing file is untouched. The tool never replaces
a file silently.

**Fix:** choose a new `saveAs`, or pass `overwrite=true` to replace the file.
The result is built in a temp file beside `saveAs` and moved into place, so a
failed import leaves an existing `saveAs` intact. `saveAs` may never be the
source `acdPath` (`SaveAsEqualsInput`).

## `logix_write` returns `AuditUnavailable`

**Symptom:** a write is refused with `kind: "AuditUnavailable"` and nothing
reaches the controller.

**Cause:** `LGX_AUDIT_FAIL_CLOSED=1` is set and the pre-write check found that the
audit log could not be opened for append (audit directory not writable, disk
full, file locked).

**Fix:** make the log directory writable (the `audit` check in `logix_doctor`
and its `auditWriteFailures` count show the state), or unset
`LGX_AUDIT_FAIL_CLOSED` to let writes proceed when the log is unavailable.

## `logix_verify_interlock` returns `verified: false` and I expected `true`

This is very likely the tool working correctly, not a bug. `verified: false`
means it found at least one rung that writes the output tag **without** the
condition tag present as a contact — read the `rungs` array in the response;
the entry with `conditionPresent: false` names the exact program, routine,
and rung number that breaks the interlock. This is a static structural proof
over rung neutral-text: it cannot observe runtime execution, `JSR`/`RET`
branching across routines, or controller-mode effects — a routine that is
never actually called still counts as a write path for this check.

## Every live write is refused

**Symptom:** every live-write call is refused immediately, with no
controller-side error at all.

**Cause:** `LGX_ENABLE_LIVE_WRITES` is unset. By design, live controller
writes are off until this is explicitly set (a controller mode change is never
applied on a live controller).

**Fix:** set `LGX_ENABLE_LIVE_WRITES=1` once you intend to reach a real or
emulated controller. Static analysis, gate derivation, and interlock proof
all work with this unset — if those are also failing, the cause is
something else (see the error's own `kind`, [`docs/errors.md`](errors.md)).

## `logix_connect` (sdk) fails: SDK not found

These entries apply to a build with the Logix Designer SDK lane. Run
`logix_doctor` first: its `sdkInstalled` and `sdkService` checks name the
problem and the next step.

**Symptom:** `logix_connect backend="sdk"` returns an error that the SDK is
not available. The response carries `probedPaths`, the list of directories
that were searched and why each was rejected, and sometimes a
`loadFailureReason`. `logix_doctor` shows `sdkInstalled` as `warn`: "the
Logix Designer SDK assembly is not loaded".

**Cause:** the server could not find or load the Rockwell SDK assemblies. The
server does not include them. They come from your own licensed Logix Designer
and Logix Designer SDK install. A missing, partial or wrong-architecture copy
produces the same symptom, and so does an SDK install in a directory the
server does not search.

**Fix:**
1. Install Studio 5000 Logix Designer (v36 or later) and the Logix Designer
   SDK on this machine.
2. If the SDK is in a non-default place, set `LOGIX_SDK_PATH` to the directory
   that holds the `RockwellAutomation*.dll` files, then restart the MCP server.
3. If the install has the SDK's NuGet package but the server's `sdk/vendor/`
   folder is empty, run `bootstrap/provision-sdk.ps1 -AppDir <install dir>`,
   which extracts the two files the server needs from your copy of the
   package.
4. Read `probedPaths` in the response: each entry says which path was tried
   and why it was rejected.
5. Run `logix_doctor` again; `sdkInstalled` is `ok` once the SDK assembly
   loads.

The static tools, the gate and the CIP backend work without the SDK. Only
`backend="sdk"` and the SDK tools need it.

## Connect takes minutes / Request timed out

**Symptom:** `logix_connect backend="sdk"` (an online connect that uploads the
controller's project) takes minutes on a large project, and the client reports
"Request timed out" or "MCP error -32001" before the connect answers. Or
`logix_connect` returns `status: "connecting"` instead of a session.

**Cause:** an online SDK connect uploads the project, saves it as L5X, parses
it and derives the write gate. On a large project that can take longer than
the client's request timeout. The server therefore runs the connect as a
background job and waits only a synchronous budget for it. A connect that
finishes inside the budget returns its normal payload. A longer one returns
`{ status: "connecting", connectId, phase, elapsedMs, pollWith: "logix_connect_status" }`
and keeps running in the server. `connecting` is not a failure, and closing
the client request does not cancel the connect.

**Fix:**
1. Call `logix_connect_status` (read-only) and repeat it until `status` is
   `connected` or `failed`. `connected` carries the same payload `logix_connect`
   returns when it finishes in time, and the session is then attached.
   `failed` carries the connect's original typed error (`error`, `kind`,
   `hint`); use that kind in [`docs/errors.md`](errors.md).
2. Do not call `logix_connect` again while it is `connecting`. The same
   arguments return the in-flight status; different arguments are refused with
   `ConnectInProgress`. Tools that need the session also return
   `ConnectInProgress` (with `connectId`, `phase`, `elapsedMs`) until the
   connect ends.
3. If your client's request timeout is shorter than the 20 second default
   budget, lower it with `LGX_CONNECT_SYNC_BUDGET_S` (seconds) in the MCP
   server's environment and restart the server. `0` answers `connecting` at
   once. A blank, non-numeric or negative value falls back to 20. A value above
   600 (for example `1e9`) is clamped to 600 seconds and the clamp is logged to
   the server's stderr (`LGX_CONNECT_SYNC_BUDGET_S=... exceeds the maximum`).
   If the wait itself ever fails after the connect has started, `logix_connect`
   still answers `connecting` rather than an error. The variable
   changes only how long `logix_connect` waits; it does not make the connect
   faster or slower.
4. `logix_connect_status` with no connect in flight returns the last
   outcome, or `kind: "NoConnectInProgress"` when none has run since the
   server started. Call `logix_connect` first.

**Reading the phase timings:** `phase` is the last phase the connect reached
(`starting` before the first report). `phaseTimings` is a list of
`{ phase, startedMs, durationMs }`: `startedMs` is the time from the start of
the connect to the start of that phase, and `durationMs` is how long the phase
ran (for the phase still running, the time so far). The phases run in this
order:

| Phase | What it does |
|---|---|
| `upload` | pulls the controller's program image into a working ACD; usually the longest phase, it grows with project size and network speed |
| `save-l5x` | saves the uploaded project as a detailed L5X |
| `parse` | parses the saved L5X for type resolution |
| `gate-derivation` | derives the write-gate policy from the parsed project |

A `connecting` status that stays in one phase with `durationMs` still growing
is working; look at the slow phase before you assume a hang. If `elapsedMs`
keeps growing and the phase never changes for much longer than the
`upload` of a similar project took before, run `logix_doctor` (is the
controller reachable on 44818?) and check the controller and network.

## Connect shows an `sdk-restart` phase, or the SDK service crashed

**Symptom:** an online `logix_connect backend="sdk"` takes about 30 to 60
seconds longer than usual. Its `phaseTimings` show `sdk-restart` after
`upload`, followed by a second `upload`, and the result lists the crash in
`sdkRestarts`. The Windows Application event log has an `LdSdkServer.exe`
crash (event 1026) with an `IOException` saying a `tmp….acd` file is "being
used by another process", in `ProjectList.KeepAliveCleanup`. `logix_doctor`
counts these crashes.

**Cause:** a race inside the Rockwell Logix Designer SDK, not in lgx-mcp.
When the SDK uploads a controller's project, its service starts a keep-alive
countdown for the new project (about 10 seconds) *before* it sends the project
file to the client. The client starts its keep-alives only after the whole
file has arrived. If that transfer takes longer than about 9 seconds (a larger
project, or a busy machine), the service disposes the project while it is
still sending it, fails to delete the file and crashes. Windows restarts the
service.

**What lgx-mcp does:** the connect waits for the service to come back, uploads
again and usually succeeds; nothing on the controller is changed by the crash.
If the service keeps crashing, the connect gives up with `SdkServiceUnstable`
(see [errors](errors.md)).

**What it costs you:**
- The connect is slower by the restart, about 30 to 60 seconds.
- Any *other* SDK session on the same machine (another lgx-mcp server, or your
  own SDK scripts) loses its connection when the service restarts and must
  reconnect. Studio is not affected; it does not use the SDK service.
- Each crash leaves one orphaned `tmp*.acd` file in
  `C:\Windows\ServiceProfiles\LocalService\AppData\Local\Temp`. They are
  safe to delete while no SDK session is open.

**What helps:** keep the machine unloaded during connects and reuse a session
rather than reconnecting often. On our Echo bench (a 4.5 MB project), about 1
connect in 10 to 20 hit this. The fix belongs in the SDK.

## Logix Services faulted (`SdkServerFaulted`)

**Symptom:** SDK-backed tools return `kind: "SdkServerFaulted"` with
`session: "faulted"`, and every further call on the session fails the same
way. `logix_doctor` shows the `session` check as `faulted` and says that the
SDK server (Logix Services) hit a fatal error (`LgxSrv_E_FATAL_ERROR` or
`LgxSrv_E_SERVER_FAULTED`).

**Cause:** the SDK's own server process (Logix Services) died or faulted. The
session handle is dead, so nothing on it can recover.

**Fix:**
1. Call `logix_connect` again to start a new session. `logix_close_project`
   also works and disposes the faulted process.
2. Read `logix_pending_edits` before you do anything else. Edits staged on the
   workstation in the dead session are lost; an edit that already reached the
   controller (a pending zone from `target="controller"`, or a finalized
   `target="live"` change) is still there.
3. If the fault returns straight away, run `logix_doctor`: if `sdkService`
   fails, restart the `LdSdkService` Windows service and connect again.

`SdkServerFaulted` is a different failure from `SdkServiceDegraded`, where the
service is running but its communications layer has faulted and the
controller still answers on port 44818. For that, restart `LdSdkService` (the
`sdkService` check says the same).

## Upload fails with "Project is not open."

**Symptom:** after a successful `logix_connect` with a comm path (the upload
route), a later call returns an SDK message "Project is not open." The tool
reports it as `SdkServiceDegraded`.

**Cause:** this message does not mean you forgot to open a project. The SDK
service that held the uploaded project crashed or was stopped, so the
session handle no longer points at anything. It is not evidence of a pending
edit, and the server does not treat it as one: whether a pending zone exists
is decided only by reading `logix_pending_edits`.

**Fix:**
1. Check `logix_doctor`: the `sdkService` check shows whether `LdSdkService`
   is running. On a bench run recorded against an emulated controller, killing
   the SDK service's server process produced exactly this message on the next
   call, and Windows service recovery restarted the service within about 45
   seconds without any action.
2. Restart `LdSdkService` if it is not running.
3. Call `logix_connect` again with the comm path. This starts a new session.
4. Read `logix_pending_edits` to see what the controller holds. Staged
   workstation edits in the old session are gone.

## Studio automation tool refused

**Symptom:** `logix_accept_pending_edits`, `logix_test_pending_edits`,
`logix_untest_pending_edits`, `logix_assemble_pending_edits` or
`logix_cancel_pending_edits` returns `CapabilityDisabled`, `StudioNotRunning`,
`StudioOffline` or `StudioModalOpen`, and nothing happens in Studio.

**Cause:** the Studio tools are opt-in and experimental. They need
`LGX_ENABLE_STUDIO_AUTOMATION=1` and `LGX_ENABLE_LIVE_WRITES=1`, Logix Designer
open on the session's project and online (by Upload), in the same Windows
session and at the same privilege level as the server, with no dialog open.
The server never launches Studio or takes it online.

**Fix:** open the project in Studio and go online by Upload, close any open
dialog, and retry. Each refusal is sent before any Studio command, and the
kinds are listed in [`docs/errors.md`](errors.md#pending-state-errors-logix_pending_edits-finalize-accept-test-untest-assemble-cancel).
Without the switch, accept or cancel the pending edits in Logix Designer
(Logic → Online Edits).

## Got a different error?

Check [`docs/errors.md`](errors.md) for the `kind` taxonomy, then open an
issue with: the exact tool call and arguments (redact any real tag values or
IP addresses you don't want public), the full error response, and whether
`LGX_ENABLE_LIVE_WRITES` was set.

## Are forces on?

**Symptom:** you need to know whether any I/O or tag forces are installed or
enabled on the controller, and `logix_diagnose` reports `forces` with
`reported: false`.

**Cause:** Logix controllers do not report force status over CIP to an
outside client. Rockwell documents it only as the Module object's
`ForceStatus` attribute, readable with a GSV instruction in logic.
`logix_diagnose` therefore never says whether forces are on, and it does not
treat `debugMode` in the status word as forces (it only adds a note to check
in Studio).

**Fix:** Look at the Forces indicator in Studio 5000 (online), or add logic: GSV Module ForceStatus into a DINT tag (bit 0 = forces installed, bit 1 = forces enabled), then read that tag with `logix_read`.
