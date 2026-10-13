---
name: lgx-test-logic
description: Write and run scripted logic tests (cases built from the rung text) against the Echo emulator with logix_run_test, and use them to decide whether to keep or undo a live change. Use when the user asks to test an interlock, check a rung's behaviour, or try a change before keeping it. Never for real hardware.
---

# Test logic on the emulator (write cases → run → read the verdict)

`logix_run_test` drives inputs, waits, reads outputs and gives a pass/fail verdict per step,
then **restores every tag it set**. It exists so a test is a repeatable script and not a hand-made
`logix_write` / `logix_read` sequence with no verdict and no restore.

## Never on real hardware

**This skill is for the Echo emulator only.** The server reads the controller's EtherNet/IP
ListIdentity first and refuses unless the product name starts with `Emulate`:
`EmulatorOnly` (names the product it saw) or `IdentityUnavailable` (lookup failed). Nothing is
written on a refusal. Do not look for a way round it: do not retry, change the address, or
suggest a flag. If the controller is real, say so and stop. Test inputs (including
`Safety_`-prefixed ones) are only safe to drive because the target is an emulator.

Also required: `LGX_ENABLE_LIVE_WRITES=1` (see `logix_ping` → capabilities) and
`confirmedByUser=true`. Ask the engineer before running; never set the flag on your own judgement
or to retry a refusal.

## 1. Write the cases from the rung text

Read the routine first (`logix_get_routine` / `logix_export_rungs`, `logix_gate_report`). For
**each output** (OTE/OTL/OTU target) the rungs drive, write:

- **Energise cases**: for every branch that should turn it on, set exactly the inputs that make
  that path true (and everything else false), then `expect` the output `equals` 1.
- **Interlock cases**: for every condition that should block it (permissive false, `E_Stop`,
  `Safety_` input, fault bit, opposing output), make the energise path true *and* the interlock
  active, then `expect` the output `equals` 0.
- **Drop-out**: after an energise step, a step that removes the input and expects the
  seal-in/latch behaviour the rung text implies.

For timer and counter cases (.DN/.TT/.ACC, RES, edge-counted CTU) see `lgx-instruction-reference`.

Test what the logic *should* do per the rung and the engineer's intent, not just what it does
now. A failing case is a finding: report it. Do not edit the case to make it pass.

Shape (up to 20 cases, 50 steps per case):

```
logix_run_test(confirmedByUser=true, cases=[{
  "name": "Motor runs when started and healthy",
  "setup": [{"tag": "E_Stop", "value": 1}],
  "steps": [
    {"set": [{"tag": "Cmd_MotorStart", "value": 1}], "waitMs": 200,
     "expect": [{"tag": "Motor_Run", "equals": 1}]}
  ]
}, {
  "name": "E-stop blocks the motor",
  "steps": [
    {"set": [{"tag": "Cmd_MotorStart", "value": 1}, {"tag": "E_Stop", "value": 0}],
     "expect": [{"tag": "Motor_Run", "equals": 0}]}
  ]
}])
```

`expect` takes `equals`, `min` or `max`. `waitMs` defaults to 200 and is clamped to 0..10000;
the controller settles within about one ~100 ms poll, so there is no point asserting finer timing.

## 2. What a test may write

A test may set any tag that logic does **not** write. Refused before anything is written
(`TestWriteRefused`): a tag written by program logic (`ProgramWritten`: test the output by
driving its inputs), a tag whose External Access is not Read/Write (including tags lgx-mcp
created, see `lgx-write-gate`), and a program-scoped tag. Each case lists in `ordinaryWriteDenied`
the tags it set that plain `logix_write` would deny: tell the engineer about those.

## 3. Read the result

- `overall`: `pass`, `fail` or `restore_failed`.
- Per case: `result`, steps with `set`, `observed` vs `expected` and a verdict, `durationMs`.
- Restore: every tag set is snapshotted before the case and written back afterwards, even when
  the case fails. `restored: [{tag, value, confirmed}]`. **`restore_failed` names the tags that
  did not come back: tell the engineer which tags and what value they hold now; do not
  carry on testing.**

Report which cases passed, which failed and why (rung, observed vs expected), and that inputs
were restored.

## 4. A change under trial (live edit → test → keep or undo)

1. Stage and go live with the engineer's go-ahead, exactly as in `lgx-propose-rung-change`
   (`target="live"`, `confirmedByUser=true`). Keep the `undo` block from the result.
2. Run the energise and interlock cases for the changed outputs, plus a few for outputs the change
   must **not** have disturbed.
3. Decide with the engineer:
   - **all pass**: keep the change; nothing more to do.
   - **any fail** (or `restore_failed`): show the failing steps and propose `undo`: pass the
     `undo` block back as one more live `logix_propose_rung_edit(target="live",
     confirmedByUser=true, <undo values>)`. Then re-run the tests to confirm the original
     behaviour is back. Tags the change created stay on the controller.

Never keep or undo on your own judgement.
