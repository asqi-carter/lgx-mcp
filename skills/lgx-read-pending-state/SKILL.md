---
name: lgx-read-pending-state
description: Inspect what is pending, staged or assembled on a Logix controller and whether the logic is actually executing. Use before and after any rung publish, or when the user asks "what's pending" / "did my change take effect".
---

# Read pending-edit state honestly

1. `logix_pending_edits(program, routine)` (or program-wide / controller-wide). It exports
   the routine and classifies each rung by its L5X `Type`: `N` normal (assembled); `I`/`D`/`R`/`IR`
   pending (untested); `e`/`er`/`dN` tested; `rR`/`rI`/`rN` accepted. The routine's `state`
   is one of `none`, `pending`, `tested`, `accepted`. It never imports anything. Each routine
   reports `controllerRungs` (what the controller holds; `rungs` is the same number) and
   `stagedRungs` (rungs this session staged). The staged count lives only on the server:
   a staged rung is not on the controller, and the export shows it as a normal rung, so the
   server subtracts it from the controller count by matching rung text.
2. **Check `controllerMode` first.** Only interpret edit state on a controller in **Run**. A
   controller in Program mode or faulted applies online imports directly — there is no
   pending zone to see, and "no pending edits" there proves nothing.
3. `staged` lists what *this session* holds locally for its next publish (live or controller);
   it is not on the controller. With `LGX_ENABLE_LIVE_FINALIZE` unset, staged is as far as the
   default flow goes.
4. "Pending" means **not executing**. To show a change is live, read a tag the rung drives
   (`logix_read`) after the engineer accepts it — not before. The default rung flow
   (`lgx-propose-rung-change`: stage, then `target="live"`, verify, keep or `undo`) leaves
   nothing pending: after a live change `logix_pending_edits` reports `none` and the rung is
   already executing, so verify it with `logix_read`. A pending zone exists only when the
   engineer asked for a `target="controller"` pending edit.
5. A new, fresh `logix_connect` gives an independent view if you suspect your own session's
   image differs from the controller.
6. `logix_finalize_edits` cannot assemble an existing pending zone (the SDK has no call for
   it). To resolve one: with `LGX_ENABLE_STUDIO_AUTOMATION=1` and the engineer's go-ahead,
   `logix_accept_pending_edits` or `logix_cancel_pending_edits`; otherwise the engineer
   accepts or cancels in Logix Designer (Logic > Online Edits). Do not try other workarounds.
7. **Read `zoneState`, not `state`, to tell tested from untested.** The routine export marks a
   zone `I` whether or not it is tested, so `state` cannot tell. `logix_pending_edits` therefore
   also reads the **program** export's `TestEdits` attribute and reports it as
   `programTestedEdits` (`true` / `false` / `null` when the attribute is absent — never guess) and
   as `zoneState` per routine: `none` (nothing pending), `untested` (edits pending, `TestEdits=false`),
   `tested` (edits pending, `TestEdits=true`; the new rung is executing). `zoneState` is the
   evidence for tested/untested; `logix_read` on a tag the rung drives is a behaviour check on top.
   Test and untest return `ok` only when the after-read shows the expected `zoneState`
   (`TestStateNotConfirmed` / `UntestStateNotConfirmed` otherwise).
   **Cancel-on-tested trap:** Studio disables Cancel Accepted while a zone is tested, so
   `logix_cancel_pending_edits` on a `tested` zone is refused with `ZoneTested` (no Studio command is
   sent). Untest first (`logix_untest_pending_edits`, a separate confirmed call: the controller goes
   back to the original logic), then cancel. Other refusals before any Studio command:
   `NotTested` (assemble or untest on an `untested` zone), `AlreadyTested` (test on a `tested` zone).
8. To check what you staged, read `staged` (`rungCount`, `tagNames`) or a routine's `stagedRungs`. Typed `rungs`/`tags`
   input and raw `l5xFragment` both land there; the propose response's `envelope` says
   whether the server built the envelope (`server-built`) or you did (`caller`).
