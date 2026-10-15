# Online edits: Studio's lifecycle, the SDK, and lgx-mcp

This is the reference for how a rung change reaches a running Logix controller. It covers:
- what Studio 5000 Logix Designer does at each step;
- what the Logix Designer SDK can and cannot do;
- which lgx-mcp tool covers each step.

Claims marked **Echo run** were observed on an emulated controller (FactoryTalk Logix Echo) and are not re-verified for this revision (pending verification on a physical controller); treat them as unconfirmed.

## 1. Studio's online-edit lifecycle

An engineer who edits a rung while online does not change the running logic straight away. The edit
moves through these states:

| Step | Studio action | What the controller runs | Reversible? |
|---|---|---|---|
| Edit | Start Pending Rung Edits (Ctrl+Shift+S), then type | the original rung | yes, local only |
| Verify | Studio checks instructions, operands and tags while you type, and on Verify Routine | the original | n/a |
| Accept | Accept Pending Rung / Program Edits | the original; the new rung is downloaded but not scanned | yes: Cancel Accepted |
| Test | Test Accepted Program Edits | **the new rung**; the original is kept | yes: Untest goes back to the original (Echo run: Test made the rung run, Untest stopped it; the export showed `I` both times) |
| Assemble | Assemble Accepted Program Edits | the new rung; the original is deleted | **no** |
| Finalize All | Finalize All Edits in Program (Ctrl+Shift+F) runs Accept, Test and Assemble in one step | the new rung | **no** |
| Cancel | Cancel Pending / Accepted Program Edits | the original | n/a |

**Download is not part of this cycle.** It replaces the whole project, which stops the controller
and reloads it. lgx-mcp never downloads unless `allowControllerDownload` is passed, and a test
setup reconnects Studio by **Upload** only.

**Rung markers in an L5X export** (`PendingEditsHandler` maps these to `logix_pending_edits.state`):

| `state` | `Type` values | Meaning |
|---|---|---|
| `none` | all `N` | nothing pending |
| `pending` | `I`, `D`, `R`, `IR` | edit zone, the shape a `target="controller"` publish leaves (Echo run: `I`); a zone after Test or Untest also exports `I`, so the **routine** export cannot tell tested from untested (see the table below) |
| `tested` | `e`, `er`, `dN` | testable or tested zone |
| `accepted` | `rR`, `rI`, `rN` | accepted into assembly |

**`?` operands in the export.** The controller export writes a timer's or counter's preset and
accumulator as `?`, because those values live in the structure tag (`.PRE`, `.ACC`) and not in the
rung: `TON(T,3000,0)` reads back as `TON(T,?,?)`. When lgx-mcp compares the rung text it expects with
the exported text, a `?` in the export matches any operand at that position only for the table
instructions (`TON`, `TOF`, `RTO`, `CTU`, `CTD`, operands 2 and 3); it is not a wildcard anywhere else,
so `XIC(A)` does not match `XIC(?)`. Because the wildcard hides the preset, the preset is not taken
from the rung: it is verified by reading `<tag>.PRE` from the tag after publish and comparing it to
the literal in the expected rung text.

**Tested or untested: read the program export.** The routine export is identical in both states, but
the **program** export carries a `TestEdits` attribute that flips (Echo run, Studio
v38.11, SDK 2.02.00):

| State | Program export `TestEdits` (`Controller/Programs/Program[@Name='…']`) | Routine export |
|---|---|---|
| no zone | `false` | all `N` |
| zone published (`AcceptEdits`), untested | `false` | `2[I]` |
| after Studio Test (32061); the rung runs | **`true`** | `2[I]` (identical) |
| after Untest (32062); the rung stops | `false` | `2[I]` (identical) |

The full `save_as` L5X carries the same attribute. `logix_pending_edits` reports it as
`programTestedEdits` and derives `zoneState` from it (section 5). An earlier statement that the
export cannot tell tested from untested was wrong.

Caveat: an accepted rung has been seen to export as plain `N`, so `none` means "none
visible in this export". The live and accept tools therefore also check the after-state against
the rung text they expect.

