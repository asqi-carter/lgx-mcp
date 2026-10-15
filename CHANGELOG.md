# Changelog

All notable changes to lgx-mcp. Format loosely follows
[Keep a Changelog](https://keepachangelog.com/).

## [Unreleased]

## [1.0.1] - 2026-10-15

### Added
- `logix_import_datatype` creates a type that does not exist yet (imported at `Controller/DataTypes`); the `NewDatatypeUnsupported` refusal is gone.

### Fixed
- `logix_connect_status` without an id now reports the most recent connect made by any server instance, with `finished`, `outcome`, `completedAt` and the real elapsed time, and returns `NoConnectInProgress` only when no instance has connected.
- An accept or cancel done in Logix Designer is now noticed by the next pending-edits check, which applies the edits, restores the image, or marks the image stale, and writes one `image_reconciled` audit line per outcome.
- A staged or pending rung replace is counted once by `logix_lint` and `logix_cross_reference`, and `logix_get_routine` after staging stays on the project file with one rung.
- The `logix_get_rung_diagram` hint for a rung not yet in the served view now names the pending-edits tool, then `logix_get_routine`.
- Every accept, cancel, test, untest and assemble outcome now writes one audit line: refusals as `tool_refused` with a `kind`, and failures with `error` and the driver reason.
- A failed `logix_connect` no longer leaves an upload's deleted working copy registered; an offline connect to a missing file returns `ProjectFileMissing` with a hint, and `logix_connect_status` carries `connectedBackend` and says which session `connected` describes when `status` is `failed`.
- A second accept, test, untest, assemble or cancel after a successful one no longer fails with `command_not_applicable`: Logix Designer's Errors pane, shown as an auto-hide flyout by the previous accept, blocked the confirmation dialog; the driver now toggles the pane (View > Errors, posted, no click) and retries once; the pane is then left docked under the ladder view, never hidden, the response says so in `errorsPaneLayout`, and its pin (Auto Hide) returns it to auto-hide, and reports `errorsPaneToggled`.
- `logix_get_routine`, `logix_lint` and `logix_cross_reference` right after a publish no longer see the replaced and the new rung as two plain rungs when an analysis call had refreshed the image after the stage: the session image now recognises Logix Designer's own staged-rung codes (`e`, `er`, `rN`, `dN`) when the publish splices the zone in.
- `logix_connect_status` without an id no longer reports a finished connect of a server instance that has exited, and during this instance's own connect it answers `connecting` from the first statement of `logix_connect`, before any work starts, and a poll that reaches the server in the same instant as the connect waits for it instead of saying none has run; an offline SDK connect with no project is a typed `NoProjectLoaded` error with the next steps.
- `logix_get_routine`, `logix_lint` and `logix_cross_reference` after staging a rung identical to the one it replaces no longer see the routine with that rung missing (or, after the no-change publish, with no rungs at all): Logix keeps such a rung normal, and the session image now does too.
- A publish whose every replacement rung is identical to the rung it displaces (`NO CHANGE`) no longer also says the caller's `insertPosition` was ignored and committed at a server-chosen position; nothing landed, so no position was ignored and `ignoredInsertPosition` is absent.
- After a publish whose every rung was identical to the rung it displaces (`NO CHANGE`), the next real publish and accept in the same session no longer leaves `logix_get_routine` stale: nothing landed, so the session image has nothing to follow and stays current.
- A rung-edit proposal refuses an `insertPosition` past the end of the routine (`InsertPositionOutOfRange`) or a `replaceCount` that reaches past it (`ReplaceCountOutOfRange`) before anything is staged or imported; previously the import clamped them silently and the responses echoed the requested numbers.
- A tool argument of the wrong type or range (for example a negative `insertPosition`) is reported as `InvalidArguments` naming the argument, instead of `InternalError` with a JSON conversion message.
- `logix_import_datatype` now imports: the rendered L5X carried `ContainsContext="false"`, which the Logix Designer SDK imports silently as nothing, so every real run saved an unchanged copy while the response read as success. A run whose re-export does not match the requested members is now the typed error `ImportNotApplied` (nothing written); `collision: Cancel` on an existing type is `ImportCancelledOnCollision`; the SDK's import messages are returned as `sdkMessages`.
- The session image now follows the controller by re-exporting the program after every publish, accept, assemble, cancel, finalize, clear_staged and create_routine, instead of replaying the publish; reads after an accept are right for every replace shape (edit pairs, delete + add, interleaved pairs, several routines), `logix_get_routine` and friends refresh before answering, a new routine is visible at once, and `logix_diff_against_controller` compares the accepted logic and lists pending zones separately.
- `logix_connect` with an unroutable `commPath` under the SDK backend now returns the typed error `CommPathInvalid` (also in the `logix_connect_status` mirror), and a `path` given with `backend: sdk` adds a `warnings` entry saying `path` is a CIP backplane path the SDK backend does not use.
- Every reader that needs a project (`logix_get_routine`, `logix_lint`, `logix_cross_reference`, `logix_project_summary`, `logix_find_tags`, `logix_list_datatypes`, `logix_trace_origin`, `logix_get_rung_diagram`, `logix_verify_interlock`, `logix_gate_report`, `logix_list_modules` and `logix_document`) now answers `NoProjectLoaded` with a `hint`: "call logix_open_project first", or, after `logix_close_project` or a failed `logix_connect` closed the SDK session, that it was closed at a given UTC time and to call `logix_connect` again.
- A routine or program that does not exist is now the typed error `RoutineNotFound` or `ProgramNotFound`, with the routines (or programs) that do exist listed in the message, in `logix_get_routine`, `logix_get_rung_diagram`, `logix_trace_origin`, `logix_document`, `logix_cross_reference`, and `logix_gate_report`; the rung export also types a malformed `xPath` or a bad rung range as `InvalidArguments`, keeping the raw SDK text in `sdkError`.
- `logix_connect_status` without an id now answers `closed` (with the reason and time) after `logix_close_project` instead of the stale `connected`, until the next `logix_connect`; a `connected` or `failed` status also carries `phaseTimingsSummary`, the same summary object `logix_connect` returns, beside the `phaseTimings` array.
- Accepting and cancelling pending edits now return `dialogText` and `dialogWarnings` when Logix Designer showed a dialog, as the other lifecycle tools already did.
- Rung diagrams, `logix_cross_reference`, `logix_verify_interlock` and `logix_trace_origin` now say what state a rung is in: `staged (not sent to the controller)` for a workstation-staged rung, `pending` for an unassembled controller edit, and `tested` (executing) when the program's edits are under test, instead of calling all three "pending"; `logix_trace_origin` also honours `includePending` and lists the writers it left out under `pendingWriters`.
- A rung-edit proposal now checks each `tags[]` declaration: a tag that already exists with the same data type is left out of the import and listed in `reusedTags`; one with a different type is refused as `TagTypeConflict`, and an unknown `dataType` is refused at stage as `UnknownDataType`, with nothing staged or published.
- `logix_import_datatype` now imports and verifies types with BOOL members: a BOOL member is written in Logix's own `BIT` form (the importer no longer logs `XMLSrv_W_DISALLOWED_ATTRIBUTE`), the check compares only visible members so the hidden host is ignored (an applied import is no longer reported `ImportNotApplied`), and no `.bak` file is left beside `saveAs`.
- A routine created this session is readable at once: `logix_get_routine` finds it (0 rungs, `source: "session-image"`, `createdThisSession: true`) and `logix_project_summary` counts and lists it (`source: "session-image"`, the routine marked `createdThisSession`), instead of `RoutineNotFound` until an edit in it was accepted. A routine the project file holds unchanged still reads from the project file.
- The landed header of a rung-edit publish no longer contradicts itself: a shape that prints the replacement header never also reports `ignoredInsertPosition` or the "insertPosition ignored" note, a replacement at position 0 gets the replacement wording, and the header is plural and names the landed range when more than one rung landed.
- A workstation stage whose import log carries the rung-text scanner's syntax error (for example `XIC(A)[OTE(B);`, an unclosed branch) is now the typed error `RungSyntaxError` with the log's line, column and offending text; nothing is staged. `X;;` is normalised to `X;` instead of failing later as `StagedEditLost`.
- A rung with a common instruction (`XIC`, `XIO`, `OTE`, `MOV`, `TON`, `ADD` and the rest of the common set) given the wrong number of operands, such as `XIC(A,B)`, is refused at stage as `OperandCountMismatch`; an instruction that is neither known nor an Add-On Instruction in the project (`FOO(x)`) is reported at stage as an `UnknownInstruction` warning.
- Comment characters outside the Basic Multilingual Plane (for example an emoji) now round-trip through a rung comment instead of being written as an escaped entity.
- The `insertPosition` and `replaceCount` arguments of a rung-edit proposal are typed arguments: a string value such as `"1"` or a `null` is refused as `InvalidArguments` naming the argument, instead of a numeric string being accepted.
- `logix_read` and `logix_poll` of a bad tag path are typed: a whole array is `StructureReadUnsupported` with the element form (`Arr[0]`) in the hint, an index past the dimension is `IndexOutOfRange` (tag, index, dimension), and a member the type does not have is `MemberNotFound` listing the members. `logix_write` has the same `IndexOutOfRange` and `MemberNotFound`, and a write of a whole structure, user-defined type, STRING or TIMER is `StructureWriteUnsupported` listing the members, no longer the read-named `StructureReadUnsupported`.
- `logix_poll` with `count` below 1 (previously it ran one cycle) or an empty `tags` list is now `InvalidArguments` naming the parameter; nothing is polled.
- `logix_find_tags` entries for an array tag now carry `dimensions` (for example `[5]` or `[2, 3]`); the field is omitted for a scalar tag.
- The session image now notices a change made outside this session (another session finalizing a rung, a new routine, a re-download): `logix_get_routine`, `logix_lint`, `logix_cross_reference`, `logix_document` and `logix_diff_against_controller` check the controller before they answer, refresh the affected program, and say so in `imageRefreshedBecause`; a different controller or a re-download is the typed error `ControllerChanged` until `logix_connect` runs again.
- The pending-edit tools that accept, assemble or test a zone refuse a pending zone this server did not publish (`ForeignPendingZone`, with `publishedByThisServer: false`) unless you pass `acceptForeign: true`, which reports `foreign: true`; the cancel tool is never refused on that ground and says `foreign: true` for such a zone.
- Exporting rungs with a reversed or past-the-end rung range is now `InvalidArguments` naming `from`, `to` and the routine's rung count, checked before the range is exported, and the malformed-`xPath` hint describes the routine XPath form only; the `CommPathInvalid` hint no longer tells a caller who passed `sdkCommPath` to use it, and the warning for a `path` given with `backend: sdk` names `sdkCommPath`; a connect that finishes inside the sync budget carries `phaseTimingsSummary` like the status poll; with `includePending: true`, `logix_cross_reference`, `logix_verify_interlock` and `logix_trace_origin` mark rungs that are staged, pending or tested with `state`, and the `logix_trace_origin` note says "also writes" for a single writer.
- `logix_connect` removes `ctl-*` upload slot folders whose owner process is no longer running (a server that was killed leaves its ~4.5 MB slot behind) and reports the count as `staleUploadSlotsRemoved`; a `logix_connect` made while a session is live says so in `replacedSession` (`connectId`, `connectedAt`) and in its message instead of returning a payload identical to a fresh connect.

### Changed
- The `command_not_applicable` hint, when the dialog is empty, now names its three known causes.

## [1.0.0] - 2026-10-13

Initial public release. All verification to date is against FactoryTalk Logix
Echo (emulator), not physical hardware; see
[`docs/verified-on.md`](docs/verified-on.md).

### Added
- Static `.L5X` analysis: `logix_open_project`, `logix_project_summary`
  (including `routineList`, name and type), `logix_find_tags`,
  `logix_get_routine`, `logix_get_rung_diagram` (ladder text and SVG drawings of rungs,
  with pending edits labelled), `logix_cross_reference`, `logix_trace_origin` (backward origin trace of a tag: writers,
  sources, gating conditions, external inputs and gaps, as text, JSON or Mermaid),
  `logix_diff_projects`, `logix_list_datatypes`, `logix_close_project`.
- Derived write-gate engine: `logix_gate_report`, with `Cmd_*`/`Sim_*` grants,
  `ProgramWritten`/`SafetyTag`/`NotDesignated`/`UnknownWriteContext`/
  `CreatedByThisSession` denies, per-tag provenance, and UDT/tag-description
  bounds checking. It reports `policySource`, `sourcePath`,
  `enforcedBySession`, and `externalAccess` on each entry with an
  `ExternalAccessNotReadWrite` deny count.
- Static interlock proof: `logix_verify_interlock`.
- Parameter discovery: `logix_list_options` lists every fixed-choice parameter with its legal
  values and a one-line meaning each; `parameter` returns a single entry.
- `logix_connect_status`: read-only status of a background SDK connect (`connecting`, `connected`, `failed`), with the last outcome kept after it finishes and `NoConnectInProgress` when none has run.
- `ConnectInProgress` error kind: while a background connect runs, session-requiring tools return it (with `phase`, `elapsedMs`) instead of `NoActiveSession`; a duplicate `logix_connect` with the same arguments returns the in-flight status and one with different arguments is refused.
- Cross-instance connect registry: a client can start two server processes for one config entry, and a
  `logix_connect` started in one could be polled from the other, which answered `NoConnectInProgress`
  and led the model to reconnect. Each connect now leaves an advisory per-user entry. When another live
  server instance owns the connect, `logix_connect_status` returns the new `ConnectOwnedByOtherInstance`
  kind (with `ownerPid`, `phase`, `elapsedMs` and `status`) and says to call `logix_connect` here only after
  the other has finished; a finished foreign connect reports its outcome. `NoConnectInProgress` is kept for
  when the registry has nothing either. `logix_connect` and `logix_connect_status` responses carry
  `serverInstance` (`pid`, `startedAt`) so a transcript shows which instance answered. A registry failure
  never fails a connect.
- SDK lane: the stage and publish responses list the replay-copy sub-phases as `replay-copy/<step>` entries
  after `replay-copy` in `phaseTimings`, so the time spent in the offline copy can be seen step by step. The `replay-copy` total and the other phase names are unchanged.
- SDK lane: `logix_project_summary` adds `appliedEditsSource: "session-image"` whenever it lists
  `appliedEdits`, which come from the session image; everything else it reports still has
  `source: "project-file"`.
- Live tag access: `logix_connect` (`cip`/`virtual`/`l5x` backends),
  `logix_read`, `logix_write` (gate-enforced, with write readback),
  `logix_poll`, `logix_controller_mode`.
- Offline datatype import: `logix_import_datatype` (confirm-gated, dry-run by
  default). `saveAs` must end in `.L5X` and stay out of the install
  and Windows directories (`InvalidSavePath`); `l5xPath` must be an L5X
  document of bounded size; `datatypeName` is validated and escaped; calls are
  audited. A brand-new data type is refused up front (`NewDatatypeUnsupported`;
  the dry run reports `willCreate` and a warning) and any `saveAs` copy is
  removed after a failed import. Creating new types is unsupported; use Studio 5000.
- `LGX_ENABLE_LIVE_WRITES` gate covering live controller writes and
  controller-mode requests.
- `LGX_WRITE_ALLOWLIST`: an optional site-controlled file naming the tags (or
  `Prefix*` patterns) the server may write. With a full export it only narrows the
  gate; a tag the gate denies stays denied even if listed. With a partial export
  it is the only source of grants, and the partial file's denies still apply. An unreadable or invalid file
  fails the connect with `AllowlistInvalid` and the offending line. Shown in
  `logix_gate_report`, `logix_ping` and `logix_doctor`.
- Partial-export rule. A routine or program export is not a full controller
  export, so no write gate can be derived from it. `logix_open_project` reports
  `partialExport: true` and an `exportTarget` (for example `Routine`). The rule
  is the same on the `cip`, `virtual` and `l5x` backends: the connect succeeds
  with `gated: false`, reads work, and `logix_write` is refused with
  `WriteGateRequired`; `logix_gate_report` is `hypothetical`, never `enforced`.
  With `LGX_WRITE_ALLOWLIST` set, grants come only from the allowlist and every
  deny and range the partial file proves still applies (`Safety_`,
  program-written, `UnknownWriteContext`, External Access, firmware types); the
  connect result says `gated: true` with
  `gateSource: "allowlist+partial-export-denies"`. Documented in the safety
  model, tool reference, error reference and SECURITY.md.
- `logix_doctor`: a read-only setup and health check. Reports the
  emulator service, capability flags, the write allowlist, and any controller
  fault, with a named fix for each failure. A `cipRuntime` check
  is Ok when the CIP library's native component loads and Fail, with the Visual
  C++ runtime fix, when it does not.
- `logix_lint`: static checks for logic bugs and fault risks, ranked by severity,
  each finding citing its rung. Rules: duplicate destructive outputs (a later
  `OTE` defeating an earlier interlock), missing rung comments, unchecked array
  indexes, possible divide by zero, missing JSR targets, shadowed controller
  tags, case-only name collisions, never-used AOIs, negative timer presets,
  JSR parameter-count mismatches, recursive JSR, backward jumps, and timers or
  counters driven by more than one instruction. An optional `namingPattern`
  regex adds naming findings. Unused tags are never findings.
- `logix_document`: project documentation as Markdown, built only from the
  project's own comments, descriptions, routines, modules and data types (no
  generated prose). Output is capped and falls back to an outline with a hint
  to narrow the scope.
