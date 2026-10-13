---
name: lgx-instruction-reference
description: Ladder (RLL) instruction facts that are easy to get wrong - bit, timer, counter, compare, math/move, program-control and array behaviour, with the common mistake and the fault each can cause (as type/code, e.g. 4/20). Use when the user asks "what does <instruction> do", asks you to explain a rung, or before writing new ladder logic. Ladder only; no ST, FBD, SFC, motion or safety instructions.
---

# Ladder instruction reference (facts agents get wrong)

Short, per-group facts for RLL. Each section gives **semantics**, **operands**, a **common mistake**
and the **fault** it can cause. Every fault is written as `type/code` (for example `4/20`), the
form `logix_diagnose` and `logix_doctor` report. For other codes see Rockwell publication 1756-PM014
(Major, Minor and I/O Faults), type by type. Instruction semantics: Rockwell publication 1756-RM003
(Logix 5000 General Instructions); read it for anything not covered here.

Out of scope: Structured Text, Function Block, SFC, motion and safety instructions.
Use this before writing a rung and when choosing timer and counter test cases.

Names: this skill uses the Logix Designer v36+ (IEC) instruction names, and gives the v35-and-earlier
name in brackets where a name changed, for example `LT` (v35 and earlier: `LES`). Rung text you send
must use the names of the target controller's revision; see "Versions" at the end.

## Bit instructions: XIC, XIO, OTE, OTL, OTU, ONS, OSR, OSF

Reference: Logix 5000 General Instructions (1756-RM003), Bit Instructions.

- **Semantics.** `XIC(tag)` is true when the bit is 1. `XIO(tag)` is true when the bit is 0.
  `OTE(tag)` writes the rung condition to the bit every scan: 1 when true, 0 when false.
  `OTL(tag)` sets the bit to 1 when true and does nothing when false. `OTU(tag)` clears it to 0 when
  true and does nothing when false. `ONS(storage)` passes one scan on each false-to-true change of its
  rung condition. `OSR(storage, output)` and `OSF(storage, output)` set their output bit for one scan
  on a rising or falling edge of the rung condition.
- **Operands.** XIC, XIO, OTE, OTL, OTU take a BOOL tag or a bit of a wider tag. ONS, OSR and OSF
  take a separate storage bit that holds the previous rung state.
- **Common mistake 1.** Writing the same OTE target in two rungs and expecting "either condition".
  The last OTE in scan order wins: it overwrites the earlier rung every scan. Combine the conditions
  in parallel branches on one rung instead.
- **Common mistake 2.** Treating OTL and OTU as duplicates of OTE. OTL and OTU are latch and unlatch:
  the bit keeps its value after the rung goes false until the paired OTU runs. A latched bit with no
  OTU rung stays on until the program clears it.
- **Common mistake 3.** Sharing one storage bit between two ONS/OSR/OSF instructions. Each needs its own.
- **Fault.** None. The major faults in this skill (4/16, 4/20, 4/21, 4/31, 4/34, 4/42) never come from
  bit instructions; a wrong pattern shows up as wrong behaviour, not a controller stop.

## Timers: TON, TOF, RTO and RES

Reference: Logix 5000 General Instructions (1756-RM003), Timer and Counter Instructions.

- **Semantics.** A TIMER tag has `.PRE` (preset, ms), `.ACC` (accumulated, ms), `.EN` (enabled),
  `.TT` (timing) and `.DN` (done). `TON` times while its rung is true: `.EN` follows the rung, `.TT` is 1
  while `.ACC` < `.PRE`, and `.DN` goes to 1 when `.ACC` reaches `.PRE`. When the rung goes false,
  `.ACC`, `.DN`, `.TT` and `.EN` reset to 0. `TOF` is the off-delay: `.DN` is 1 while the rung is true,
  and timing starts when the rung goes false. `RTO` is a retentive TON: `.ACC` is kept when the rung
  goes false, and only `RES` clears it.
- **Operands.** `TON(timer, timebase, preset, accum)` with the timer tag. Set `.PRE` to a
  non-negative DINT; do not write `.ACC` unless you mean to.
- **RES on a timer.** `RES(timer)` resets `.ACC`, `.DN`, `.TT` and `.EN` to 0. It is how an RTO is cleared.
  On a TON it is rarely needed, because the TON already resets when its rung goes false. It does not
  "reset the done bit" alone, and it does not change `.PRE`.
- **Common mistake.** Reading `.DN` as "the timer is running" (that is `.TT`), or putting RES on the
  same rung as an RTO's input so the rung clears the timer as it times.
- **Fault.** A negative `.PRE` or `.ACC` raises major fault 4/34 (timer .PRE or .ACC negative).
  `logix_diagnose` lists timers with a negative `.PRE` as the suspects for 4/34.