## 2. Where checking happens, and which errors mean what

There are two separate failure points.

1. **The SDK import itself.** `PartialImportRungsFromXmlFile` parses the L5X file and checks it
   **before** anything becomes a pending edit. A failure here leaves the controller and Studio
   untouched. It covers:
   - the envelope (root attributes, the `Use="Context"` markers);
   - rung text syntax;
   - unknown operands and tags.

   Errors come back as `XMLSrv_E_…` mnemonics with a line and column, for example:
   - `XMLSrv_E_INVALID_SCHEMA_REVISION`: the root `<RSLogix5000Content>` is missing `SchemaRevision`
     and its sibling attributes (Echo runs). This is a malformed **file**, not bad
     logic.
   - `XMLSrv_E_PIE_CONTROLLER_EDITS_EXIST_FOR_PROGRAM`: `FinalizeEdits` was requested while the
     program already has a zone.
2. **The controller.** It accepts the edit, then runs it once it is tested or assembled. Faults and
   behaviour show up here, which is why the live and accept tools read tags back and lgx-mcp's
   `logix_verify_interlock` / `logix_gate_report` exist. Those two tools check logic and tag
   safety. They do not check the import format.

**The envelope the SDK requires.** Callers do not need to write it: pass typed `rungs` / `tags` to
`logix_propose_rung_edit` and the server builds the envelope below (the response reports
`envelope: "server-built"`). A raw `l5xFragment` is the advanced path: with a `SchemaRevision` root it
is passed through unchanged (`envelope: "caller"`); without one, a fragment holding only rungs and tags
is rebuilt by the server; anything else is refused. Get a raw envelope by exporting, never by writing it by hand:

```xml
<RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="38.02" TargetName="MainRoutine"
    TargetType="Routine" TargetSubType="RLL" ContainsContext="true">
  <Controller Use="Context" Name="InterlockCtrl">
    <Tags Use="Context"> … new tags … </Tags>
    <Programs Use="Context"><Program Use="Context" Name="MainProgram">
      <Routines Use="Context"><Routine Use="Target" Name="MainRoutine" Type="RLL">
        <RLLContent> <Rung Number="n" Type="N"><Comment>…</Comment><Text><![CDATA[…;]]></Text></Rung> </RLLContent>
      </Routine></Routines></Program></Programs>
  </Controller>
</RSLogix5000Content>
```

**Pre-publish mnemonic check.** Logix Designer v36 renamed 16 ladder mnemonics to IEC names (`LES` became `LT`, `MOV` became `MOVE`, and so on) and does not alias the old spellings on import. Before any import or staging, on every target, `logix_propose_rung_edit` scans the rung text of `rungs` or of the fragment against the target's mnemonic set (`mnemonicSet` and `targetMajorRevision` in `logix_ping` / `logix_connect`, from the project's software revision). A name counts only in instruction position or as a function inside a `CMP`/`CPT`/`FAL`/`FSC` expression, so tags such as `MOV_Count` and Add-On Instructions are not flagged. Rung text from the other set is refused as `WrongMnemonicSet`; the ASCII serial instructions on v37 and later are refused as `UnsupportedOnRevision`. Each refusal lists, per rung, `rungIndex`, `found`, `expected`, `targetMajorRevision` and `suggestedText` (the same rung with only those names replaced; omitted when there is no mechanical replacement). `=>` / `=<` in expressions are accepted on every revision (per the 2026-10-08 SDK probe report, not independently reproduced here: they import on v38.02). Nothing is imported, staged or rewritten, so the text the caller resends is the text that lands and the comparator, the audit and the engineer's review all see the same text. When the target's set is unknown the check is skipped and the result carries a warning. The name table is in the `lgx-instruction-reference` skill, section "Versions".

**`verifyOnStage` (opt-in, `target="workstation"` only).** A workstation stage is held in the session image, which Logix does not check, so a normal stage reports `stagedIn: "session-image"`, `verified: false` and the fixed note. With `verifyOnStage=true` the server first imports the same fragment into an offline copy of the project and verifies it (the same import-log capture and `ImportLogParser` the failed online import uses), before the session image is touched:

- **Verify passes:** the rung is staged, `verified: true`, and `buildVerify` is `passed`.
- **Verify rejects:** the rung is **not** staged (buffered tag declarations from the call are rolled back too). The call returns `kind: "StageVerifyFailed"` with `verifyRejectReasons: [{mnemonic, hex, text, rungIndex, hint}]`. `hint` is set for mnemonics the server knows; for `RxCMP_E_DEPRCATED_INSTR_IN_EXPRESSION` it names the IEC function to use (for example `SQR -> SQRT`).
- **Replay unavailable** (copy cannot be opened and so on): the rung is staged as usual with `verified: false` and the reason in `verifiedNote`.
- `buildVerify` and `verified` always agree: a workstation stage that Logix did not verify reports `buildVerify: "passed-excluding-staged"`, never `"passed"`, next to `verified: false`.
- `target="controller"` ignores the flag and returns `verifiedNote: "ignored: controller publishes are always checked by Logix"`.

**Cost:** a project save-as, an offline open, an import and a build on every stage call: about **+12 to 18 s per stage** (a figure reported with this feature, not independently reproduced here; more on a large project; the replay is bounded at 60 s). Leave it off for routine staging and use it before a publish you cannot afford to have rejected.

**Refusals from the server-built path.** All are returned before any import is attempted:

| `kind` | Meaning |
|---|---|
| `AmbiguousInput` | `rungs`/`tags` were given together with `l5xFragment`; they are mutually exclusive. |
| `InvalidRungText` | A `rungs[i].text` is empty, or contains XML (`<![CDATA[`, `]]>` or `<Rung`). Pass rung text only. |
| `InvalidTag` | A `tags[i].name` does not match `^[A-Za-z_][A-Za-z0-9_]{0,39}$` (or contains `__`), or `dataType` is empty. |
| `EnvelopeIncomplete` | A raw fragment without `SchemaRevision` holds more than rungs and tags (DataTypes, AOIs, other routines), so the server cannot rebuild it. Use `rungs`/`tags`, or pass the export unchanged. |

`ExportAndStripRungsAsync` (in `ProposeRungEditHandler.cs`) produces exactly this from the
controller's own export. The server builds every fragment this way.

## 3. What the SDK can do

`PartialImportRungsFromXmlFile(xpath, start, replaceCount, file, option)`:

| `PartialImportOption` | Result | lgx-mcp |
|---|---|---|
| `LeaveEdits` | edit stays in the SDK's project image; nothing reaches the controller | `target="workstation"` |
| `AcceptEdits` | edit reaches the controller as a pending zone (`I`), visible in Studio, not running | `target="controller"` |
| `FinalizeEdits`, **no zone in the program** | **live in one call** (~2.6 s): all rungs `N`, it executes, Studio shows No Edits (Echo run) | `target="live"` |
| `FinalizeEdits`, zone exists | refused: `…EDITS_EXIST_FOR_PROGRAM` (Echo run) | refused before the call (`PendingZoneExists`) |
| `FinalizeEdits`, **empty** `RLLContent`, `replaceCount > 0`, no zone | **live rung delete** (~1.4 s); later rungs renumber (Echo run) | `target="live"`, `rungs: []` + `replaceCount`, `expectedRungText` per deleted rung; refused on `controller`/`workstation` (`DeleteNeedsLive`) |
| `FinalizeEdits`, replace what was published with what was there before | **undo** of any live change (Echo run) | the `undo` block on every `target="live"` result, passed back as one more live call |

Echo run, SDK only (SDK 2.02.00). Every case is one
`PartialImportRungsFromXmlFile(xpath, start, replace, file, FinalizeEdits)` call on a clean program and ended
with all rungs `N`.