- `logix_diagnose`: a read-only report of why a controller stopped. Decodes the
  major fault (type, code, meaning, timestamp), cites likely suspect rungs and
  tags (for example "index 5, size 2"), and says how the fault can be cleared.
  Nothing is written.
- `logix_list_modules`: the I/O module tree from the project (catalog number,
  revision, parent, ports, inhibited, description). With `probe: true` on a
  live session it also performs one read-only, routed CIP Identity read per
  module and reports `responding`, `not_responding`, `error` or `unknown`, with
  the module's identity and an `identityMismatch` flag when the configured
  catalog number or revision disagrees.
- `logix_diff_against_controller`: compare an L5X file with a controller image
  held by the session. This build's backends hold no controller image, so it
  returns `NoActiveSession` or `NotOnline`; use `logix_diff_projects` to compare
  two files.
- `logix_ping`, `logix_list_skills` and `logix_get_skill`: server identity and
  capability switches, and the bundled agent-playbook skills.
- `lgx-instruction-reference` skill: ladder instruction semantics, common
  mistakes and the faults they cause (bit, timer, counter, compare, math, move,
  program-control instructions).
- `<untrusted>` provenance wrapping on all project-authored text (rung
  comments, tag descriptions, rung neutral-text) entering model context.
- Local-only audit trail (`audit.jsonl`) with rotation (`LGX_AUDIT_MAX_MB`,
  default 50, 0 disables; `LGX_AUDIT_KEEP`, default 5; a failed rotation never
  loses an event) and per-call traffic instrumentation (`traffic.jsonl`),
  byte-size-only for call content. `logix_ping` reports both rotation values.
