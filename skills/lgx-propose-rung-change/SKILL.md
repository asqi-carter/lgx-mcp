---
name: lgx-propose-rung-change
description: Change ladder (RLL) logic on a running Logix controller - stage the rungs, then (with the engineer's go-ahead and the live flag on) apply them live, verify, and keep or undo. Use when the user asks to add, change or fix rungs, interlocks or logic online.
---

# Change a rung (stage → live → verify → keep or undo)

The default lifecycle is: **stage locally → inspect → (go-ahead + live flag) `target="live"` →
verify → keep it or pass `undo` back.** It uses the SDK only: no pending-edit zone is
created, so nothing is left for Studio to resolve.
Staging alone never reaches the controller. `target="live"` publishes and finalizes in one SDK
call: **the rung executes immediately**. Never go live on your own judgement.

A pending edit that the engineer reviews and accepts in Studio is a different path: use it
**only when the engineer asks for a pending edit** (see that section below).

1. **Connect online through the comm path** — `logix_connect(backend="sdk",
   sdkMode="online", sdkCommPath="EmulateEthernet\\<ip>" | "AB_ETH-1\\<ip>\\Backplane\\0")`.
   The server uploads the controller's program into a private copy; it never touches the
   engineer's own project file. Do not pass `allowControllerDownload` — a download stops and
   reloads the controller.
2. **Read before you write.** `logix_get_routine` / `logix_export_rungs(xPath=<routine XPath>)`
   and `logix_gate_report`. Know which tags already exist and which are writable. Keep the
   exported text of any rung you will replace.
   Run `logix_lint(routine=<routine>)` on the routine before the change and keep the findings:
   it is the baseline for the "after" run in step 7. (It lints the project file; online, the
   server's private uploaded copy.)
3. **Describe the change as rungs/tags; the server builds the L5X.** Pass
   `rungs=[{"text": "XIC(Start)OTE(Lamp);", "comment": "optional"}]` (rung text only, never
   XML; a missing trailing `;` is added) and, for any NEW tag, `tags=[{"name": "Lamp",
   "dataType": "BOOL"}]` (optional `radix`, `description`, `dimensions`). The server wraps
   them in the import envelope for the target routine. Do not combine them with
   `l5xFragment` (`AmbiguousInput`). Refusals before any import: `InvalidRungText` (empty
   text, or CDATA / `<Rung` markup), `InvalidTag` (bad name or empty `dataType`).
   *Advanced (raw round trip):* to edit exported XML directly, take the `l5x` from
   `logix_export_rungs`, replace the contents of its `<RLLContent>` with your
   `<Rung Number="n" Type="N">` elements, add NEW tags inside its `<Tags Use="Context">`,
   and pass it as `l5xFragment`. Never invent the RSLogix5000Content root attributes: an
   envelope without `SchemaRevision` is rebuilt only if it holds nothing but rungs and
   tags, otherwise it is refused as `EnvelopeIncomplete`.
4. **Stage:** `logix_propose_rung_edit(routineXPath, insertPosition, replaceCount,
   rungs, tags, target="workstation")`. Repeat for more changes. Check
   `logix_pending_edits(...).staged` — `rungCount` and `tagNames` are exactly what a
   later publish will send. Staged rungs are counted per routine as `stagedRungs`, never in
   `controllerRungs` / `rungs`: the staged count lives only on the server, not on the
   controller. Show the engineer the staged set and the rung texts.
5. **Check `logix_ping` → `capabilities.liveFinalize`.**
   - **True** (`LGX_ENABLE_LIVE_FINALIZE=1`, plus `LGX_ENABLE_LIVE_WRITES=1`): continue to step 6
     once the engineer gives an explicit go-ahead for this exact staged change.
   - **False / `LGX_ENABLE_LIVE_FINALIZE` unset: live is unavailable. Stage only** and stop at
     step 4. Tell the engineer the change is staged locally, nothing reached the controller, and
     that it can go to the controller as a pending edit if they ask (see "Only when the
     engineer asks for a pending edit"). Do not publish to the controller unprompted.
6. **Go live:** `logix_propose_rung_edit(routineXPath, insertPosition, replaceCount, rungs,
   tags, target="live", expectedRungText=[<one exported text per replaced rung>],
   confirmedByUser=true)`. `confirmedByUser=true` records the engineer's go-ahead (the server
   cannot verify it) and is required even in `auto` publish mode; never set it on your own
   judgement or to retry a refusal. `expectedRungText` is required when `replaceCount > 0` (a changed rung is refused as `StaleTarget`).
7. **Verify:** `logix_pending_edits` must report `none`, and `logix_read` a tag the rung
   drives, while its input changes.
   Run `logix_lint` on the routine again, before and after a change: report any **new** errors
   (compare with the baseline from step 2) to the engineer. If the lint source is a copy that
   predates the change, say so: the lint covers the project file, not the live edit.
8. **Keep or undo, with the engineer.** Every successful live result carries an `undo` block
   `{routineXPath, insertPosition, replaceCount, rungs, expectedRungText}`.
   - **keep**: nothing more to do.
   - **undo**: pass the block back as one more live call:
     `logix_propose_rung_edit(target="live", confirmedByUser=true, <undo values>)`. It is
     guarded by the stale check (`StaleTarget` if the routine changed since) and returns its own
     `undo` block (a redo). The server stores nothing; the block exists only in the response,
     so keep it in the conversation.
9. **Tell the user** what executes now, and `tagsCreated`: tag creation is immediate and
   permanent. Undo reverts rungs only: **tags the change created stay on the controller** (the
   SDK cannot delete tags online). That is normal — tell the engineer.

### Reference pre-check (before staging or publishing)

Every stage and publish checks the tag operands of the rungs against what the session can see, and refuses
only what Logix certainly rejects (nothing is imported or staged):

- `UndefinedTag`: the name is in no visible scope. If the name exists only in **another program**, the hint says
  `use \<Program>.<Tag> if it is Public`.
- `TagNotPublic`: `\Prog.Tag` where the tag is not reachable from another program. Only `Usage="Public"`, `Input`
  and `Output` are; a missing Usage (real exports leave it out of ordinary local tags), `Local` and `InOut` are
  refused. A program or tag the export does not resolve is let through with a warning.
- `UndefinedMember`: `T1.NoMember` where the tag's type is known (a user data type, or TIMER, COUNTER, CONTROL,
  STRING) and has no such member. Tags of any other type (module-defined, AOI) are not member-checked.
- `CrossProgramOutputNotWritable`: `OTE`/`OTL`/`OTU` whose destination is another program's Output parameter, such as
  `OTE(\Line_A.P6_Out)` (Logix: `RxCMP_E_DIRECT_WRITE_OUTPUT`). The error names the rung index and operand. Reading
  the tag (`XIC`, `XIO`, input operands) is allowed.
- `MotionTagRequiresOffline`: a declared tag of type `AXIS_*`, `MOTION_GROUP` or `COORDINATE_SYSTEM`. Logix creates
  these only offline; the call is refused (stage, `verifyOnStage` and publish) before the controller is contacted.
- `ReadOnlyExternalAccessRejected`: Logix rejected a new tag's External Access (Read Only) for its data type; the
  error names the type. HMIBC, ALARM_DIGITAL and ALARM_ANALOG are already created Read/Write and listed in
  `externalAccessForced`.

**Left to Logix on purpose** (the pre-check neither refuses nor warns): a literal where a tag is expected, such as
`MOVE('A',dA)`, and a `JSR` to a routine in another program. Logix reports both when the rung is published.

## Before writing a rung: check the instruction facts

Before writing a rung (step 3), read the matching section of `lgx-instruction-reference`: OTE last-wins,
timer and counter bits, DIV by zero, array bounds, JSR/SBR parameters. The `logix_lint` messages name the
section that applies to each finding.

## After a kept change: offer documentation

Once the engineer has **kept** a change (step 8, "keep"), offer `logix_document` for the routine:
`logix_document(scope="routine", program=<program>, routine=<routine>)`. It returns deterministic
Markdown (numbered rungs with their comments, the tag table with External Access) that the
engineer can paste into a change record. Offer it; do not run it unasked. It documents the
project file (online: the server's private uploaded copy), which can predate a live change, so
say so if the change was applied live. Present the Markdown as returned, and never add rungs,
comments or tags to it that the tool did not return.

## Try it, then decide

After a live change, check it with scripted tests rather than hand-made writes: see
`lgx-test-logic` (`logix_run_test`, emulator only, every input restored). Write the energise and
interlock cases from the rung text, run them, then keep the change or pass the `undo` block back
(step 8). A failing case is a reason to propose `undo`, with the engineer. Never on real
hardware: the tool refuses anyway.

Tags a live or controller publish creates are External Access **Read Only** (readable, not
writable until an engineer sets Read/Write in Logix Designer). A rung that writes them still
runs; only `logix_write` and CIP clients are refused. Refusal `ExternalAccessChangeRefused`
means the fragment tried to change an existing tag's External Access — drop that attribute.

To delete rungs live, pass an explicit empty `rungs=[]` with `replaceCount > 0` and
`expectedRungText` (one entry per deleted rung); omitting `rungs` is not a delete. The result's
`undo` block restores the deleted rungs.

Refusals happen before any import: `CapabilityDisabled` (flag off), `ConfirmationRequired`,
`AmbiguousInput` / `InvalidRungText` / `InvalidTag` (typed input), `StagedRungsPresent` (publish staged rungs, or
discard them with `logix_clear_staged`, first), `PendingZoneExists` (resolve with `logix_accept_pending_edits` /
`logix_cancel_pending_edits`, or in Studio, first), `StaleTarget`. Never use `live` on your own
judgement or to retry a refusal.

## When the logic needs a new routine

`logix_propose_rung_edit` only stages into a routine that exists (`RoutineNotFound`; the hint points
here). Create it first with `logix_create_routine(program, name, type="RLL", description?,
confirmedByUser=true)`: ask the engineer first, as the routine cannot be deleted again through the SDK
(the `undo` note in the result says to remove it in Studio, and that it stays even if later edits are
cancelled). It needs `LGX_ENABLE_LIVE_WRITES`. It refuses with `PendingEditsBlockImport` when the program has
edits that are not yet assembled, so create the routine **before** staging or publishing anything in that
program. Only RLL is supported. An offline session is refused with `OfflineCreateNotSupported` (nothing imported or saved): connect online, or create the routine in Studio.

Recipe: (1) `logix_create_routine`; (2) stage the new routine's rungs and a `JSR(NewRoutine,0);` rung in the
caller with `target="workstation"`; (3) one publish (no fragment) for the whole set: one zone.

## Only when the engineer asks for a pending edit (publish → accept or cancel)

Use `target="controller"` **only when the engineer asks** for a pending edit to review and
accept in Studio 5000. It sends the staged rungs as *accepted-but-not-assembled* pending edits:
visible in Studio, **not executing** until accepted or cancelled. It creates a zone that blocks
every further import into that program until Studio (or the gated lifecycle tools) resolves it;
the result carries a `zoneNote` saying so. It is not the way to try a change: for that use the
live flow above and `undo`.

1. Stage first (steps 1–4 above).
   **Several routines of one program:** stage each routine with `target="workstation"` (staged
   rungs are kept per routine), then publish **once** with no fragment. Do not publish routine by
   routine. **The server chooses the commit position** (the end of the publish call's routine in the
   session image); the `insertPosition` you pass is ignored and reported as `ignoredInsertPosition`.
   Two programs are two zones and two publishes.
2. **Publish once — check `logix_ping` → `capabilities.publishMode` first.**
   - `confirm` (default): show the user the staged set (`logix_pending_edits(...).staged`
     and the rung texts) and **stop**. Publish only after the user explicitly approves
     this exact staged set, with `confirmedByUser: true`. The flag records that approval;
     the server cannot verify it. Never set it on your own judgement or to retry a refusal.
   - `auto`: publish once without asking.

   Either way: `logix_propose_rung_edit(..., target="controller")` with an **empty**
   `l5xFragment` sends the staged set; `insertPosition` and `replaceCount` are ignored
   (pass `0, 0`). Live writes must be enabled (`LGX_ENABLE_LIVE_WRITES=1`).
3. **Read the response and tell the user**: which rungs are pending, `tagsCreated` (tag
   creation is immediate and permanent — it is NOT part of the pending edit and a cancel will
   not remove it), and that the pending rungs must be accepted or cancelled.
4. **Hand off — the engineer decides.** By default stop here: the engineer accepts or
   cancels in Logix Designer (Logic > Online Edits). Only if
   `logix_ping` → `capabilities.studioAutomation` is true **and** the engineer gives an
   explicit go-ahead: `logix_accept_pending_edits(confirm=true)` (or
   `logix_cancel_pending_edits` to discard) → verify with `logix_pending_edits` and a
   read of a tag the rung drives. Accepting makes the rungs execute on a running
   controller; never do it on your own judgement.
   One publish per accept/cancel cycle: a second controller publish is refused
   (`PendingEditsPresent`) until the zone is resolved. Tags created by your
   publish are External Access **Read Only**: readable, and not writable through lgx-mcp or
   any CIP client — in this session or a later one (`ExternalAccessNotReadWrite`; this session
   also gets `CreatedByThisSession`) — by design. The **engineer grants writes in Studio**:
   Logix Designer → tag properties → External Access = Read/Write. Tell the user this; never
   try to change External Access yourself (`ExternalAccessChangeRefused`).

A tag your publish declared is not readable (`TagNotFound`) until the edit is accepted.

### Pending-edit path only: test, then assemble or untest (test / untest / assemble)

**Default: accept or cancel and stop** (everything above). The Test/Untest path exists only inside
this pending-edit path, when `logix_ping` → `capabilities.studioAutomation` is true **and** the
engineer gives an explicit go-ahead to run this exact change on the controller. **Test makes the
new rung execute** on the running controller while the original is kept; Untest puts the original
back. To try a change and back it out without a zone, use the live flow and `undo` instead.

1. Publish: `logix_propose_rung_edit(..., target="controller")` (the steps above) — the zone is
   now pending and not executing.
2. Test: `logix_test_pending_edits(program, routine, confirm=true)`. Show the engineer
   `dialogWarnings` verbatim (outputs in `D`/`R` zones hold their last state; SFC routines reset).
3. Verify: `logix_pending_edits` must show `zoneState: "tested"` (program `TestEdits=true`; the
   test result says "verified"). The rungs still export as `I`, so use `zoneState`, not `state`
   (see `lgx-read-pending-state`). Then `logix_read` a tag the rung drives, while its input changes.
4. Decide, with the engineer:
   - good: `logix_assemble_pending_edits(confirm=true)` — **irreversible**; the rungs become `N`.
     Assemble needs `zoneState: "tested"`: on an `untested` zone it is refused with `NotTested`
     before any Studio command, so check `zoneState` first rather than trying it.
   - bad: `logix_untest_pending_edits(confirm=true)` to restore the original logic, then
     `logix_cancel_pending_edits(confirm=true)` to discard the zone. Cancel on a `tested` zone is
     refused with `ZoneTested`, so untest always comes first.

   Confirm the end state with `logix_pending_edits` (`none` after assemble or cancel) and a read.
   Never test, assemble or untest on your own judgement.
