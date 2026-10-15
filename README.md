# lgx-mcp

Version 1.0.1. Verified on FactoryTalk Logix Echo only; not verified on a physical controller. See docs/verified-on.md.

MCP server for Allen-Bradley/Logix PLCs. Static `.L5X` analysis, live tag
access over CIP (EtherNet/IP), and **write permissions derived from your own
project file** — never typed into a config. Open a project, and lgx-mcp reads
your tag names, UDT member descriptions, safety-tag prefixes, and rung
neutral-text to build a write-gate policy: every tag is classified as granted
or denied, and every decision carries a provenance string naming the exact
L5X element that justified it.

> *"Open this project and tell me what an agent would be allowed to write."*
>
> *"Read MainRoutine and check whether every rung that sets Motor_Run also
> checks Safety_OK."*

## What it is

**The SDK lane.** With your own licensed Logix Designer and Logix Designer SDK,
an agent can propose online rung edits in Logix Designer while you watch. An edit
moves through a pending-edit lifecycle: propose, then pending on the controller,
then test, then assemble. Before anything reaches the controller, lgx-mcp can run
a build check on an offline copy of your project, so undefined tags show up
first. The engineer decides when to test and when to assemble: by default the
agent only proposes. The lane is optional, Windows-only, and off unless you build
and provision it; see [Logix Designer SDK lane](#logix-designer-sdk-lane). It is
verified on Echo only.

The rest of lgx-mcp needs no Rockwell software:

- **Static analysis** — open a `.L5X` file (or, in the SDK lane, an `.ACD`
  project) and ask about tags, data types, routines, cross-references, and
  diffs between two project versions, with no controller required.
- **Derived write-gates** — the open project is the single source of truth for
  what may be written: `Cmd_*` and `Sim_*` tags are writable; logic-written,
  `Safety_`-prefixed and undesignated tags are denied. Every decision traces to a
  specific L5X element. A partial export derives no gate; see the
  [safety model](docs/safety-model.md#partial-exports).
- **Live tags over CIP** — read and poll tags against a real controller
  using [libplctag](https://github.com/libplctag/libplctag), license-free.
- **Static interlock proof** — check whether every rung that writes a given
  output tag also carries a named condition contact, and get back the exact
  rung(s) that do not.

## What's verified, and on what

**The capabilities listed in [`docs/verified-on.md`](docs/verified-on.md) were
verified against FactoryTalk Logix Echo — Rockwell's software emulator — and
none of them has been verified against a physical controller.** Rockwell's own documentation is explicit that emulated
controller behavior can differ from physical hardware. Re-verify anything you
rely on against your own physical controller before depending on it.

## Requirements

- **Static analysis + CIP tag access:** Windows, macOS, or Linux; the .NET
  runtime bundled in the published binary. No Rockwell software installed.
- **Project files:** `.L5X` for the core build. `.ACD` needs the
  [SDK lane](#logix-designer-sdk-lane); without it, export your project to `.L5X`
  from Logix Designer first.
- **SDK lane (optional):** Windows, plus your own licensed Logix Designer and
  Logix Designer SDK: v36 or later; verified on v38 (see
  [`docs/verified-on.md`](docs/verified-on.md)). See
  [Logix Designer SDK lane](#logix-designer-sdk-lane).
- An MCP client: Claude Code, Claude Desktop, VS Code (Copilot agent mode),
  or any other MCP-compatible client.

## Testing

`make test` runs without any Rockwell software: it builds and tests with the SDK
lane switched off, so the SDK-lane code is not exercised by it. Exercising that
lane needs the SDK build described in
[Logix Designer SDK lane](#logix-designer-sdk-lane) and your own Logix Designer.
The documentation tests (README, `docs/`, `NOTICE` and the safety claims) read
the documents at the repository root and run as part of `make test`. A few
individual tests inside shipped test files read development-only files; they
are tagged `Scope=Internal` and `make test` skips them here.

## Quickstart (Windows)

From a regular PowerShell window:

```powershell
git clone https://github.com/asqi-carter/lgx-mcp.git
cd lgx-mcp
powershell -ExecutionPolicy Bypass -File bootstrap\install.ps1
```

`install.ps1` needs only PowerShell and winget: no make, bash or Python. It
installs the .NET 10 SDK if it is missing, finds your Logix Designer SDK package
(or builds the no-SDK build if there is none), builds, checks that no Rockwell
file is in the output, installs to `%LOCALAPPDATA%\Programs\lgx-mcp`, runs the
cold-install self-check, and registers `lgx-mcp` with Claude Desktop. Add
`-EnableLiveWrites` to allow tag writes and `-EnableStudioAutomation` to let the
agent click Test and Assemble in your open Studio window; both are off by
default. `-Client ClaudeCode` or `-Client VSCode` registers with those instead.

Then fully quit Claude Desktop from its tray icon and start it again. Under
Settings → Developer, `lgx-mcp` shows `running`; ask Claude to run
`logix_doctor`. In **Cowork**, start a new task: the lgx-mcp tools are already
there (see [Claude Desktop setup](docs/install-claude-desktop.md#cowork)).

The sections below cover the release zip, the manual steps and every option.

## Install (10 minutes)

### Prerequisites

- To build from source with `make release-nosdk` (not needed to run a published
  release zip): **`make`**, **`bash`**, **`python3`**, **`sha256sum`** (or
  `shasum`) and the **.NET 10 SDK**. On Windows run it from WSL or
  Git Bash.
- **Microsoft Visual C++ 2015–2022 Redistributable (x64)** — Windows only, for
  the CIP backend (`backend="cip"`).
- **`make`, `tar` and `pwsh`** (PowerShell 7) — to run the tests.

### Get lgx-mcp

On Windows, run `bootstrap\install.ps1` as in the [Quickstart](#quickstart-windows).
Or build from source and run the manual steps below. Release zips
(`lgx-mcp-<version>-win-x64.zip`, `lgx-mcp-<version>-linux-x64.zip`) can be
unpacked in place of the build, when published.

To build the zips from source, run this in bash (Linux, macOS, WSL or Git Bash on Windows), not in PowerShell:

```bash
make release-nosdk
```

This writes `dist/lgx-mcp-<version>-win-x64.zip`,
`dist/lgx-mcp-<version>-linux-x64.zip` and `dist/SHA256SUMS`. Unpack the zip for
your platform, then on Windows run the installer from the unpacked directory:

```powershell
powershell -ExecutionPolicy Bypass -File bootstrap\setup.ps1 -AppDir <path>
```

`<path>` is the unpacked directory that contains `LogixMcp.exe`.

`setup.ps1` runs a cold-install gate that launches the binary and drives
`logix_ping → logix_open_project` before declaring success. The
`logix_connect` SDK probe is not part of the no-SDK gate.
The release zip contains no Rockwell software.

**Windows execution policy.** If the release zip was downloaded, Windows marks
it as blocked and PowerShell may refuse to run the scripts. Unblock the zip
before unpacking it, then run the installer with the policy bypassed for that
one process:

```powershell
Unblock-File .\lgx-mcp-<version>-win-x64.zip
powershell -ExecutionPolicy Bypass -File bootstrap\setup.ps1 -AppDir <path>
```

To register lgx-mcp with your MCP client, run
`bootstrap/setup-mcp-client.ps1` with `-Client ClaudeCode`, `ClaudeDesktop` or
`VSCode`. Without `-WriteConfig` it only prints the command or JSON block it
would use; with `-WriteConfig` it applies it. It needs the same execution
policy bypass:

```powershell
powershell -ExecutionPolicy Bypass -File bootstrap\setup-mcp-client.ps1 -Client ClaudeCode -AppDir <path> -WriteConfig
```

`-EnableLiveWrites` applies to every build. `-EnableStudioAutomation`,
`-PublishMode` and `-LiveFinalize` belong to the SDK lane: for a no-SDK build
`setup-mcp-client.ps1` refuses the first two, and `-LiveFinalize` has no effect.
For an SDK build they set `LGX_ENABLE_STUDIO_AUTOMATION`, `LGX_PUBLISH_MODE` and
`LGX_ENABLE_LIVE_FINALIZE`. See [Logix Designer SDK lane](#logix-designer-sdk-lane).

Or point your MCP client at the published binary as a stdio server by hand. See:

| | |
|---|---|
| [Claude Code](docs/install-claude-code.md) | CLI registration |
| [Claude Desktop](docs/install-claude-desktop.md) | GUI client config |
| [VS Code](docs/install-vscode.md) | Copilot agent mode / native MCP |

## Tools

42 tools, each with exactly one row below: 29 in the core build, and 13 more that
exist only in an SDK build (the [SDK lane](#logix-designer-sdk-lane) tables). The Parameters column lists every
argument a tool takes: required ones first, then optional ones marked `?` with
their defaults where there is one; `—` means the tool takes none. The
[tool reference](docs/tool-reference.md) says what each tool does and returns,
what it can change, and the gates and flags it needs, and describes the main
parameters in each tool's entry.

### Read and analyze

Nothing here writes a tag, a file, or the controller (except session state for
open/close).

| Tool | Parameters | Purpose |
|---|---|---|
| `logix_open_project` | `path` | Load a `.L5X` file (an `.ACD` in an SDK build). A full controller export derives the write gate when you connect; a routine or program export is reported as `partialExport` with an `exportTarget` and derives none. |
| `logix_close_project` | — | Unload the open project. |
| `logix_project_summary` | — | Controller info and per-program tag and routine counts. |
| `logix_find_tags` | `namePattern?`, `dataType?` | Filter tags by glob name pattern and/or data type. |
| `logix_get_routine` | `program`, `routine` | Rung-by-rung neutral text and comments for a routine. |
| `logix_get_rung_diagram` | `program`, `routine`, `rung`, `format?` (default `text`) | Draw one rung or a range of up to 10 as ladder text and/or SVG. |
| `logix_trace_origin` | `tag`, `maxDepth?` (default `6`), `focus?`, `format?` (default `text`) | Trace where a tag's value comes from: its writers, their sources and gating conditions, back to literals and external inputs. |
| `logix_cross_reference` | `tag` | Every rung that reads or writes a tag. |
| `logix_diff_projects` | `pathA`, `pathB`, `normalize?` (default `true`) | Component-level diff between two `.L5X` files. |
| `logix_diff_against_controller` | `l5xPath` | Diff an `.L5X` file against the controller image an online SDK session uploaded at connect. Without one (the core build holds none) it returns `NoActiveSession` or `NotOnline`. |
| `logix_list_datatypes` | `nameContains?`, `userOnly?` (default `true`) | Data types in the open project, with member detail. |
| `logix_list_modules` | `probe?` (default `false`) | The I/O module tree, optionally probing each module read-only. |
| `logix_gate_report` | `commandPrefixes?`, `safetyPrefixes?` | What this session may write, and the L5X element behind each decision. |
| `logix_verify_interlock` | `outputTag`, `conditionTag` | Statically prove whether every rung writing an output carries a condition contact. |
| `logix_lint` | `program?`, `routine?`, `namingPattern?` | Static lint of ladder routines (defeated interlocks, bad JSR calls, divide by zero, and more). |
| `logix_document` | `scope?` (`controller`, `program`, `routine`), `program?`, `routine?`, `includeRungText?` (default `true`) | Deterministic Markdown documentation of the open project. |
| `logix_list_skills` | — | List the bundled agent-playbook skills. |
| `logix_get_skill` | `name` | Return the full playbook for a named skill. |
| `logix_list_options` | `parameter?` | List the legal values of every fixed-choice parameter, or of one. |
| `logix_read` | `tag` | Read a tag's current value. |
| `logix_poll` | `tags`, `count?` (default 3), `intervalMs?` (default 1000) | Poll a set of tags and report only what changed. |
| `logix_diagnose` | `controllerIp?` | Read-only controller fault diagnosis. |

### Live writes

Each of these is off by default or needs an explicit confirmation. See the
[safety summary](#safety-summary).

| Tool | Parameters | Purpose |
|---|---|---|
| `logix_write` | `tag`, `value` | Write one tag; gate-enforced, live targets need `LGX_ENABLE_LIVE_WRITES=1`. |
| `logix_controller_mode` | `mode?`, `confirm?` (default `false`) | Read the controller mode; mode changes on a live controller are not applied. |
| `logix_import_datatype` | `acdPath`, `datatypeName`, `saveAs`, `l5xPath?`, `members?`, `collision?`, `dryRun?` (default `true`), `confirm?`, `overwrite?` | Offline datatype import into a new copy (a file write, not a live one); `dryRun` by default. The core build writes an `.L5X` copy; an `.ACD` `saveAs` needs the SDK lane. |

### Setup

| Tool | Parameters | Purpose |
|---|---|---|
| `logix_ping` | — | Server identity, version, and which opt-in switches are on. |
| `logix_doctor` | `commPath?`, `controllerIp?` | Read-only setup and health check with the next step for every problem. Available in every build. |
| `logix_connect` | `backend?` (`cip` default, `virtual`, `l5x`, and `sdk` in an SDK build), `gateway?`, `path?`, `useVirtual?`; SDK build only: `sdkMode?` (`offline` default, `online`), `sdkCommPath?`, `allowControllerDownload?` (default `false`), `confirmedByUser?` | Open a tag session: `cip` (live), `virtual`, `l5x`, or `sdk` (the Logix Designer SDK lane). On a partial export no gate is derived and writes are refused (`WriteGateRequired`) unless `LGX_WRITE_ALLOWLIST` is set. A controller download (`allowControllerDownload`) is off by default; see `LGX_ENABLE_CONTROLLER_DOWNLOAD`. |
| `logix_connect_status` | — | Read-only. Progress of a background SDK connect after `logix_connect` answered `connecting`: `status` (`connecting`, `connected`, `failed`), `connectId`, `phase`, `elapsedMs`, `phaseTimings`. `connected` carries the normal connect result, `failed` the original typed error; with no connect in flight it returns the last outcome or `NoConnectInProgress`. |

### SDK lane: rung edits, build and test

Present only in an SDK build, and they work only in an SDK session
(`logix_connect` with `backend="sdk"`). See
[Logix Designer SDK lane](#logix-designer-sdk-lane) for what each does to a
running controller.

| Tool | Parameters | Purpose |
|---|---|---|
| `logix_export_rungs` | `xPath` | Export a routine's rungs as an L5X fragment (read-only). |
| `logix_propose_rung_edit` | `routineXPath`, `insertPosition`, `replaceCount`, `rungs?`, `tags?`, `l5xFragment?`, `offline?` (default `false`), `target?` (`workstation` default, `controller`, `live`), `confirmedByUser?` (default `false`), `expectedRungText?` | Stage a rung edit, publish it as a pending edit (`target="controller"`), or publish and finalize it in one step (`target="live"`). Gate-enforced on every tag the edit references; `controller` and `live` need `LGX_ENABLE_LIVE_WRITES=1`, and `live` also `LGX_ENABLE_LIVE_FINALIZE=1` and `confirmedByUser=true`. |
| `logix_create_routine` | `program`, `name`, `type?` (only `RLL`), `description?`, `confirmedByUser` (default `false`) | Create an empty ladder routine in a program, ready for `logix_propose_rung_edit`. Needs `LGX_ENABLE_LIVE_WRITES=1` and `confirmedByUser=true`. Refused with `PendingEditsBlockImport` while the program has unassembled edits. The routine stays even if later edits are cancelled; the SDK cannot delete it, so remove it in Studio. |
| `logix_pending_edits` | `program?`, `routine?` | Read the pending-edit state of a routine, a program or the whole controller. Read-only. |
| `logix_clear_staged` | `routine?`, `program?` | Discard this session's staged rungs and tags for one routine, or for all when `routine` is omitted. Server-side only; the controller is not touched. |
| `logix_build` | `target?` (`default`, `physical`, `echo`) | Structural build check of an offline saved copy of the session's project; never goes online or downloads. Needs Logix Designer v37 or later. |
| `logix_run_test` | `cases`, `confirmedByUser?` (default `false`) | Scripted logic tests that write inputs and check outputs. Emulator only: refused on any other controller. Needs `LGX_ENABLE_LIVE_WRITES=1` and `confirmedByUser=true`. |
| `logix_finalize_edits` | `program`, `routine`, `confirm?` (default `false`) | Never assembles an existing pending zone. It reports the zone and names the way to accept or cancel it. |

### SDK lane: Studio automation

Each of these drives the Studio 5000 Logix Designer window on the engineer's
interactive desktop (the SDK has no call for these steps). All are refused unless
`LGX_ENABLE_STUDIO_AUTOMATION=1` and `confirm=true`, and they refuse when Studio
is not installed or nothing is pending. Accept, test, untest and assemble also need
`LGX_ENABLE_LIVE_WRITES=1`.

| Tool | Parameters | Purpose |
|---|---|---|
| `logix_accept_pending_edits` | `program`, `routine`, `confirm?` (default `false`) | Accept the pending edits and finish the zone in one step. The new rungs then execute on a running controller. |
| `logix_test_pending_edits` | `program`, `routine`, `confirm?` (default `false`) | Test the accepted edits: the new logic executes and the original is kept. |
| `logix_untest_pending_edits` | `program`, `routine`, `confirm?` (default `false`) | Put the original logic back after a test. |
| `logix_assemble_pending_edits` | `program`, `routine`, `confirm?` (default `false`) | Assemble a tested zone. Irreversible. |
| `logix_cancel_pending_edits` | `program`, `routine`, `confirm?` (default `false`) | Cancel the pending or accepted edits. |

## Environment variables

Every variable the server reads. All are optional. Rows marked **SDK lane** apply
only to an SDK build (see [Logix Designer SDK lane](#logix-designer-sdk-lane)); a
no-SDK build ignores them. Set them in the environment of the MCP server process (for example with `-e NAME=value` on `claude mcp add`,
or the `env` block of your client's config).

| Variable | Default | Effect |
|---|---|---|
| `LGX_ENABLE_LIVE_WRITES` | off | `1`, `true` or `yes` allows live tag writes and controller mode requests. Without it, `logix_write` and `logix_controller_mode` are refused on live targets. Reported by `logix_ping` as `capabilities.liveWrites`. |
| `LGX_CONNECT_SYNC_BUDGET_S` | `20` | Seconds an SDK-backend `logix_connect` waits before answering `status: "connecting"` (with a `connectId`, the current `phase` and `elapsedMs`) while the connect keeps running in the server. A connect that finishes within the budget returns its normal result. Clients that declare the MCP Tasks extension run the connect as a task instead and are not affected. `0` always answers `connecting` at once. Values above `600` are clamped to 600 s (logged). |
| `LGX_WRITE_ALLOWLIST` | unset | Path to a text file listing the tags a session may write. With no project open it is the only check on a live session; with a full export it only narrows what the derived gate grants; with a partial export it is the only source of grants and the partial file's denies and ranges still apply. See the [safety model](docs/safety-model.md). |
| `LGX_GATE_COMMAND_PREFIXES` | `Cmd_,Sim_` | Comma-separated tag-name prefixes (case-insensitive) that designate writable command tags, for example `Cmd_,Sim_,HMI_`. `*` alone designates every tag name. It relaxes naming only; logic-written, safety, External Access and consumed tags stay denied. An entry with characters outside `A-Z a-z 0-9 _` fails `logix_connect` with `GateConfigInvalid`. The Safety prefix is not configurable. `logix_connect` and `logix_gate_report` report `commandPrefixes` and `commandPrefixesSource`. See the [safety model](docs/safety-model.md). |
| `LGX_TAG_ROUTE` | `auto` | Which channel carries tag reads and writes in an SDK online session: `auto` uses CIP when a CIP connection to the same controller succeeds and otherwise the SDK; `cip` uses CIP only and fails with `TagRouteUnavailable` when CIP cannot connect; `sdk` always uses the SDK. The SDK session stays the source of types and the write gate; CIP carries only the values. In `auto` a CIP failure during a call is retried once over the SDK and the result says so (`routeNote`); in `cip` it is returned as a typed error. Every read, write and poll result carries `route`, and `logix_doctor` (check `session`) shows the active route and why it was chosen. An unknown value fails `logix_connect`. **SDK lane.** |
| `LGX_AUDIT_MAX_MB` | `50` | Rotate `audit.jsonl` before an append would take it past this many megabytes. `0` never rotates; unset, unparsable or negative values use the default. |
| `LGX_AUDIT_KEEP` | `5` | Number of rotated audit files kept (`audit.1.jsonl`, `audit.2.jsonl`, …). Minimum `1`; the oldest is deleted. |
| `LGX_AUDIT_FAIL_CLOSED` | off | `1`, `true` or `yes` checks, before the write, that the audit log can be opened for append, and refuses `logix_write` with `AuditUnavailable` if it cannot. Otherwise a failed append is reported on stderr and by `logix_doctor`, and the write proceeds. |
| `LGX_ENABLE_STUDIO_AUTOMATION` | off | `1`, `true` or `yes` allows the five Studio automation tools (`logix_accept_pending_edits`, `logix_test_pending_edits`, `logix_untest_pending_edits`, `logix_assemble_pending_edits`, `logix_cancel_pending_edits`), which drive the Studio 5000 window on the interactive desktop. Each call also needs `confirm=true`; all but cancel also need `LGX_ENABLE_LIVE_WRITES=1`. **SDK lane.** |
| `LGX_ENABLE_LIVE_FINALIZE` | off | `1`, `true` or `yes` allows `logix_propose_rung_edit` with `target="live"`, which publishes and finalizes a rung in one call so it executes at once. Each call also needs `confirmedByUser=true` (even when `LGX_PUBLISH_MODE=auto`) and `LGX_ENABLE_LIVE_WRITES=1`. **SDK lane.** |
| `LGX_ENABLE_CONTROLLER_DOWNLOAD` | off | `1`, `true` or `yes` lets `logix_connect` download the open project to the controller when you pass `allowControllerDownload=true` (`backend="sdk"`, `sdkMode="online"`, no comm path). A download stops the running controller (PROGRAM mode), permanently overwrites the program stored in it, goes online and returns the controller to RUN; it also disconnects other Logix Designer sessions online with it. **Off by default**, and it is a last resort for a controller that is empty or holds an unrelated program, where upload cannot correlate the project. Even when set, it needs `LGX_ENABLE_LIVE_WRITES=1` and an explicit `confirmedByUser=true` on that call, which you pass only after the user approves; the server cannot verify the approval. Without the flag or the confirmation the call is refused before anything reaches the controller. **SDK lane.** |
| `LGX_PUBLISH_MODE` | `confirm` | `auto` publishes staged rung edits without a confirmation step; anything else means `confirm`. **SDK lane.** |
| `LGX_IMAGE_FINGERPRINT_TTL_S` | `5` | Seconds a controller fingerprint probe stays valid. A session-image read older than this probes the controller (routine names, rung counts, `TestEdits`, name and serial) to notice changes made outside this session, and refreshes the changed programs; reads within it cost no probe. `0` probes on every read; unset, unparsable or negative values use the default. `logix_diff_against_controller` always probes. **SDK lane.** |
| `LOGIX_SDK_PATH` | unset | Directory searched first for the Logix Designer SDK assemblies. **SDK lane.** |
| `XDG_STATE_HOME` | `~/.local/state` | Linux and macOS only: the log directory is `$XDG_STATE_HOME/logix-mcp/logs/`. Ignored on Windows, which uses `%LOCALAPPDATA%\logix-mcp\logs\`. |
| `WINDIR` | set by Windows | Read by `logix_import_datatype` to refuse a `saveAs` path inside the Windows directory. Not for you to set. |

## First session, no Rockwell software

1. `logix_open_project` a `.L5X` file.
2. `logix_gate_report` — see exactly what this session may write, and why.
3. `logix_verify_interlock` — statically prove (or disprove) a guard
   relationship between two tags.
4. `logix_connect` (CIP, license-free) and `logix_read` a live tag.

## First online session (SDK lane)

SDK build only, with your own Logix Designer and a controller (FactoryTalk Logix
Echo is the one it is verified on). Writes are off until you set the flags named
in [`docs/tool-reference.md`](docs/tool-reference.md); each step's gates are
listed there and in [`docs/safety-model.md`](docs/safety-model.md).

1. `logix_connect` with `backend="sdk"` and `sdkMode="online"`.
2. `logix_get_routine` — read the routine you intend to change.
3. `logix_propose_rung_edit` — stage the rung edit, then publish it as a pending
   edit (`target="controller"`).
4. Test it: in Logix Designer, or with `logix_test_pending_edits`.
5. Assemble it: in Logix Designer, or with `logix_assemble_pending_edits`. The
   engineer decides when; assembling is irreversible.
6. `logix_run_test` — run scripted input and output checks (emulator only).

See [`docs/tool-reference.md`](docs/tool-reference.md) for what every tool does,
returns and can change, and [`docs/verified-on.md`](docs/verified-on.md)
for the capabilities verified on Echo (none verified on a physical controller). Anything not listed there is not verified.

## Safety summary

- **Writes are gate-enforced, not merely gate-suggested.** A denied tag is
  rejected before anything reaches the wire; the rejection names the L5X
  element that caused the denial.
- **Live controller writes are off by default.**
  Set `LGX_ENABLE_LIVE_WRITES=1` to enable them. A controller mode change is
  requested but never applied on a live controller (`ModeChangeNotApplied`).
  Without them, the server still opens projects, runs static analysis, and
  reports what a session *would* be allowed to do.
- **SDK lane (SDK builds only):** a controller download is off by default and
  needs `LGX_ENABLE_CONTROLLER_DOWNLOAD=1`, `LGX_ENABLE_LIVE_WRITES=1` and a
  per-call `confirmedByUser=true`; Studio automation needs
  `LGX_ENABLE_STUDIO_AUTOMATION=1` and `confirm=true`; a live rung finalize needs
  `LGX_ENABLE_LIVE_FINALIZE=1` and `confirmedByUser=true`. By default an edit is
  only proposed, and the engineer accepts or cancels it.
- **This is not a safety system.** Nothing in this repository may be used to
  satisfy a safety function. See [SECURITY.md](SECURITY.md).

Full detail: [`docs/safety-model.md`](docs/safety-model.md).

## Logix Designer SDK lane

The SDK lane is an optional, Windows-only part of lgx-mcp that drives Rockwell's
Logix Designer SDK. It is switched off in the core build and in the release zips,
which contain no Rockwell software. You build it from source against **your own**
licensed Rockwell install; nothing from Rockwell is redistributed.

### Requirements

- **Windows** (x64). The SDK lane does not run on Linux or macOS.
- **Your own licensed Logix Designer, v36 or later**, and the **Logix Designer SDK**
  (it provides the `RockwellAutomation.LogixDesigner.CSClient` `.nupkg`). `logix_build`
  needs Logix Designer v37 or later; on v36 it refuses with `SdkCapabilityUnavailable`.
- **FactoryTalk Logix Echo** if you have no hardware: it is the bench controller
  for trying the lane, and `logix_run_test` works only against an emulator.
- For the Studio automation tools only: Studio 5000 Logix Designer running in your
  interactive desktop session.
- To build: the **.NET 10 SDK**, `make` and `bash` (WSL or Git Bash on Windows).

### Install

1. Copy `RockwellAutomation.LogixDesigner.CSClient.2.2.1109.nupkg` (the required
   version: **Logix Designer SDK 2.2.1109**; `install.ps1` and `setup.ps1` refuse any
   other) from your Logix Designer SDK install (by default `%PUBLIC%\Documents\Studio 5000\Logix Designer SDK\dotnet\`)
   into a `.localfeed/` directory at the repository root. It is not on nuget.org
   and is never committed or shipped.
2. Build the SDK binary, in bash:

   ```bash
   make publish-win
   ```

   The output is in `src/LogixMcp/bin/Release/net10.0/win-x64/publish`. The build
   fails if any `RockwellAutomation*` file would end up in it.
3. Provision it and run the cold-install gate, in PowerShell, from the repository root:

   ```powershell
   powershell -ExecutionPolicy Bypass -File bootstrap\setup.ps1 -AppDir src\LogixMcp\bin\Release\net10.0\win-x64\publish
   ```

   `setup.ps1` extracts the two Rockwell files it needs from your `.nupkg` into
   `sdk\vendor\` (pass `-NupkgPath` if the package is not in the default location),
   then launches the binary and drives `logix_ping`, `logix_open_project` and a
   `logix_connect` probe with `backend="sdk"` before it reports success. If the
   assemblies live elsewhere, set `LOGIX_SDK_PATH`.
4. Register it with your MCP client using `bootstrap\setup-mcp-client.ps1`, adding
   the opt-in switches you want (`-EnableLiveWrites`, `-EnableStudioAutomation`,
   `-LiveFinalize`, `-PublishMode`). See [Install](#install-10-minutes).

### What the lane adds

- **Online rung edits.** `logix_propose_rung_edit` stages rungs (and the tags they
  use) on the workstation, publishes them to the controller as a **pending edit**
  (`target="controller"`), or publishes and finalizes in one call
  (`target="live"`, which runs the rung at once). `logix_export_rungs` gives the
  current rung text to check against.
- **Pending-edit lifecycle.** `logix_pending_edits` reports the zone state. Moving
  it on (accept, test, untest, assemble, cancel) is Studio automation, off unless
  `LGX_ENABLE_STUDIO_AUTOMATION=1`; otherwise the engineer does it in Logix
  Designer (Logic, Online Edits). `logix_get_rung_diagram` labels pending rungs.
- **Structural build check.** `logix_build` saves a copy of the session's project,
  opens that copy offline, builds it with the SDK and deletes it, so undefined tags
  show up before you publish. It never builds in the online session, goes online,
  downloads or changes mode.
- **`.ACD` projects.** `logix_open_project` and offline SDK sessions take `.ACD`
  files, and `logix_import_datatype` can save to an `.ACD`.
- **SDK tag route with a CIP fast path.** In an online SDK session the SDK stays the
  source of types and of the write gate; `LGX_TAG_ROUTE` chooses whether values travel
  over CIP or the SDK. With `auto` (the default) CIP is used when it connects and
  the SDK otherwise. Every read, write and poll result reports its `route`.
- **Emulator tests and diffs.** `logix_run_test` runs scripted cases against an
  emulator only, and `logix_diff_against_controller` diffs an `.L5X` against the
  image uploaded when the SDK session connected.

### Safety and verification

Everything that changes a controller here is off by default and gate-enforced; see
the [safety summary](#safety-summary). Online edits can change what a running
machine does. Nothing in the SDK lane is a safety function, and the verification
caveat above applies to it in full: anything not listed in
[`docs/verified-on.md`](docs/verified-on.md) is not claimed as verified, and
nothing is verified on a physical controller.

## Documentation

| | |
|---|---|
| [Safety model](docs/safety-model.md) | Gate derivation, write path, audit trail |
| [Tool reference](docs/tool-reference.md) | Every MCP tool, parameters, returns, opt-in flags |
| [Errors](docs/errors.md) | Error kinds and what they mean |
| [Troubleshooting](docs/troubleshooting.md) | Symptom-indexed fixes |
| [What's verified](docs/verified-on.md) | Capabilities verified on Echo; anything not listed is not verified; none verified on a physical controller |
| [Security](SECURITY.md) | Disclosure, threat model, safety-function disclaimer |

## License

[MIT](LICENSE) · © 2026 ASQI · Not affiliated with or endorsed by Rockwell
Automation. Allen-Bradley, Logix, Studio 5000, ControlLogix, and FactoryTalk
are trademarks of Rockwell Automation, Inc.; this project interoperates with
locally installed, separately licensed Rockwell software without
redistributing it. See [NOTICE](NOTICE).