- Installer with a cold-install gate that verifies a clean-machine start before
  declaring success (`bootstrap/setup.ps1`, which warns when `vcruntime140.dll`
  is missing).

- Audit log: `connect` records gain `gateway`, `path`, `gated`, `gate_source`
  (project path with `gate_source_sha256`, `allowlist` or `none`); `write`
  records gain `controller`; `ts` is ISO 8601 UTC with milliseconds. A failed
  append writes `audit write failed: <reason>` to stderr and `logix_doctor`
  reports `auditWriteFailures`. `LGX_AUDIT_FAIL_CLOSED=1` (default off) checks
  before a `logix_write` that the audit log can be opened for append and refuses
  it with `kind: "AuditUnavailable"` if not (a pre-write check, not a
  transaction). SECURITY.md notes the log is not tamper-evident.
- All environment variables are listed in `docs/tool-reference.md`.
- Error kinds `TargetExists`, `SdkRequired`, `InputTooLarge`, `InvalidL5x` and
  `AuditUnavailable` in `docs/errors.md`, with troubleshooting entries for
  `TargetExists` and `AuditUnavailable`.
- `logix_diagnose` returns `status`, the ListIdentity status word decoded with
  Rockwell's published bit table (device status, faults, keyswitch, mode flag,
  with a `source` citation), and `forces`, which always reports
  `reported: false` because force status is not readable over CIP, with a
  `howToCheck` for finding out in Studio 5000 or via GSV `ForceStatus`.

