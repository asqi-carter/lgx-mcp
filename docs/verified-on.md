# What's verified

Version 1.0.0.

**Everything listed below concerns FactoryTalk Logix Echo — Rockwell's
software controller emulator — and nothing else. The SDK-lane rows are
maintainer-recorded results from the bench (Logix Designer v38; Logix Designer
v36 only for one datatype-import check on a project copy) and are not
re-verified for this page. None of it has been verified against a physical
controller.**

Rockwell documents this distinction directly: emulated-controller behavior
can differ from physical-controller behavior, and functionality proven on an
emulator should be re-verified on real hardware before anyone relies on it
in production. Do not treat any item below as a claim about your physical
5580, ControlLogix, or CompactLogix.

This page lists only what this release's automated tests and the Windows
bench checks below covered. Capabilities not listed here are not claimed.

**How to read the SDK-lane section.** The rows in it are results the
maintainers *recorded* during earlier bench runs. They are reported here as
recorded, **not re-verified**: whoever edited this page did not re-run them, no
run was made for this edit, and this page does not give a log path, time or
build for them. Treat each as a maintainers' report that the scenario behaved as
described on the bench, not as evidence you can trace from this page. The
results describe the builds that were on the bench, not necessarily yours.
Where a scenario has not been run, this page says "pending bench
verification" and gives the command to run it.

## Live tag writes over CIP (bench: Windows with Echo)

Each row was run against an Echo controller.

- **Typed writes to a gated tag.** Writing `1` to a BOOL tag is
  refused as `TypeMismatch` before anything is sent; writing `true` succeeds
  and is verified. The gate's denials are unchanged.
- **CIP session with no project open.** With no project and no
  allowlist, writes are refused as `WriteGateRequired` before any network
  write; an upload read back with a second client (pycomm3) shows the
  controller in RUN with its rungs unchanged. Member writes with no
  project are refused as `TypeUnknown` and nothing is written.
- **CIP session with a project open (gated).** A write to a REAL
  structure member takes `42` and is verified as REAL; writing `1.5` to a
  DINT member is refused as `TypeMismatch` ("DINT accepts whole numbers").
- **CIP session with no project, write allowlist only.** With an
  allowlist naming one tag, writes to that tag are verified (BOOL); a write
  to any other tag is denied as `NotOnAllowlist`.

## The Logix Designer SDK lane (Echo; Logix Designer v36 and v38)

These rows apply only to a build with the SDK lane. The bench was a Windows 11
machine with FactoryTalk Logix Echo (an emulated 5580 controller, revision
38.11, running a small interlock demo program), Studio 5000 Logix Designer
v38, and the Logix Designer SDK 2.2. Studio ran as a normal, non-elevated
user in the signed-in session. Logix Designer v36 was covered only as noted
in its own paragraph below. Every result below is a result recorded by the maintainers and not
re-verified for this page.

### Online rung edits through the SDK

- **Live insert in one call (`target="live"`).** Recorded by the maintainers; not re-verified. A rung and a new BOOL
  tag were inserted into a running routine with `LGX_ENABLE_LIVE_FINALIZE` on.
  The maintainers reported `status: live`, `finalized: true`, the new rung
  as normal with nothing pending, and no pending edits in Studio.
- **Live replace with `expectedRungText`.** Recorded by the maintainers; not re-verified. A rung was replaced, its
  comment was kept, and nothing was left pending.
- **Live delete and the `undo` block.** Recorded by the maintainers; not re-verified. Passing the `undo` block back
  deleted an inserted rung, restored a replaced rung's text and comment, and
  re-inserted a deleted rung with its comment. After a second live change, the
  first `undo` block was refused with `StaleTarget` and nothing changed. The
  `undo` block worked after the server was restarted, because the handle lives in the response.
- **Live refused while a pending zone exists.** Recorded by the maintainers; not re-verified. After a
  `target="controller"` publish, a live edit was refused with
  `PendingZoneExists`. The agent named accept and cancel and did not act, and
  the zone was unchanged.
- **Live refused without the switch.** Recorded by the maintainers; not re-verified. Without
  `LGX_ENABLE_LIVE_FINALIZE`, a live edit returned `CapabilityDisabled`;
  nothing changed.