| Case | Call | Result |
|---|---|---|
| revert a modify | replace 1 with the original text | 1.5 s; behaviour reverted |
| **delete an inserted rung** | `replace=1` with an **empty** `RLLContent` | 1.4 s; the rung is gone |
| delete 2 rungs | `replace=2`, empty | 1.3 s |
| delete a rung in the middle | `start=1, replace=1`, empty | 1.4 s; later rungs renumber |
| revert a 1→2 replace | `start=1, replace=2` with the original 1 rung | 1.4 s |
| delete an original rung, then restore it | empty `replace=1`, then insert the original | 1.3 s + 1.4 s |

Undo replaces only rungs. Tags that the change created stay in the controller; no SDK call deletes a tag online.

**Created tags are External Access Read Only.** Every tag a publish creates is declared
`ExternalAccess="Read Only"`; a new tag in a caller `l5xFragment` is forced to Read Only and listed in
`externalAccessForced`. The tags are readable, and not writable through lgx-mcp or any CIP client until an
engineer sets External Access to Read/Write in Logix Designer. The rungs that use them still run. lgx-mcp never
changes an existing tag's External Access: a fragment whose declaration differs from the controller's is refused
before any import with `ExternalAccessChangeRefused`, on every target. The expected basis (pending verification on a physical controller) is that SDK writes bypass External
Access, while CIP writes to Read Only get `Permission denied`.

Other SDK facts:
- The SDK has **no** separate Test, Untest, Assemble, or "accept an existing zone" call. Those steps
  exist only as Studio commands (section 4).
- The **program** export (`partial_export_to_xml_file("Controller/Programs/Program[@Name='X']")`)
  carries `TestEdits="true|false"` on `<Program>`. It is `true` only while the zone is tested; the
  routine export does not carry it (Echo run).
- A replace fragment that has no `<Comment>` drops the existing comment, so every replace path
  (`target="workstation"`, `"controller"` and `"live"`) carries it over: the rung at
  `insertPosition + i` hands its comment to the i-th replacement rung, and the result lists those
  routine rung numbers as `commentsPreserved` (e.g. `[0]`). Precedence:
  - an explicit new comment (`comment: "text"` or a `<Comment>` in the fragment) always wins;
  - an **explicit empty comment (`comment: ""`) clears the comment**: the replacement rung is
    imported with no comment and it is not listed in `commentsPreserved`;
  - omitting `comment` keeps the existing comment.
  Cancel restores the original rung, which already holds its own comment, so the comment comes back
  with it (SDK behaviour; pending verification on a physical controller: stage a replace with
  `comment: ""`, cancel the edit, and read the rung back with `logix_export_rungs`).
- `partial_export_to_xml_file("Controller")` is refused; `Controller/Programs` works. An export to
  a path that already exists also fails.

### Session model: SDK for types and the gate, CIP for values

An online SDK session has two channels to the same controller, and each has one job:

| Concern | Channel |
|---|---|
| Types (atomic, UDT member, array element, program-scoped, add-on) | the SDK session |
| The write gate (External Access, logic-written, safety, allowlist) | the SDK session's project, through `GatedTagService` |
| Tag values (read, write, poll) | CIP when `LGX_TAG_ROUTE` selects it, otherwise the SDK |
| Rung and program edits, imports, pending-edit zones | the SDK session only |

`LGX_TAG_ROUTE` takes `auto` (default), `cip` or `sdk`:

- `auto`: CIP when a CIP connection to the same controller opens (the target is derived from `sdkCommPath` and the controller identity must match the SDK session's); otherwise the SDK.
- `cip`: CIP only. When CIP cannot connect the tag tools fail with `TagRouteUnavailable`; the SDK is not used.
- `sdk`: the SDK always.

The route is chosen at `logix_connect` and reported there (`tagRoute`, `tagRouteReason`). Every read, write and poll result carries `route` (`cip` or `sdk`), and the audit write record carries it too. If CIP is lost during a call, `auto` retries it once over the SDK and the result carries `route: "sdk"` and a `routeNote`; a write is never retried silently. `cip` returns the typed error instead.

`logix_doctor` check `session` shows the active route, the setting and why: for example `tag route cip (LGX_TAG_ROUTE=auto), because auto: CIP connected`, or `tag route sdk (LGX_TAG_ROUTE=auto), because auto: CIP connect failed: ...`. The doctor only reports; it never opens a route (before the first choice it says `tag route not chosen yet`).

### Types and the write gate on the CIP route

In an SDK online session `LGX_TAG_ROUTE` may send tag reads and writes over CIP, but the SDK session stays the source of types and the gate:

- **Types.** The CIP route resolves a tag's type (atomic, UDT member, array element, program-scoped, predefined and add-on members) through the SDK service's own type lookup (`ISessionTypeSource`), not a copy and not L5Sharp's project index. A tag that lookup does not know is refreshed once by a partial re-export from the SDK session before the read is called `TypeUnknown`.
- **After a publish.** When `logix_propose_rung_edit` creates tags, the session refreshes the lookup from a partial re-export of each created tag, so a read of a just-created tag is typed at once on either route.
- **Gate.** Every write, on either route, passes through the same `GatedTagService` policy object, which sits above the route.

## 4. Studio commands (v38.11)

The IDs come from Studio's own menu resources (`LoadMenu` on `rllENU.dll`, `LogixACENU.dll`,
`AFLibENU.dll`). They are sent as `PostMessage(main, WM_COMMAND, id, 0)` to the Studio window found
by PID, with no focus and no cursor. Posted `WM_COMMAND` plus verified dialog text is the
only mechanism: no keys, no clicks, no focus, and the driver never launches Studio or takes it online. If a command shows no dialog the result is `command_not_applicable`.

| Command | ID | Notes |
|---|---|---|
| Accept Pending Program Edits | 32059 | |
| Cancel Pending Program Edits | 32060 | |
| Test Accepted Program Edits | 32061 | `logix_test_pending_edits`; dialog "Test edits for program 'X'." (Echo run: enabled directly on a published zone, dialog in 0.22 s) |
| Untest Accepted Program Edits | 32062 | `logix_untest_pending_edits`; dialog "Untest edits for program 'X'." (Echo run) |
| Assemble Accepted Program Edits | 32063 | `logix_assemble_pending_edits`; **disabled** on a freshly published zone, enabled after Test (MFC drops the command silently otherwise; Echo run); dialog "Assemble edits for program 'X'." |
| Cancel Accepted Program Edits | 32064 | `logix_cancel_pending_edits`; dialog "Cancel edits for program 'X'." |
| Finalize All Edits in Program | 32077 | `logix_accept_pending_edits`; dialog "Finalize all edits in program 'X'." |
| Start Pending Rung Edits | 32155 | |
| Accept / Cancel Pending Rung Edits | 32156 / 32158 | rung-scoped |

Dialog traps (Echo run):
- The Finalize confirm dialog is custom MFC. Its Yes button has control ID **1** and it **ignores
  IDYES (6)**. The driver posts `WM_COMMAND(GetDlgCtrlID(yes), hYes)` and waits for the dialog to
  close; if it stays open the result is `confirmation_dialog_still_open`.
- The driver checks the dialog text (the verb phrase plus the program name) before pressing Yes. On
  a mismatch it presses No and returns `confirmation_dialog_mismatch`.

## 5. Tool map