### Changed
- SDK lane: after an edit this session accepted or assembled, `logix_get_routine` and the rung diagram
  serve the changed routine from the session image (`source: "session-image"`), and every project-file
  response (`source: "project-file"`) carries `imageStale`, `staleSince` and `staleReason` with a reconnect
  hint when the image is stale. Edits that are still pending are never read.
- SDK lane: the session image keeps each landed rung's comment on accept or assemble: a comment preserved by a
  replace (`commentsPreserved`) or given with the new rung is carried into the image, so `logix_get_routine`
  returns it. Cancel still restores the image as it was before the publish.
- **External Access is the write grant.** The write gate requires a tag's
  External Access to be `Read/Write` (a missing attribute means `Read/Write`),
  in addition to the `Cmd_*`/`Sim_*` rules, on every backend. Only an engineer,
  in Logix Designer, makes a tag writable.
- `logix_write` on a live CIP session connected with no project open needs a
  site allowlist: with no project and no `LGX_WRITE_ALLOWLIST` it is refused
  with `WriteGateRequired` and nothing is written. Reads are unaffected, and so
  are virtual, offline L5X and gated (project-open) sessions. With an
  allowlist and no project, listed tags write and other tags are denied as
  `NotOnAllowlist`. The connect note for an ungated CIP session says so up
  front.