- **`target="controller"` only on request.** Recorded by the maintainers; not re-verified. Asked to "try it, then
  decide", the agent used `target="live"` and offered the `undo` block. Asked
  for a pending edit to review in Studio, it used `target="controller"` and
  the result carried the `zoneNote`. The maintainers reported the rung as
  pending (`I`).
- **Staging is not visible to a controller read.** Recorded by the maintainers; not re-verified. After staging
  rungs and tags on the workstation, a fresh process saw the controller
  unchanged: same rungs and `state: none`. In-session `logix_pending_edits`
  reports `state: none` and the staged count.
- **Publish pending census.** Recorded by the maintainers; not re-verified. A `target="controller"` publish
  reached `status: proposed` with exactly one pending `I` rung, in a fresh
  process read-back; a re-sent fragment was de-duplicated
  (`droppedDuplicates: 1`).
- **`LGX_PUBLISH_MODE`.** Recorded by the maintainers; not re-verified. `logix_ping` showed `publishMode` as
  `confirm` by default and `auto` when set. In `confirm` a controller publish
  with `confirmedByUser: true` proceeded; in `auto` it proceeded without it.
- **Created tags.** Recorded by the maintainers; not re-verified. A tag created by a publish was reported in
  `tagsCreated`, and a `logix_write` to it in the same session was refused
  (`CreatedByThisSession`). A tag created online with External Access Read
  Only was refused by `logix_write` (`ExternalAccessNotReadWrite`), and no attempt was made to change External Access.

### The Studio lifecycle (opt-in, experimental)

With `LGX_ENABLE_STUDIO_AUTOMATION=1` and `LGX_ENABLE_LIVE_WRITES=1`, against
the Studio of the bench:

- **Accept.** Recorded by the maintainers; not re-verified. After a controller publish, `logix_accept_pending_edits`
  returned `status: accepted` with before `pending` and after `none`. The
  maintainers reported Studio moving from Edits Present to no edits.
- **Test, read, untest, cancel.** Recorded by the maintainers; not re-verified. The maintainers reported
  the rung behaving as tested only while the zone was tested, and cancel leaving two rungs and nothing pending.
- **Test, assemble.** Recorded by the maintainers; not re-verified. The rung ran after assemble, and nothing was pending.
- **Refusals before any Studio command.** Recorded by the maintainers; not re-verified. Assemble on an untested zone
  returned `NotTested`; cancel on a tested zone returned `ZoneTested`; test
  without the switch returned `CapabilityDisabled`; with only the Studio
  switch set (live writes off) all four tools returned `CapabilityDisabled`;
  with Studio offline, accept returned `StudioOffline` and left the zone unchanged.
- **Posted menu commands only.** Recorded by the maintainers; not re-verified. Every driver step reported
  `gestureTier: posted`, `cursorMoved: false` and `foregroundChanged: false`.
- **Cancel.** Recorded by the maintainers; not re-verified. `logix_cancel_pending_edits` returned `status: cancelled`
  and the maintainers reported Studio moving from Edits Present to no edits.

### A client run through Claude Desktop (Cowork)

- **Claude Desktop (Cowork), Echo, Logix Designer v38 online, 2026-10-12.** Recorded by the maintainers from
  the Cowork transcripts and the server audit log; not re-verified. The Microsoft Store build of Claude Desktop,
  running the server through Cowork, connected with `backend="sdk"` and `sdkMode="online"`, and:
  - summarised the project and read a routine;
  - staged a rung edit with `verifyOnStage=true` (phase timings reported, including the `replay-copy/<step>`
    entries), published it as a pending edit, and accepted it through Logix Designer's own dialog;
  - ran `logix_run_test` (5 of 5 cases passed);
  - replaced a rung that carried a comment; after the accept, `logix_get_routine` returned the new text and the
    original comment from the session image (`source: "session-image"`), and `logix_project_summary` listed the
    routine under `appliedEdits` with `appliedEditsSource: "session-image"`.
  - Also seen: Claude Desktop starts two server processes for one config entry. A status poll answered by the
    other process reports that the connect belongs to another instance; the client reconnected on the process
    it was using.

### Build check, session health and routes

- **Build check on v38, success path.** Recorded by the maintainers; not re-verified. A valid rung published with
  `target="controller"` reached `proposed` through the offline-copy build check.