| Lifecycle step | Tool | Gate |
|---|---|---|
| read state | `logix_pending_edits`, `logix_export_rungs`, `logix_get_routine` | none |
| create an empty RLL routine | `logix_create_routine` (SDK named-object import, `LeaveEdits` only) | `LGX_ENABLE_LIVE_WRITES` + `confirmedByUser` + write allowlist, if set |
| edit and check | `logix_propose_rung_edit target="workstation"` | `LGX_ENABLE_LIVE_WRITES` |
| accept (pending zone) | `logix_propose_rung_edit target="controller"` | + confirmation per `LGX_PUBLISH_MODE` |
| finalize, nothing pending | `logix_propose_rung_edit target="live"` | + `LGX_ENABLE_LIVE_FINALIZE`, always `confirmedByUser` |
| finalize an existing zone | `logix_accept_pending_edits` (Studio driver) | `LGX_ENABLE_STUDIO_AUTOMATION` + `confirm` |
| cancel an existing zone | `logix_cancel_pending_edits` (Studio driver) | `LGX_ENABLE_STUDIO_AUTOMATION` + `confirm` |
| test a change (emulator only) | `logix_run_test` (SDK lane; scripted cases, verdict per step, every tag it set is restored; `EmulatorOnly` unless ListIdentity reports an `Emulate…` product). Unlike test / untest below it does not touch Studio or the pending zone | `LGX_ENABLE_LIVE_WRITES` + `confirmedByUser` |
| test / untest | `logix_test_pending_edits` / `logix_untest_pending_edits` (Studio driver) | `LGX_ENABLE_STUDIO_AUTOMATION` + `confirm` |
| assemble a tested zone | `logix_assemble_pending_edits` (Studio driver; `NotTested` otherwise) | `LGX_ENABLE_STUDIO_AUTOMATION` + `confirm` |
| undo a live change: pass `undo` back (SDK, no GUI) | `logix_propose_rung_edit target="live"` with the `undo` block from the earlier live result | `LGX_ENABLE_LIVE_FINALIZE`, always `confirmedByUser`; `StaleTarget` if the rungs changed since |
| SDK finalize of an existing zone | `logix_finalize_edits` refuses and points at the tools above | |

**`target="controller"` only on request.** `target="live"` (with `LGX_ENABLE_LIVE_FINALIZE`) plus the
`undo` block is the way to try a change and back it out; it uses the SDK only and leaves no zone.
`target="controller"` is only for when the engineer asks for a pending edit to review and accept in
Studio. The zone it creates blocks every further import into that program until Studio resolves it
(accept or cancel). There is no override parameter. A successful `target="controller"` result carries a
`zoneNote` saying so.

**A new routine: create it first, then stage into it.** `logix_propose_rung_edit` stages into a routine
that exists (`RoutineNotFound` otherwise). `logix_create_routine(program, name, type="RLL",
description?, confirmedByUser=true)` makes an empty ladder routine with
`PartialImportWithTargetFromXmlFileAsync(<program>/Routines, name, file, LeaveEdits)`. The envelope is an
export of an existing RLL routine of the same program (smallest first), renamed, with an emptied
`<RLLContent/>` (a self-closed export is opened first) and under 30 kB; a program without an RLL routine gets
it from the program export. Only `LeaveEdits` is ever used, so the import never finalizes other edits. The
tool refuses with `PendingEditsBlockImport` (no import attempted) when the program holds edits, read the way
`logix_pending_edits` reads them, and re-exports the program afterwards (`CreateNotConfirmed` on a
mismatch). It returns `routineXPath`, `createdInController` (always `true`: an offline session is refused with
`OfflineCreateNotSupported` and nothing is imported or saved) and an `undo` note: the routine stays even if later edits are cancelled, the SDK has no routine
delete, and it is removed in Studio. Each call writes a `routine_created` audit event.

**A controller publish checks the program first.** `logix_propose_rung_edit target="controller"` reads the
program's edit state (the read `logix_pending_edits` and `logix_create_routine` use) before any export envelope
or import. If any routine holds edits it refuses with `PendingEditsBlockImport`, naming the routines and the
next step (assemble, finalize or cancel them first); no import call is made, so the raw
`XMLSrv_E_PIE_CONTROLLER_EDITS_EXIST_FOR_PROGRAM` never surfaces. `target="workstation"` staging is not affected.

Recipe for new logic with one zone:

1. `logix_create_routine` for the new routine (before any staging: an import into a program with a pending
   zone is refused).
2. `logix_propose_rung_edit target="workstation"` for the rungs of the new routine, and for a call to it
   (`JSR(NewRoutine,0);`) in the caller routine.
3. One `target="controller"` publish with no fragment. The staged routines of the program go in together,
   so the result is one zone, accepted or cancelled as a whole.

Cancelling that zone removes the rungs and the JSR but leaves the empty routine.

