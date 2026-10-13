# Security & safety posture

lgx-mcp gives an LLM read access to a Logix project file and, opt-in,
write access to a live or emulated Allen-Bradley controller. This page is
the map of what that access can and cannot do, what gets recorded, and where
the trust boundaries sit. Deep configuration and lifecycle detail:
[`docs/safety-model.md`](docs/safety-model.md).

## Not a safety system

**lgx-mcp is not a safety system, and nothing in this repository may be used
to satisfy a safety function** (E-stops, guard interlocks, SIL-rated logic,
or any other safety-instrumented function). `logix_verify_interlock` is a
**static structural proof over rung neutral-text** — it can tell you a
guard *relationship exists in the source*, and it explicitly cannot observe
runtime execution, `JSR`/`RET` branching, or controller-mode effects. Do not
substitute it for a safety validation process, a functional safety
assessment, or the physical lockout/tagout procedures your site already
requires.

## What the LLM can and cannot do

| | |
|---|---|
| **Can, always** | Open and statically analyze a `.L5X` project; derive and report the write-gate policy with provenance; run the static interlock proof; read live and virtual tags. |
| **Can, opt-in (`LGX_ENABLE_LIVE_WRITES=1`)** | Write a **granted** tag on a live or emulated controller, through the project-derived gate or, with no project open, a site allowlist (`LGX_WRITE_ALLOWLIST`). With neither, `logix_write` is refused with `WriteGateRequired`. A routine or program export (a partial export) derives no gate: with it open, writes are refused with `WriteGateRequired` on every backend unless a site allowlist is set, in which case only listed tags can be written and every deny and range the partial file proves still applies. Request a controller mode change (with `confirm=true`) as a request only: on a live controller it is never applied and returns `ModeChangeNotApplied`. |
| **Cannot, ever** | Write a **denied** tag. The gate check happens before any backend is touched, on every write path that has a project-derived gate, or an allowlist plus the denies of a partial export (see "Partial exports" and "CIP with no project open" below). A partial export alone is never writable. Denials cover `Safety_` tags and tags written by program logic, including through aliases and in ladder, Structured Text, Function Block Diagram and Sequential Function Chart routines. The Structured Text scan fails closed: statements are joined up to their `;`, so a multi-line `:=` still counts, and so does the non-retentive `[:=]`; the destination or timer/counter operand of an instruction call (`COP`, `CPS`, `FLL`, `MOV`, `TONR`, `TOFR`, `RTOR`, `CTUD`, `RES`, …) and the destination of a string instruction (`CONCAT`, `DELETE`, `INSERT`, `MID`, `FIND`, `UPPER`, `LOWER`, `DTOS`, `STOD`, `RTOS`, `STOR`) is treated as program-written, as is every output/in-out argument (and the instance tag) of a call to a project Add-On Instruction; an argument of any function or call that is not an operand-table instruction, a project AOI or a documented value-returning function is `UnknownWriteContext` (denied); program parameters are read from the real export shape, a `<Tag Usage="…">` in the program's `<Tags>`: an `Output` or `InOut` parameter is program-written, an `Input` or `Public` parameter is program-written when it is an endpoint of a controller-level `<ParameterConnection>`, both endpoints of a connection that touches a declared parameter (or a program-scoped name that is not a declared parameter, whose direction cannot be proven) are program-written, and a parameter with any other `Usage` value is `UnknownWriteContext`; an unrecognised element or attribute in `<ParameterConnections>` makes every `Cmd_`/`Sim_` tag of every program `UnknownWriteContext`; cross-program references (`\Prog.Tag`, resolved to `Program:Prog.Tag`) are program-written; the tag-valued attributes of a MESSAGE tag's `<MessageParameters>` are classified one by one: `DestinationTag` (where a CIP Generic reply lands) is program-written for every `MessageType`; `LocalElement` is program-written when the `MessageType` is a read type (contains "Read"), is a source and gets no mark when it is a write type or `CIP Generic`, and is `UnknownWriteContext` when the `MessageType` is missing or is neither or both read and write, or when the element has an unrecognised destination-like attribute; when the `MessageType` is unknown, every tag-valued attribute (`DestinationTag`, `LocalElement`, and any attribute whose name ends in `Tag` or `Element`) is `UnknownWriteContext`, and on a known type an unrecognised attribute ending in `Tag` or `Element` is `UnknownWriteContext`; alias status is propagated between an alias and its base under both the bare and the program-scoped key (`Program:<P>.<Name>`), the base being resolved in the alias's scope first and then the controller scope, so a write through a program-scoped alias or parameter marks the base, and a write to the base marks the alias; an alias whose base cannot be resolved passes its status to the literal `AliasFor` target (`Tag`, `\Prog.Tag` or `Program:Prog.Tag`) and is itself `UnknownWriteContext`; and content the scanner cannot read fails closed: a routine that is encoded or protected (an `EncodedData` element at any depth and with any `EncodedType`, or no content element for its type — `STContent`, `RLLContent`, `FBDContent` or `SFCContent`) or a ladder rung whose text cannot be parsed makes every `Cmd_`/`Sim_` tag visible to its program, **and every `Cmd_`/`Sim_` program parameter of every program (including a parameter without a command prefix that aliases a command-prefixed tag)**, `UnknownWriteContext` (an `EncodedData` element outside any program does the same for every controller-scope `Cmd_`/`Sim_` tag), with provenance naming the element. A consumed tag (`TagType="Consumed"` or a `ConsumeInfo` element) is denied as `UnknownWriteContext`. In Structured Text, whitespace and comments inside an assignment target (`Cmd_Motor . Speed :=`, a target split across lines) are ignored when the target is resolved; a left-hand side that does not parse as a tag path makes every identifier on it `UnknownWriteContext` rather than being dropped. Write target names are trimmed and whitespace around `.`, `[` and `]` is removed before the policy and bounds are matched; a name that is empty, contains a control character, or is not a valid tag path once normalised is refused (`InvalidTagName`) before any backend is touched. Change a tag's External Access: a tag whose External Access is not `Read/Write` is denied. Write a single bit or array element of a range-checked value (`Cmd_Speed.31`, `Cmd_Motor.Speeds[2]`): such writes are refused because they would skip the range check; write the whole value instead. Write a value outside a tag's declared bounds. Write NaN or ±Infinity to any tag (`InvalidValue`). Apply a controller mode change on a live controller. |
| **Recorded, not prevented** | Every gate decision and write is appended to a local audit log. The log is not tamper-evident (see "Audit trail"). Each write's audit record is appended after the write is attempted. `LGX_AUDIT_FAIL_CLOSED` (`1`, `true` or `yes`) adds a pre-flight check that the log is writable and refuses the write if it is not; it does not make the record transactional. |