## Counters: CTU, CTD and RES

Reference: Logix 5000 General Instructions (1756-RM003), Timer and Counter Instructions.

- **Semantics.** A COUNTER tag has `.PRE`, `.ACC`, `.CU`, `.CD`, `.DN`, `.OV` and `.UN`. `CTU` adds 1 to
  `.ACC` on each false-to-true change of its rung; `CTD` subtracts 1. `.DN` is 1 when `.ACC` >= `.PRE`
  for a CTU. `.OV` is set when the count passes the DINT maximum, `.UN` when it passes the minimum.
- **Operands.** `CTU(counter, preset, accum)`, the same counter tag for a CTU/CTD pair.
- **RES on a counter.** `RES(counter)` sets `.ACC` to 0 and clears `.DN`, `.OV`, `.UN`, `.CU`, `.CD`.
- **Common mistake.** Expecting CTU to count while its rung is true (it counts edges, one per scan
  transition) or forgetting that the count stays until a RES clears it.
- **Fault.** None from counting itself (CTU/CTD overflow sets `.OV`/`.UN`, not a major fault such as 4/34 or 4/20).

## Compare: EQ, NE, LT, LE, GT, GE, LIMIT

Reference: Logix 5000 General Instructions (1756-RM003), Compare Instructions.

- **Semantics.** `EQ` (v35 and earlier: `EQU`) equal, `NE` (`NEQ`) not equal, `LT` (`LES`) less than,
  `LE` (`LEQ`) less than or equal, `GT` (`GRT`) greater than, `GE` (`GEQ`) greater than or equal.
  Each is a rung input: true or false, it writes nothing.
  `LIMIT(low, test, high)` (v35 and earlier: `LIM`) is true when `test` is between `low` and `high` inclusive; if `low` > `high`
  it is true when `test` is outside the range (<= high or >= low) instead.
- **Operands.** Two tags or immediate values (`LIMIT` takes three). Mixed integer and REAL operands are
  converted; do not use `EQ` (v35 and earlier: `EQU`) on REALs, as rounding makes it rarely true. Use a
  `LIMIT` (`LIM`) band or a `LE`/`GE` (`LEQ`/`GEQ`) pair.
- **Common mistake.** Using `GT` (v35 and earlier: `GRT`) where `GE` (`GEQ`) is meant at a boundary, an
  off-by-one that only shows at the limit value. Another: using a compare to guard an array subscript
  with `LE` (`LEQ`) against the array size (a valid index is 0 to size-1, so use `LT` (`LES`) size).
- **Fault.** None from the compare itself; used as an array guard it prevents major fault 4/20.

## Math and move: ADD, SUB, MUL, DIV, MOD, CPT, MOVE, COP, CPS, CLR

Reference: Logix 5000 General Instructions (1756-RM003), Compute/Math Instructions and Move/Logical Instructions.

- **Semantics.** `ADD`, `SUB`, `MUL`, `DIV` compute `dest = A op B` when the rung is true. `MOD` gives the
  remainder. `CPT(dest, expression)` evaluates a whole expression (function names inside it are renamed too; see "Versions"). `MOVE(source, dest)` (v35 and earlier: `MOV`) copies one value.
  `CLR(dest)` sets the destination to 0. `COP(source, dest, length)` and `CPS(source, dest, length)` copy
  a block; the length is in elements of the destination type, not bytes and not source elements.
  `CPS` copies synchronously (the data cannot change mid-copy by a task interrupt); `COP` does not.
- **Operands.** Source and destination types should match; the destination size decides the result
  (an integer destination truncates a REAL). Check the destination is large enough for the result.
- **Divide by zero.** `DIV` by 0 sets the math status bit `S:V` and a minor fault; it is not a major fault like 4/34 or 4/20, so the controller keeps running (this skill gives no type/code for the minor record; see 1756-PM014).
  The destination is not reliably usable, so test the divisor with `NE(divisor, 0)` (v35 and earlier: `NEQ`) in front of the DIV.
- **Common mistake.** Giving a COP length in source elements or bytes, so it copies too much or too
  little; and moving a deprecated operand form with `MOVE` (v35 and earlier: `MOV`), which the import audit refuses
  (`RxCMP_E_AUDIT_DEPRECATED_INSTRUCTION`): write the current instruction form.
- **Fault.** None for ADD/SUB/MUL/MOD/MOVE/CLR in range (overflow sets `S:V` only, no major fault such as 4/20).
  A COP/CPS or MOVE whose array operand is indexed past the end is the array case: major fault 4/20.

## Program control: JSR, RET, SBR, JMP, LBL, AFI, NOP

Reference: Logix 5000 General Instructions (1756-RM003), Program Control Instructions.