**A change set across several routines of one program.** Stage each routine's change with
`target="workstation"` (the staged rungs are kept per routine), then publish **once** with
`target="controller"` and **no fragment**. Do not publish routine by routine, and do not put one routine's
rungs in the fragment while others are staged. The **server chooses the commit position**: for a
no-fragment publish it commits at the end of the publish call's routine in the session image, with
`replaceCount` 0, and ignores the caller's `insertPosition` (reported as `ignoredInsertPosition`). The
caller does not need to compute a position. A change set across two programs is two zones and stays two
publishes.

**Echo run: why the position is not the caller's.** Isolation runs on an emulated controller (Echo L85E v38,
a 4-motor project; each run staged, published, read `logix_pending_edits` for both routines, then cancelled;
Echo reloaded between series). They were recorded from the original bug report, not re-run for this doc, and
show the behaviour **before** the server chose the position itself: a no-fragment publish imported at the
caller's `insertPosition`, and when that position held a staged rung the SDK dropped that rung.

| Staged | Publish call (`routineXPath`, `insertPosition`) | Result |
|---|---|---|
| MainRoutine replace 0..3 | MainRoutine, 0 | **rung at 0 lost**: `0 D`, then 3 `IR`/`R` pairs; `PublishMismatch` |
| MainRoutine replace 1..3 | MainRoutine, 0 | correct: 3 `IR`/`R` pairs |
| MainRoutine replace 0..3 | MainRoutine, 6 | **rung at image index 6 lost** (the new rung at that index; its original becomes `D`) |
| MainRoutine replace 0..3 | MainRoutine, 10 (end of the session image) | **correct**: 4 `IR`/`R` pairs, no error |
| MainRoutine replace 0..3 | AlarmRoutine, 1 (end; nothing staged there) | correct zone in MainRoutine; census still errors (staged buffer keyed by position only, not routine) |
| MainRoutine 0..3 + AlarmRoutine 0 (11 rungs) | AlarmRoutine, 11 (end of its image) | **every rung correct** in both routines; census still errors `PublishMismatch` |

Reading the table: committing at the end of the image is always safe, which is why the server picks it.

**Studio not ready.** The Studio-driver tools never launch Studio or take it online. They return a typed
error with the before-state attached, and send no command:

| `kind` | When |
|---|---|
| `StudioNotRunning` | no Studio main window is found |
| `StudioOffline` | the Studio window title shows the project offline |

For both, ask the engineer to open the project in Studio and go online by Upload.

**`zoneState` and the lifecycle refusals.** `logix_pending_edits` reports, per routine, `zoneState`
(`none` | `untested` | `tested`) and `programTestedEdits` (`true` | `false` | `null` when the attribute
is absent; never guessed). `state` is unchanged. The Studio-driver tools check `zoneState` after
their before-read and refuse **before any Studio command is sent**:

| Tool | Refused when | `kind` |
|---|---|---|
| `logix_cancel_pending_edits` | `zoneState == "tested"` (Studio disables Cancel Accepted while tested; untest first, then cancel) | `ZoneTested` |
| `logix_assemble_pending_edits` | `zoneState == "untested"` | `NotTested` |
| `logix_test_pending_edits` | `zoneState == "tested"` | `AlreadyTested` |
| `logix_untest_pending_edits` | `zoneState == "untested"` | `NotTested` |

Every refusal includes `zoneState`, `programTestedEdits` and the pending rungs. Fallbacks and
after-state checks:
- `command_not_applicable` from the runner: `NotTested` for assemble, `CancelNotApplicable` for cancel
  (its message names untest-then-cancel).
- After the runner, test must show `zoneState == "tested"` and untest `"untested"`; otherwise
  `TestStateNotConfirmed` / `UntestStateNotConfirmed` with the before and after states.
- There is no automatic untest-then-cancel: untest changes what the controller runs, so it stays a
  separate confirmed call. `logix_accept_pending_edits` has no state precondition.

**Never (no tool, no SDK call):**
- no online tag delete (`Use="Delete"` and `Controller/Tags` targets are refused online; plain import is
  offline-only), so tags a live change created stay after an undo;