## Write guardrails

- **External Access is the write grant.** A tag's External Access attribute,
  set by an engineer in Logix Designer, decides whether it may be written. The
  controller enforces it for CIP; lgx-mcp honours it on every backend (CIP,
  virtual, offline file); lgx-mcp **never changes it**; and no tool raises it, so nothing becomes
  writable until an engineer says so.
- **The gate is derived from your project.** `Cmd_` and `Sim_` tags are
  designated writable; `Safety_` tags and tags written by program logic
  (`ProgramWritten`) are denied; anything else is denied unless designated.
  Range annotations bound the values a granted tag accepts.
- **A site allowlist can only narrow it.** `LGX_WRITE_ALLOWLIST` names a file
  of permitted tags. A write must be granted by the gate **and** listed; the
  list can never grant a tag the gate denies, and an invalid file fails the
  connect rather than being ignored. The two cases with no derived gate are
  below: no project open, and a partial export.
- **Gate-enforced, not gate-suggested.** Every write path — CIP, the
  in-memory virtual store, and the offline L5X file — passes through the
  same derived policy before touching the backend. A denied write never
  reaches the wire.
- **Partial exports: reads only, unless a site allowlist is set.** A routine or
  program export (`logix_open_project` reports `partialExport: true` and an
  `exportTarget`) holds only part of the controller's logic, so no gate is
  derived from it. The rule is the same on `cip`, `virtual` and `l5x`: the
  connect succeeds with `gated: false`, reads work, and `logix_write` is refused
  with `WriteGateRequired`. With `LGX_WRITE_ALLOWLIST` set, grants come only
  from the allowlist while every deny (`Safety_`, program-written,
  `UnknownWriteContext`, External Access, firmware types) and every range the
  partial file proves still applies; the connect result says `gated: true`
  with `gateSource: "allowlist+partial-export-denies"`. The allowlist cannot see
  logic outside the partial file, so list only tags the site knows are safe to
  write. Open a full controller export for the complete gate.
- **CIP with no project open: reads only, unless a site allowlist is set.**
  The gate comes from a project. If you connect over CIP without one, the
  connect result says `gated: false` and `logix_write` is refused with
  `WriteGateRequired` — nothing reaches the controller. Reads stay open. Open
  the project first (`logix_open_project`, then `logix_connect`) to have the
  gate on CIP. Alternatively, a site allowlist (`LGX_WRITE_ALLOWLIST`) is
  enough on its own: only the tags it lists can be written, with no
  Cmd_/Sim_/Safety_/ProgramWritten check or bounds, because those come from
  the project (live writes must still be enabled, and the controller still
  enforces External Access).