- CIP reads and writes use the tag's real data type, taken from the open project
  (including UDT members and array elements) or from the controller tag list,
  and never a type guessed from the value's size. Writes check integrality and
  range (`1.5` to a `DINT` is `TypeMismatch`). A read or write whose type cannot
  be resolved returns `TypeUnknown`; reading a whole structure or STRING returns
  `StructureReadUnsupported` with the member paths. Unsigned integer types are
  supported.
- CIP connect probes the controller and fails fast with `CipPathInvalid` (empty
  path, with a slot hint) or `ConnectionFailed`; library parameter errors map to
  the `ProtocolError` kind instead of `TypeMismatch`.
- Honest confirmation and gate wording: `confirmedByUser` is documented as an
  agent obligation the server cannot verify; the environment switches are the
  server-enforced controls. The
  `ConfirmationRequired` refusal no longer tells the model to retry with the
  flag. Capability gates fail closed when capability state is unknown.
- `logix_controller_mode` on a live controller returns `ModeChangeNotApplied`
  instead of reporting a mode that was never set, and is audited.
- `logix_read` and `logix_poll` wrap STRING values as untrusted content, like
  tag descriptions and rung comments. Numeric and BOOL values are unchanged.
- The no-session refusal carries a machine-readable `kind` (`NoActiveSession`).
- Single version source (assembly metadata) and one server name, `lgx-mcp`,
  across ping, server info and the client-registration script.