- no undo of a zone-based change through `undo`; use the Studio lifecycle tools (cancel, untest).

The playbooks are the `lgx-propose-rung-change` and `lgx-read-pending-state` skills
(`logix_get_skill`).

### Logix Designer v36

Found against an emulated controller on Logix Designer v36. The SDK's build check, `BuildAsync`, is not supported before Logix Designer v37 and answers `Operation not supported on Logix Designer version 36.5.`

On v36, a publish with `logix_propose_rung_edit`:

| Step | Runs on v36? |
|---|---|
| The import itself (`LeaveEdits`, `AcceptEdits`, `FinalizeEdits`) | yes |
| Post-import state check (session still online) | yes |
| SDK build check (offline copy: save-as, open offline, build, close; also `unavailable` with the reason when save-as fails with edits pending or the copy lacks the staged edit) | **no**: the result says `buildVerify: "unavailable"` and carries `designerVersion`. This is not a build failure, so nothing is rolled back. |
| Pending census (`target="controller"`) or live re-export check (`target="live"`) | yes |
| Studio's own verification when you accept or assemble | yes, and it still applies |

Rules that hold on every version:
- When the build check really fails after a `controller` or `live` import, the server does not go offline. Going offline cannot remove an accepted or finalized edit. It reads the routine's pending rungs back and returns `BuildFailedEditPending`, listing them. Cancel in Studio (Logic → Online Edits → Cancel Accepted Program Edits) or with `logix_cancel_pending_edits`.
- Only the workstation path (`LeaveEdits`) goes offline to discard the edit, and the "nothing left pending" wording is used only after that call succeeded.
- Every error returned after the import succeeded carries `pendingAfterError`: the pending rungs read back, or `unknown` with the read-back error. No text claims nothing is pending unless a read-back or a successful go-offline confirmed it.
- `logix_connect` reports `designerVersion` and `buildCheckAvailable` (true for 37 and later). The SDK has no version call: at connect the version comes from the project's software revision, and `buildCheckAvailable` is left out when that is below 37. The first unsupported-version error fixes the exact version for the session, and later publishes skip the build call. `logix_doctor` shows the version and where it came from. The `buildCheck` detail reads "Needs Logix Designer 37 or later; this is <v>." when the version is known, and "Needs Logix Designer 37 or later. The installed version is not known yet; the first logix_build reports it." when it is not.
- The rung-edit audit record carries `build_verify` (`passed`, `failed`, `unavailable` or `skipped`) and `designer_version`.

## 6. External background

External background on the Studio workflow:
- [Studio 5000 edit workflow: auto accept, test and finalize edits](https://industrialmonitordirect.com/blogs/knowledgebase/studio-5000-auto-accepttestassemble-edits-workflow)
- [Studio 5000 test edits vs PLC test mode](https://industrialmonitordirect.com/blogs/knowledgebase/studio-5000-test-edit-mode-how-outputs-actually-work)
- [Pending rung edit states (r / R / i)](https://industrialmonitordirect.com/blogs/knowledgebase/studio-5000-pending-rung-edit-states-r-r-i-assembly-fix)

### Context tag declarations write no tag data: pending verification

Staging now buffers and reports (`stagedTags`) only tag declarations whose names are absent from the pre-stage census, and the publish response lists existing context declarations under `contextTags`, never `tagsCreated`. Unit tests cover this (`StagingTagsTests`, `PublishTagsTests`).

Not yet verified on hardware: that importing `<Tags Use="Context">` declarations for existing tags leaves their values unchanged. **Pending verification** — this was not run here (no controller access). To verify, on a test controller:

1. `logix_read` a non-zero DINT member of an existing context tag (for example `ExampleTag.Count`) and record the value.
2. `logix_propose_rung_edit target="controller"` with an export-shaped fragment whose `<Tags Use="Context">` envelope declares that tag, then accept the edit.
3. `logix_read` the same member again and record it.

Record both values and the date here once done. Expected: the value is identical before and after.
