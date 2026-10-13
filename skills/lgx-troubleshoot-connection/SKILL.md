---
name: lgx-troubleshoot-connection
description: Diagnose connect/read failures over CIP (EtherNet/IP). Use when logix_connect fails, reads return errors, or tools report ConnectionFailed / TagNotFound / WriteDenied.
---

# Troubleshoot connections

**Run `logix_doctor` first.** It is read-only (it never connects, changes mode or writes) and
returns `{overall, checks:[{id, status, observed, fix?}]}`. Every `warn` / `fail` check carries a
`fix` naming the exact next step; do that before guessing. Pass `commPath` and/or `controllerIp`
when you know the target so `controllerReachable` can run. Then use the tables
below for symptoms the doctor does not cover.

| Doctor check | Not `ok` means |
|---|---|
| `server` | always `ok`; shows version, OS session and the capability flags |
| `audit` | audit directory not writable, or `audit.jsonl` over its rotation limit |
| `cipRuntime` | the CIP library's native component (libplctag) did not load — install the Microsoft Visual C++ x64 redistributable (`vc_redist.x64.exe`), then restart the MCP server |
| `controllerReachable` | no TCP connection to `<ip>:44818` — check power, IP, route, firewall |
| `session` | the active session is the offline project but a `commPath` was given — `logix_connect` with `backend="cip"` and the controller's gateway and path. (If the whole CIP library fails to load, that is `cipRuntime`: install the VC++ x64 redistributable.) |
| `controllerFault` | controller FAULTED → `logix_diagnose` (decoded major fault, suspect rungs, fix); lgx-mcp cannot clear faults — clear in Logix Designer after correcting the cause |

Ignore the doctor's SDK, Echo and Studio checks (`sdkInstalled`, `sdkService`, `echoService`, `pendingZone`, `studio`): this build has no Logix Designer SDK lane, and `echoService` reports `not checked: this build has no Logix Designer SDK support`.

## How to connect

`logix_connect` goes **CIP** (`backend="cip"`, default): EtherNet/IP straight to the controller's
IP, port 44818. No Rockwell software is needed. Always pass `path` (e.g. `"1,0"` = backplane
slot 0). Two offline alternatives involve no controller: `backend="l5x"` (reads and writes the
opened L5X file) and `backend="virtual"` (in-memory tags seeded from the opened project); both
need `logix_open_project` first.

| Symptom | Meaning / next step |
|---|---|
| `ConnectionFailed` naming :44818 | controller unreachable — IP, network, controller powered/emulator running; check `path` |
| `StructureReadUnsupported` on a read | the tag is a structure, UDT or STRING; over CIP a STRING member (`Tag.Name`) is refused the same way. Read the individual members instead (e.g. `LEN`) |
| `TagNotFound` | the tag does not exist on that target — check spelling and scope (`Program:Name.Tag`), and `logix_find_tags` against the open project |
| `WriteDenied` | the write gate refused it; read the deny reason and use `lgx-write-gate`. Not a connection problem |
| `WriteGateRequired` | no project was open when you connected over CIP — `logix_open_project`, then reconnect |
| `CapabilityDisabled` on a live `logix_write` or a mode change | live controller changes are opt-in: the server needs `LGX_ENABLE_LIVE_WRITES=1` in its environment (`logix_ping` shows `capabilities.liveWrites`). Tell the engineer; do not look for a workaround |
| an I/O tag (e.g. `Local:2:I.Data`, `Drive_1:I`) reads wrong or stale | `logix_list_modules`: find the card from the tag prefix (`tagPrefix` — `Local:<slot>:I` for chassis modules, `<name>:I` for Ethernet modules), then check its `inhibited` and `majorFaultOnConnectionLoss` flags, parent and `slotOrAddress`. Pass `probe: true` for read-only routed Identity reads; `not_responding` means the module did not answer through the controller, not the controller's own I/O fault bits. A module with no `tagPrefix` has no input tag defined in the project |
| structure member read fails | atomic, STRING and nested members (`Struct.Member`, `Struct.Inner.Member`) are all readable; check the member path and spelling against `logix_list_datatypes`, and that the tag exists on the target (`TagNotFound`) |

### `connecting` is not a failure

A connect that runs as a background job and outlives its sync budget (`LGX_CONNECT_SYNC_BUDGET_S`,
default 20 s) answers `status: "connecting"` with `connectId`, `phase` and `elapsedMs`, and keeps
running in the server. **When you get `connecting`, poll `logix_connect_status`** (read-only)
until it reports `connected` or `failed`; do not call `logix_connect` again (different arguments
are refused with `ConnectInProgress`). `failed` carries the original typed error; use the table
above. `phaseTimings` (`upload`, `save-l5x`, `parse`, `gate-derivation`) show which phase is slow.
If the client says "Request timed out" before any answer, lower `LGX_CONNECT_SYNC_BUDGET_S` below
the client's timeout and see `docs/troubleshooting.md`, "Connect takes minutes / Request timed out".

Always state which target a result came from: the live controller (CIP), the L5X file, or the
virtual store.