- The ModelContextProtocol library is upgraded to 2.2.0. No tool name,
  parameter or result shape changed.
- Docs: a mode change on a live controller is requested but never applied
  (`ModeChangeNotApplied`). `SECURITY.md`, `README.md` and the docs tree describe
  the tools and error kinds in this build only.
- Public documentation set (README and docs tree) drafted for open-source
  release. The Visual C++ 2015-2022 x64 redistributable is listed as a
  prerequisite for the CIP backend in the install guides; `errors.md` documents
  `CipRuntimeMissing` and the connect timeout wording on `ConnectionFailed`;
  `troubleshooting.md` covers the connect timeout and the "Dll was not found"
  symptom. The safety model no longer claims an allowlist has no effect without
  a project.
- `logix_doctor` in the no-SDK build reports a missing SDK or Studio as `info` ("not used by this
  build"), so a clean install reports `ok`.

### Fixed
- SDK lane: the SDK session is now closed when `logix_connect` reconnects and when the server exits.
- Setup: the Claude Desktop-running check in `bootstrap/setup-mcp-client.ps1` no longer counts `cowork-svc.exe`,
  the always-on Cowork helper installed with the Microsoft Store build of Claude Desktop. `-WriteConfig` no
  longer refuses with "Claude Desktop is running" when Desktop is closed. Only `Claude.exe` counts, and a Claude
  process whose path cannot be read still does.
- SDK lane: a `target="controller"` publish of a staged change reports the number of rungs actually replaced as
  its replace count, in line with its message and `publishedRoutines`; it used to report `0` for a staged
  replace.
- SDK lane: a replace publish is recorded for the session image. The new rungs land at the reported insert
  position with the replaced originals after them, which the record assumed the other way round, so an accept
  of a replace left the image stale with a false reason. After an accept, lint, document and reads now show the
  new rungs.
- `logix_connect backend="cip"` no longer hangs when the controller does not
  answer (nothing listening, or an unroutable address). The connect probe is
  bounded by a hard 15-second deadline and returns `ConnectionFailed` naming the
  gateway, the path and the elapsed time; the server keeps answering.
- A missing Visual C++ runtime is now named. Where the CIP library's native
  component cannot load, `logix_connect backend="cip"` returns the new
  `CipRuntimeMissing` error (install the Microsoft Visual C++ 2015-2022
  Redistributable (x64)) instead of a misleading "Dll was not found" connection
  failure with advice about the controller.
- The major-fault record is decoded at its real offsets, so `logix_diagnose` and
  `logix_doctor` report the correct fault type and code instead of garbage.
- `logix_diff_against_controller` ignores export-format noise (built-in data
  types, attributes only one export writes, comment and whitespace layout) and
  reports only real logic and declaration differences.
- The CIP backend returns `TypeMismatch` (with the exact-type hint) for `1` or
  `0` written to a BOOL, and maps known libplctag
  statuses such as `ErrorOutOfBounds` to a named kind instead of `Unknown`.
- `logix_gate_report` no longer lists a tag under `grants` when the write
  allowlist would deny it; it appears under `denies` with `NotOnAllowlist`.
- CIP writes to a REAL or to a UDT member no longer write the wrong bits, and
  the write read-back can no longer "verify" a value of the wrong type.
- The audit log's default directory is no longer affected by a traffic-log
  directory override on the same thread.
- `bootstrap/cold-install-gate.ps1` passes on a machine without
  Studio 5000, stamping the probe project with a fixed revision (38.11).
- **Security hardening.**
  - `logix_import_datatype` no longer overwrites an existing `saveAs`: it is
    refused with `TargetExists` unless `overwrite=true`. The result is built in a
    temp file and moved into place, and a failed import removes only that temp
    file. An `.ACD` `saveAs` is refused with `SdkRequired`.
  - The embedded libplctag native library is extracted into a fresh private
    directory and its SHA-256 checked before loading.
  - Untrusted L5X/XML is parsed through one helper that refuses DTDs and input
    over 64 MB (`InvalidL5x`, `InputTooLarge`), including in `logix_open_project`,
    `logix_diff_projects` and `logix_diff_against_controller`.
  - `logix_poll` enforces limits: at most 64 tags, `count` ≤ 1000, `intervalMs`
    ≥ 100; violations return `InvalidValue`. These were previously clamped.
- The audit record for a refused NaN/±Infinity write was silently dropped
  because the JSON serializer rejects non-finite numbers; it is now written.
- `logix_write` refuses NaN and ±Infinity (`NaN`, `-NaN`, `nan`, `Infinity`,
  `-Infinity`, `∞`, or numeric values) with `kind: "InvalidValue"` in every tag
  service (gated, CIP REAL and LREAL, virtual, L5X), whether or not the tag
  has bounds. NaN previously passed the range check because every compare with
  NaN is false. Each refusal is audit-logged as a denied gate decision.
- `logix_write` refuses bit and element writes (`Cmd_Speed.31`, `Cmd_Motor.Speed.31`,
  `Cmd_Motor.Speeds[2]`) under a range-checked tag or UDT member with `WriteDenied`
  ("write the whole value"); these previously skipped the range check. Member bounds
  now match on the normalized, case-insensitive member path and cover nested members
  (`Cmd_Motor.Sub.Speed`). Unbounded BOOL members and bits of unbounded granted
  tags are unchanged.
- The gate resolves aliases to their base tag. An alias is denied when its base is denied
  for any reason (the denial states it is an alias of the base and gives the base's reason),
  takes its bounds from the base, and reports the stricter External Access of the two. An alias
  of module I/O (`Local:1:O.Data`) or a missing tag is denied as `UnknownWriteContext`, as is a
  bit or element alias of a range-checked value. A rung writing an alias makes the base
  program-written.
- The gate scans Structured Text routines for `name :=` assignment targets and treats them as
  program-written (provenance `Program/Routine (ST line N)`). A `Cmd_` or `Sim_` tag named anywhere
  in an FBD or SFC routine, which are not parsed, is denied as `UnknownWriteContext`. Previously
  only ladder routines were scanned, so a tag driven from ST, FBD or SFC could be granted.
- The write policy is keyed by scope. A controller tag is addressed by its bare name and a
  program tag as `Program:<P>.<Tag>`; a program-local tag of the same name no longer replaces or
  widens the controller tag's grant, deny or bounds. `logix_gate_report` entries for program tags
  carry a `program` field.
- Write gate: a MESSAGE tag's tag-valued attributes are classified per attribute by `MessageType`.
  `DestinationTag` is program-written for every `MessageType` (real exports carry it on CIP Generic
  messages, where the reply lands in it and there is no `LocalElement`); a `Cmd_`/`Sim_` reply
  destination is denied. `LocalElement` is a destination (program-written) for read types
  (`CIP Data Table Read`, `PLC5 Typed Read`, `SLC Typed Read`, `PLC2 Unprotected Read`,
  `Block-Transfer Read`, any type containing "Read") and a source (no mark) for write types and
  `CIP Generic`. An unknown or missing `MessageType` makes every tag-valued attribute
  (`DestinationTag`, `LocalElement`, any name ending in `Tag` or `Element`) `UnknownWriteContext`.
  `CIP Generic` is a known type.
- Write gate: alias status propagates under both the bare and the program-scoped key
  (`Program:<P>.<Name>`), resolving the base in the alias's scope first and then the controller
  scope. An alias whose base cannot be resolved passes its status to the literal `AliasFor` target
  and is itself `UnknownWriteContext`. Unreadable-content marking also covers a parameter without a
  command prefix that aliases a command-prefixed tag.
- Write gate: a value that cannot be converted to a number for a range-checked tag is refused with
  `InvalidValue` instead of being passed on to the backend.
- Write gate: program parameters are read from the real export shape, a program tag with
  `Usage="Input"`, `"Output"`, `"InOut"` or `"Public"`. `Output` and `InOut` are program-written;
  `Input` and `Public` are program-written when a connection or another program's `\Program.Tag`
  reference writes them. An unrecognised `Usage` is `UnknownWriteContext`.
- Write gate: structured-text targets with whitespace, comments or line breaks beside `.`, `[` and
  `]` (`Cmd_Arr [i] := 1;`) are resolved to the real tag. A target that does not parse as a tag path
  marks every identifier on the left-hand side `UnknownWriteContext` instead of dropping the
  statement.
- Write gate: unreadable content (an encoded routine, an unparseable rung) marks every
  command-prefixed tag that has a `Usage` attribute, in every program, `UnknownWriteContext`, not
  just the tags of its own program.
- Write gate: an encoded or unknown add-on instruction is never classified as a pure function or
  `*_TO_*` conversion; `Cmd_`/`Sim_` tags of firmware-written types (`PHASE`, `AXIS_*`,
  `MOTION_GROUP`, `SFC_STEP`, `SFC_ACTION`, `MESSAGE`, `COORDINATE_SYSTEM`) are not granted;
  equipment-phase programs are scanned like any other program.
- Tag names outside the Logix tag-path grammar (identifier segments joined by `.`, with `[n]` or
  `[n,m]` indices and `:` for `Program:` and module tags) are refused with `InvalidTagName` before
  any backend call, for reads and writes. The check (`TagNameValidator`, in front of libplctag, used
  on every read, write and poll) uses `\z` (not `$`) and `[0-9]` (not `\d`) with `CultureInvariant`
  and accepts ASCII only, so a trailing newline or non-ASCII digits are refused.
- `logix_connect` refuses a `gateway` or `path` containing `&`, `=`, control characters or
  disallowed whitespace before any libplctag call.

### Known limitations
- The "No active session" error carries the machine-readable `kind` `NoActiveSession`.
- `confirmedByUser` is an agent obligation the server cannot verify (by design;
  see the safety model).