- **CIP values are typed, not guessed.** Reads and writes use the tag's type
  from the project, or from the controller's tag list for whole atomic
  controller tags. Members and structures need the project; a value that does
  not fit the type is refused and nothing is written.
- **Provenance on every decision.** Every grant and every deny names the
  exact L5X element responsible, so a denial is explainable, not opaque.
- **Bounds checking.** A granted, range-annotated tag rejects an
  out-of-range value with the range cited.

## Audit trail

Every gate decision, tag write and connect/disconnect is
appended as a JSON line to a local `audit.jsonl`. Per-call instrumentation
(tool name, byte sizes, timing, success/failure — **never argument or
response content**) is appended separately to `traffic.jsonl`. Both files
are strictly local; nothing is transmitted off the machine. See
[`docs/safety-model.md`](docs/safety-model.md#audit-trail) for file
locations and the full field reference.

The audit log is **not tamper-evident**: it is a plain append-only text file
with no hash chain or signature, so anyone with write access to the log
directory can edit or delete lines. Records carry the controller
(`gateway`/`path`) and an ISO 8601 UTC timestamp with milliseconds. When an
append fails the server writes `audit write failed: <reason>` to stderr and
`logix_doctor` reports `auditWriteFailures`. By default a failed append never
blocks a write; set `LGX_AUDIT_FAIL_CLOSED` (`1`, `true` or `yes`) to check before each `logix_write` that the log can be opened
for append and refuse it with `kind: "AuditUnavailable"` if not. Rotation
(`LGX_AUDIT_MAX_MB`, `LGX_AUDIT_KEEP`) deletes the oldest rotated file once the
keep count is reached, so it is not an archive; copy the log elsewhere if you
need a retained record. See
[environment variables](docs/tool-reference.md#environment-variables).

## Limitations

- **Audit append happens after the write.** The audit record for a write is
  appended only once the write has been attempted. `LGX_AUDIT_FAIL_CLOSED=1`
  checks *before* the write that the audit log is writable, but that is a
  pre-flight check, not a transaction: if the append fails after the write
  succeeds (disk full, directory removed, permissions changed between the check
  and the append), the controller change has happened and no audit record exists
  for it. The failure is reported on stderr and by `logix_doctor`
  (`auditWriteFailures`), but the write is not rolled back. Treat the audit log
  as best-effort evidence, and keep an independent controller-side record where
  you need a guaranteed one.

## Load paths and untrusted files

- The native EtherNet/IP library is extracted into a new private temp directory
  (mode 0700 on Unix), never reused, and its SHA-256 is checked against the
  embedded copy before it is loaded.
- Untrusted L5X/XML (opened or diffed projects, `logix_import_datatype`
  input) is capped at 64 MB and parsed with DTDs refused (`InputTooLarge`,
  `InvalidL5x`).
- `logix_import_datatype` never replaces an existing `saveAs` unless
  `overwrite=true` (`TargetExists`).

## Prompt-injection surface

- **Tool descriptions and the server's `instructions` field are static,
  version-locked content from this repository.** Nothing remote or
  user-generated is ever interpolated into a tool description.
- **Project content is data, not control.** Rung comments, tag
  descriptions, and rung neutral-text read from the customer's project file
  do flow into the model's context — a project file accumulated over years
  by many hands could carry adversarial or merely careless text. These
  fields are wrapped in an `<untrusted source="…">…</untrusted>` boundary
  before they reach the model (see
  [`docs/safety-model.md`](docs/safety-model.md#provenance-boundary--untrusted-project-text)).
  This is a provenance marker, not a sanitizer — it does not guarantee a
  model cannot be steered by what it reads.
- **The enforcing half is client-side.** Decisions about what a model may
  *do* after reading untrusted project text — including what tool calls it
  may make next — are enforced by your MCP client's own permission
  configuration, not by this marker. Pair the delimiter with client-side
  permission rules, especially around any tool that can reach a live
  controller.
- **Network surface: none.** The server speaks MCP over stdio only — no
  HTTP port, no bearer token, no listener of any kind. The only network
  traffic it originates is the CIP/EtherNet-IP traffic to the controller
  you explicitly connect it to.

## Supported versions

| Version | Supported |
|---------|-----------|
| `1.0.x` | yes (active) |

## Reporting a vulnerability

Open a private security advisory on the project's repository (preferred), or email
`cgray@asqi.org`. In
scope: a write reaching the controller despite a gate denial; path traversal or arbitrary write via `logix_import_datatype` or any
file-path argument; subprocess or command injection via any
`bootstrap/*.ps1` argument. Out of scope: anything requiring an
already-elevated local attacker, and behavior differences between Echo and
physical hardware that are Rockwell's own documented emulation limitation
rather than a defect in this project.