- **Semantics.** `JSR(routine, inputs...)` calls a routine and runs it to its end or to a `RET`.
  `SBR` receives the JSR input parameters at the top of the called routine, and `RET` returns values
  and ends the routine early. `JMP(label)` jumps to the `LBL(label)` in the same routine, skipping the
  rungs in between (outputs there keep their last values). `AFI` is always false: it disables the
  rung it is on. `NOP` does nothing.
- **Operands.** JSR parameter count and types must match the SBR/RET lists. A JMP label is a name
  in the same routine; a label may be used by several JMPs but defined once.
- **Common mistake.** Adding or removing a JSR input without updating the SBR, or jumping to a label
  that was renamed or deleted. Using `AFI` as a "temporary comment" and forgetting it is there.
  Expecting the skipped rungs after a JMP to switch their OTEs off (they hold their old value).
- **Fault.** JSR parameter mismatch raises major fault 4/31. A JMP to a label that does not exist
  raises major fault 4/42. An unknown instruction is major fault 4/16.

## Arrays: subscripts and bounds

Reference: Logix 5000 General Instructions (1756-RM003), the operand and array notes
for each instruction; decoded faults 4/20 and 4/21 in 1756-PM014.

- **Semantics.** `Tag[i]` addresses element i of an array dimensioned 0 to size-1. A tag subscript is
  evaluated at run time, every scan.
- **Operands.** The subscript must be an integer in 0 to size-1 for each dimension. `Probe_Arr[Probe_Idx]`
  with `Probe_Idx` = 5 and a dimension of 2 is out of range.
- **Common mistake.** Writing an index from an unchecked source (an HMI, a counter, a computed offset)
  and assuming the array is "big enough". Also off-by-one: valid indexes stop at size-1.
- **Fault.** A subscript past the end gives major fault 4/20 (array subscript too large, or control
  structure .POS or .LEN invalid), which stops the controller.
  A control structure with a negative `.LEN` or `.POS` is major fault 4/21.
- **Guard first.** Put a `LIMIT(0, index, size-1)` (v35 and earlier: `LIM`) or `LT(index, size)`
  (v35 and earlier: `LES`), plus `GE(index, 0)` (v35 and earlier: `GEQ`), in the
  rung ahead of any instruction that uses the index, so an out-of-range value makes the rung false
  instead of raising major fault 4/20.

## Versions: v36+ names and what v37 and v38 dropped

Reference: Logix 5000 General Instructions (1756-RM003), Updated instruction mnemonics.

Logix Designer v36 renamed 16 ladder mnemonics to IEC 61131-3 names. There is no aliasing: a project
labelled v36 or later that uses the old spelling is not converted on import, it is rejected. Use the
set that matches the target controller's major revision (`logix_ping` and `logix_connect` report
`mnemonicSet` and `targetMajorRevision`). The server checks the rung text before it imports or stages
anything and refuses the other set (`WrongMnemonicSet`) with `suggestedText`: the same rung with only the
names replaced. Nothing is rewritten for you; resend the `suggestedText`. Structured Text is not affected.

| v36+ name | v35 and earlier | v36+ name | v35 and earlier |
|---|---|---|---|
| `EQ` | `EQU` | `MOVE` | `MOV` |
| `NE` | `NEQ` | `SQRT` | `SQR` |
| `LT` | `LES` | `TRUNC` | `TRN` |
| `LE` | `LEQ` | `EXPT` | `XPY` |
| `GT` | `GRT` | `ACOS` | `ACS` |
| `GE` | `GEQ` | `ASIN` | `ASN` |
| `LIMIT` | `LIM` | `ATAN` | `ATN` |
| `TO_BCD` | `TOD` | `BCD_TO` | `FRD` |

Seven of these are also function names inside `CMP`, `CPT`, `FAL` and `FSC` expressions, and are renamed
there too: `SQRT`, `TRUNC`, `ACOS`, `ASIN`, `ATAN`, `BCD_TO`, `TO_BCD`. Tag and Add-On Instruction names that
only contain a mnemonic (`MOV_Count`, `LES_Timer`) are not affected.

- **`=>` and `=<`.** Inside a `CMP`/`CPT`/`FAL`/`FSC` expression, the older spellings `=>` and `=<` are not
  accepted on v38 and later. Write `>=` and `<=`. The server refuses them on those revisions as
  `UnsupportedOnRevision` and suggests the replacement.
- **ASCII serial instructions.** `AWT`, `AWA`, `ARD`, `ARL`, `ABL`, `ACB`, `ACL` and `AHL` are not available
  on v37 and later. The server refuses them there as `UnsupportedOnRevision`, with no `suggestedText`
  because there is no one-for-one replacement.
- **Unknown revision.** When the target's revision is not known the check is skipped and the result carries a warning.