- **Build check on v38, failure path: not observed.** An undefined tag is
  rejected by the SDK's importer first (`XMLSrv_E_IMPORT_ABORTED_NO_CHANGES`,
  no changes made), so the build never ran. The `BuildFailedEditPending` path
  has not been seen on a real controller; it is covered by unit tests only.
- **Logix Designer v36.** The SDK's build check is not supported before
  Logix Designer v37; it answers "Operation not supported on Logix Designer
  version 36.5". A datatype import run against a copy of a v36 `.ACD` project
  succeeded and reported its build as unsupported on 36.05. For a v36 publish
  the design is that the import and the state checks run and the build check
  reports `buildVerify: "unavailable"`; no v36 publish scenario is recorded
  here, and the edit-lifecycle scenarios above were run on v38, not v36.
- **Killing the SDK service under a live session.** Recorded by the maintainers; not re-verified. After the SDK
  server process was killed, the next read failed within about 3 seconds with
  a message naming `LdSdkService` and did not blame pending edits. Windows
  restarted the service within about 45 seconds without help, and
  `logix_connect` then succeeded. On the build that was tested, the
  failure carried `kind: "TagNotFound"`. That run has **not** been repeated
  against the service kinds documented in [errors](errors.md)
  (`SdkServiceDegraded`, `SdkServerFaulted`): pending bench verification. To
  run it, connect with a comm path, stop the SDK service's server process
  (`taskkill /F /IM LdSdkServer.exe`), call `logix_read`, and record the `kind`.
- **`logix_doctor`.** Recorded by the maintainers; not re-verified. On a healthy bench (`overall: ok`; the SDK, the
  SDK service, Echo, controller reachability and Studio checks ok); with Studio
  offline the `studio` check failed with a fix; with an unreachable
  `controllerIp` the `controllerReachable` check failed with a fix.
- **`logix_run_test` on Echo.** Recorded by the maintainers; not re-verified. A test of the demo interlock reported
  the real logic failures and restored every input it set. A live insert followed by `logix_run_test` and then
  `undo` ended with the original rungs and nothing pending.

### Not run on the bench (pending bench verification)

- **`SdkServerFaulted`.** No bench run has provoked `LgxSrv_E_FATAL_ERROR` or
  `LgxSrv_E_SERVER_FAULTED`. It is covered by unit tests only.
- **Controller download.** No run on this page performed one. It stays off by
  default; see the [safety model](safety-model.md#controller-download-is-off-by-default).
- **`TagRouteUnavailable`.** Covered by unit tests; not provoked on the bench.
- **Context tag declarations leave tag values unchanged.** Not run. To check it
  on a test controller, `logix_read` a non-zero member of an existing tag,
  publish an export-shaped fragment whose `<Tags Use="Context">` declares it,
  accept the edit, and read the member again; the value should be identical.

## Install

- **Cold-install gate on a clean machine.** `bootstrap/cold-install-gate.ps1` passed
  on a fresh Windows 11 machine with no Rockwell software, no .NET and no VC++
  runtime, against the single-file win-x64 build without the SDK: initialize,
  ping and open_project all completed.

## Known limitations

- Creating a new data type is not possible. Existing types only.
- `confirmedByUser` cannot be verified by the server, by design; see the
  [safety model](safety-model.md).
- The Rockwell SDK service can crash during an online connect (a race inside
  the SDK's upload; see [troubleshooting](troubleshooting.md#connect-shows-an-sdk-restart-phase-or-the-sdk-service-crashed)).
  lgx-mcp recovers and the connect succeeds, about 30 to 60 seconds later; other
  SDK sessions on the same machine must reconnect.
- Module connection status comes from a routed CIP identity probe only, and
  Echo produces no minor faults, so `logix_diagnose` covers major faults only.

## Not verified on any physical controller

**All verification above is on FactoryTalk Logix Echo. Nothing here has been
run on a physical Allen-Bradley controller.** In particular, CIP behavior,
fault decoding, module status, any live write, and every SDK-lane operation
(live finalize and undo, the pending-edit lifecycle, Studio automation, the
build check) have not been independently confirmed on a real controller. If your use depends on any of it matching
production hardware, re-verify against your own physical controller before
relying on it.
