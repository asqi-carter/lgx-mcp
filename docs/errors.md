# Error reference

Structured error records emitted by the MCP tools carry a `kind` field (the
machine-readable category) and usually a `hint` (a remediation pointer).
Where a record's shape is pinned by its own record type rather than a shared
`kind` string, that is called out below.

## Read errors (`logix_read`)

| `kind` | Meaning | Typical fix |
|---|---|---|
| `TagNotFound` | The named tag does not exist in the controller project — or the name is mistyped. | Verify the name with `logix_find_tags`. |
| `TypeMismatch` | The value or type could not be converted for the tag's declared data type. | Check the tag's type with `logix_find_tags` / `logix_list_datatypes`.  `TypeMismatch` is reserved for the server's own value-to-declared-type conversion failures. |
| `ProtocolError` | (CIP backend) The EtherNet/IP library rejected the request: bad parameter, bad data or bad configuration. The message includes the raw library status name and the tag. This is not a type problem. | Check the gateway and backplane path (for example `1,0`) and the tag name. |
| `ConnectionFailed` | The controller did not answer. On the CIP backend the connect probe has a hard 15-second deadline: if the controller neither answers nor refuses in that time (for example an unroutable address or a firewall dropping packets), the error says it timed out, naming the gateway, the path and the elapsed time. | Verify address, path and that the controller is running; see "Connect errors" below. |
| `CipRuntimeMissing` | (CIP backend) The CIP library could not load because the Microsoft Visual C++ 2015–2022 Redistributable (x64) is not installed. No controller was contacted. | Install `vc_redist.x64.exe` from Microsoft, then restart the MCP server. Studio 5000 installs it, so machines with Studio already have it. See [Troubleshooting](troubleshooting.md#logix_connect-cip-fails-with-dll-was-not-found-cipruntimemissing). |
| `TypeUnknown` | (CIP backend) The tag's data type could not be established: it is not declared in an open project and is not a whole controller-scoped atomic tag in the controller tag list. No value is returned; the type is never guessed from the tag's byte size. The refusal is audit-logged. | Open the project (`logix_open_project`) so member and structure types are known, then read again. |
| `StructureReadUnsupported` | (CIP backend) The tag is a structure, user-defined type or STRING. Whole structures are not decoded; when the project is loaded the message lists the member paths. A whole array (`Arr` where `Arr` is `[5]`) is refused the same way and its hint gives the element form (`Arr[0]`). The refusal is audit-logged. | Read the members or one element individually (`hint`: "read members individually"). |
| `IndexOutOfRange` | The tag path indexes an array past its dimension, for example `Arr[5]` on a `[5]` array (valid indexes 0 to 4). The error names the tag, the index and the dimension. Nothing was read. | Use an index inside the dimension (`logix_find_tags` shows `dimensions` for an array tag). |
| `MemberNotFound` | The tag path names a member the tag's data type does not have. The error lists the members the type has. Nothing was read. | Pick one of the listed members. |
| `BitOutOfRange` | (CIP backend) A bit-of-integer path (`Tag.N`, `Tag.Member.N`, `Local:1:I.Data.N`) named a bit the integer does not have, for example bit 40 of a DINT (bits 0 to 31). Nothing was read. A valid bit read returns a BOOL taken from the parent integer. The refusal is audit-logged. | Use a bit index inside the integer's width: SINT 0-7, INT 0-15, DINT 0-31, LINT 0-63. |
| `BitWriteUnsupported` | (CIP backend) A write targeted one bit of an integer (`Tag.N`). Over CIP that would be a read-modify-write of the whole integer, which can overwrite a change the running program makes in between, so it is refused and nothing is written. | Write the whole integer, or use a BOOL member. |
| `ValueUnavailableOffline` | (SDK backend, offline session) A read of module I/O (`Local:1:I.Data`, `Local:1:I.Data.3`) found the module and typed it from the export, but the offline file holds no value for it. The message names the type. Nothing was read; this is not a missing tag. | Connect to the controller to read live module I/O, or export the project with decorated data. |
| `OutOfBounds` | (CIP backend) The library reported that the request addressed data outside the tag's buffer. The message names the raw status and the tag. | Check the tag name, array index and element count. |
| `NoActiveSession` | The tool needs a live session and none is active. | Call `logix_connect` first. |
| `SessionNotOnline` | `logix_propose_rung_edit` was called on an SDK session that is not online (an offline project session). A pending controller edit needs a session connected to a live controller. Nothing was staged or sent. | Call `logix_connect` with `sdkMode="online"`, then retry. |
| `ConnectInProgress` | A `logix_connect` is still running in the background. Returned with `connectId`, `phase`, `elapsedMs` by tools that need the session (instead of `NoActiveSession`), and when a `logix_connect` with different arguments is refused. A repeat `logix_connect` with the same arguments returns the in-flight `connecting` status. | Poll `logix_connect_status` until it reports `connected` or `failed`, then retry. |
| `NoConnectInProgress` | `logix_connect_status` was called but no SDK connect is running and none has run in this server. | Call `logix_connect` with `backend="sdk"` first. |
| `ConnectOwnedByOtherInstance` | `logix_connect_status` found no connect in this server, but another lgx-mcp server instance on this machine owns one (running, or finished: `status` carries its outcome). Carries `ownerPid`, `phase`, `elapsedMs`, `status`. | That connection cannot be used from this instance. Wait for the other instance to finish, then call `logix_connect` here; do not start a parallel upload. |
| `ConnectBusy` | Another upload holds the host-wide upload lease (another server process, or another connect) and did not release it within the bounded wait. Carries `retryAfterSeconds` and `ownerPid`. Nothing was uploaded and the other session was not disturbed. | Call `logix_connect` again after `retryAfterSeconds`. |
| `SessionLost` | A call found the session's project closed (`Project is not open.`), typically because another server process reconnected. The session is marked lost, `logix_connect_status` reports `connected: false` with `sessionLost.cause`, and later calls keep returning `SessionLost` until a new session is attached. `lostAt` is when the loss was detected. `sdkRestartedAt` (only for cause `sdk-service-restarted`, when readable) is when the SDK server restarted, taken from the new `LdSdkServer` process start time, so it is earlier than `lostAt`; both also appear under `sessionLost` as `at` and `sdkRestartedAt` in `logix_connect_status`. | Call `logix_connect` to start a new session. Edits staged in the lost session are gone; the controller was not changed by the failed call. |
| `PublishInProgress` | `logix_clear_staged` was called while a controller publish (`logix_propose_rung_edit` with `target` `controller` or `live`) was running in the same session. Nothing was cleared or changed. | Wait for the publish to return, then call `logix_clear_staged` again if still needed. |
| `PostImportCheckInconclusive` | A rung import was accepted but the post-import state check saw a state that is neither Online nor Connected (`Connected` counts as Online). Carries `pendingAfterError` with the rungs a read-back found. The session is not blocked. | Check `pendingAfterError` and `logix_pending_edits`, then continue. |
| `Unknown` | An unexpected exception occurred while reading. | Check the session is still connected (`logix_connect`). |

A missing tag on `logix_read` is reported in the not-found shape (`status: "not_found"`, `what`, `path`) rather than as a `kind` record.

## Write errors (`logix_write`)

| `kind` | Meaning | Typical fix |
|---|---|---|
| `WriteDenied` | The gate policy derived from the open project rejected the write — the response names the deny reason (`SafetyTag`, `ProgramWritten`, `NotDesignated`, `UnknownWriteContext`, a bounds violation, or a bit or element write under a range-checked tag or member) and the provenance string behind it. | Only `Cmd_*`/`Sim_*` tags are write-designated by default; call `logix_gate_report` to see the full grant/deny list with provenance. |
| `TagNotFound` | The named tag does not exist (or the name is mistyped). | Verify with `logix_find_tags`. |
| `CapabilityDisabled` | The live-writes switch is off. The response names `LGX_ENABLE_LIVE_WRITES` in its `hint`. | Set the named environment variable only if you intend the operation, then restart the server. The gates fail closed: if the server cannot determine its capability state it refuses. |
| `TypeMismatch` | The value cannot be converted to the tag's declared type; it is refused rather than coerced. | Supply a value of the right type. |
| `StructureWriteUnsupported` | The write targets a whole structure, user-defined type, STRING or TIMER. Writes go one leaf at a time; the error lists the members (for a TIMER: `PRE`, `ACC`, `EN`, `TT`, `DN`). Nothing was written. | Write one member, for example `Timer1.PRE`. |
| `IndexOutOfRange` | The tag path indexes an array past its dimension. The error names the tag, the index and the dimension. Nothing was written. | Use an index inside the dimension. |
| `MemberNotFound` | The tag path names a member the tag's data type does not have. The error lists the members. Nothing was written. | Pick one of the listed members. |
| `InvalidValue` | The value is NaN or ±Infinity (`NaN`, `-NaN`, `nan`, `Infinity`, `-Infinity`, `∞`, or a numeric NaN/Infinity). Refused before the gate and the backend, whether or not the tag has bounds, and recorded in the audit log as a denied gate decision. Nothing was written. | Supply a finite number. |
| `InvalidTagName` | The tag name or member path is not a valid tag path, for reads and writes alike. Allowed: identifier segments (`[A-Za-z_][A-Za-z0-9_]*`) joined by `.`, with `[n]` or `[n,m]` indices and `:` for `Program:` and module tags. An empty name, or one containing a control character (a tab or newline, a NUL), is refused. On a write, leading and trailing whitespace and whitespace around `.` `[` `]` is trimmed before the gate matches the name and its member bounds; any other character outside the grammar is refused. Refused before any backend is touched. A refused write is recorded in the audit log as a denied gate decision. Nothing was written. | Correct the name to a valid tag path. |
| `ProtocolError` | (CIP backend) The library rejected the request (bad parameter, data or configuration); the message carries the raw status name. | Check gateway, path and tag name. |
| `ConnectionFailed` | The controller did not answer. | See "Connect errors". |
| `OutOfBounds` | (CIP backend) The request addressed data outside the tag's buffer, for example a write wider than the tag. | Check the tag name, array index and value size. |
| `NoActiveSession` | The tool needs a live session and none is active. | Call `logix_connect` first. |
| `AuditUnavailable` | `LGX_AUDIT_FAIL_CLOSED=1` is set and the pre-write check found that the audit log cannot be opened for append. Nothing was written to the controller. | Make the audit directory writable, or unset `LGX_AUDIT_FAIL_CLOSED`. See [environment variables](tool-reference.md#environment-variables). |
| `Unknown` | An unexpected exception occurred while writing. | Check the session is still connected. |

`ReadbackFailed` is not an error response: it is the `kind` written to the audit log when the write itself succeeded but the read-back afterwards threw. The tool then returns a partial `logix_write` result (the write happened, `verified` is not confirmed) rather than an error.

## Poll errors (`logix_poll`)

| `kind` | Meaning | Typical fix |
|---|---|---|
| `TagNotFound` | One of the polled tags does not exist. | Verify all names with `logix_find_tags`. |
| `InvalidValue` | A poll limit was exceeded: more than 64 tags, `count` above 1000, or `intervalMs` below 100. The message states the limit. Nothing was polled. | Poll fewer tags, lower `count`, or raise `intervalMs` to at least 100. |
| `NoActiveSession` | The tool needs a live session and none is active. | Call `logix_connect` first. |
| `Unknown` | An unexpected exception occurred during a poll cycle. | Check the session is still connected. |
| `InvalidArguments` | `count` is below 1, or the `tags` list is empty. The error names the parameter. Nothing was polled. | Pass `count` of at least 1 and at least one tag name. |

## Controller-mode errors (`logix_controller_mode`)

| `kind` | Meaning | Typical fix |
|---|---|---|
| `ModeChangeNotApplied` | A mode change was requested on a live controller and **was not applied**: this release does not change a live controller's mode. Nothing was sent. On a virtual session the request is recorded in memory only. | Change the mode in Studio 5000 or with the controller keyswitch. Do not tell the user the controller changed mode. |
| `CapabilityDisabled` | Mode changes on a non-virtual controller need `LGX_ENABLE_LIVE_WRITES`. | Enable deliberately, or use Studio 5000. |


## Datatype-import errors (`logix_import_datatype`)

| `kind` | Meaning | Typical fix |
|---|---|---|
| `InvalidSavePath` | `saveAs` does not end in `.L5X`, or points inside the server install directory or the Windows directory. Refused before anything is written. | Choose a `.L5X` destination outside the install directory. |
| `SaveAsEqualsInput` | `saveAs` resolves to the same file as `acdPath`. | Choose a different `saveAs`; the source project is never overwritten. |
| `InvalidInput` | An empty `saveAs`/`acdPath`/`datatypeName`, a bad `datatypeName` (letters, digits, underscores, at most 40 characters), or an `l5xPath` that does not end in `.L5X`, or is not an `RSLogix5000Content` document. | Correct the argument named in the message. |
| `InputTooLarge` | Untrusted L5X/XML input (an `l5xPath`, or a project opened or diffed by path) is larger than 64 MB. Nothing was parsed. Also returned by `logix_open_project`, `logix_diff_projects` and `logix_diff_against_controller`. | Use a smaller file. |
| `InvalidL5x` | Untrusted L5X/XML input is not well-formed or contains a DTD (`<!DOCTYPE ...>`), which is refused. Also returned by `logix_open_project`, `logix_diff_projects` and `logix_diff_against_controller`. | Supply a well-formed L5X without a DTD. |
| `FileNotFound` / `ReadError` | `l5xPath` does not exist or could not be read. | Check the path. |
| `WriteError` | The output file could not be written. | Check permissions and free space. |
| `ImportNotApplied` | The SDK import ran, but the type re-exported from the result does not have the requested members (for example `collision: Discard` kept the existing type). The response carries `datatypeName`, `expectedMembers`, `actualMembers`, `collision`, `reason` and `sdkMessages`. The saved file was not written and nothing changed; there is no `savedTo`. | Check `sdkMessages`. A type that must be replaced wholesale needs `collision: Overwrite`. |
| `ImportCancelledOnCollision` | `collision: Cancel` was passed and the type already exists, so the SDK aborted the import (`XMLSrv_E_IMPORT_ABORTED_NO_CHANGES`). The SDK message is in `sdkMessages`. Nothing was written. | Use `collision: Overwrite` to replace the type, or `Discard` to keep it. |
| `ProgramNotFound` | The `program` argument of `logix_lint` names a program the open project does not have. The message lists the programs it does have. | Use one of the listed program names, or omit `program` to lint every program. |
| `RoutineNotFound` | The `routine` argument names a routine the program does not have (`logix_get_routine`, `logix_get_rung_diagram`, `logix_document`, `logix_lint`, `logix_export_rungs`). The message lists the routines the program does have; `logix_export_rungs` keeps the raw SDK text in `sdkError`. | Use one of the listed routine names. |
| `CommPathInvalid` | `logix_connect` with `backend="sdk"` could not route the `commPath`. The same kind appears in the `logix_connect_status` mirror. | Fix `commPath` (the SDK takes a Logix Designer path such as `AB_ETH-1\\<controller-address>`, not a CIP backplane path). |
| `TagTypeConflict` | A `tags[]` declaration in `logix_propose_rung_edit` names a tag that already exists with a different data type. The error carries `tag`, `existingType` and `declaredType`. Nothing was staged or published. | Declare the existing type, or drop the declaration; same-type declarations are listed in `reusedTags` and ignored. |
| `UnknownDataType` | A `tags[]` declaration names a data type that is neither an atomic type nor a type in the project. The error carries `tag`, `dataType` and a hint naming `logix_list_datatypes`. Refused at stage; nothing was staged. | Call `logix_list_datatypes` and declare one of its types. |
| `TargetExists` | A real run found `saveAs` already present and `overwrite=true` was not passed. Nothing was written; the existing file is untouched. | Choose a new `saveAs`, or pass `overwrite=true` to replace it. |
| `SdkRequired` | `saveAs` is an `.ACD`, which this build cannot write. Only an `.L5X` fragment can be written. Nothing was written. | Use an `.L5X` `saveAs`. |
| `SdkCapabilityUnavailable` | The tool needs a Logix Designer SDK capability that is unavailable on this Designer version or install (for example the build check before Logix Designer v37). Refused before the SDK was called; nothing changed. The error names the `capability`, the `version` when known, and `whatStillWorks`. | Read `lgx://sdk/capabilities` for the full table, then use the manual route named in `whatStillWorks` (for the build check, Studio's Verify: Logic → Verify → Controller). |
| `BuildNotPerformed` | A build request reached the SDK and the SDK could not perform the operation (a server or internal error). The build did not run, so this is not a build failure. Nothing changed. | Retry; if it repeats, run `logix_doctor` and check the Logix Designer SDK service. |
| `TagRouteUnavailable` | `LGX_TAG_ROUTE=cip` is set and CIP cannot connect to the session's controller (or no CIP address could be derived from the comm path). Returned by the tools that read or write tags in an SDK session. Nothing was read or written; the SDK route is not used in `cip` mode. | Fix the CIP connection to the controller, or set `LGX_TAG_ROUTE=auto` or `sdk` and reconnect. |
| `AliasUnresolved` | A read of a program-scoped alias (`Program:P.Alias`) could not follow the alias's `AliasFor`: the target tag does not exist, the alias chain is circular, or the target is module I/O whose type the project does not declare. Nothing was read. A resolved alias read returns the target's `dataType` and `value` with `aliasFor`. | Fix the alias's `AliasFor` in the project, or read the target tag directly. |

## Diagnosis and write errors

| Tool | `kind` | Meaning | Typical fix |
|---|---|---|---|
| `logix_diagnose` | `NoActiveSession` | No active session and no `controllerIp` was passed. | Call `logix_connect` first, or pass `controllerIp` to `logix_diagnose`. |
| `logix_diagnose` | `IdentityUnavailable` | The controller's EtherNet/IP ListIdentity could not be read, so its product cannot be established: the session has no controller address (offline, virtual and file-backed sessions have none) or the read failed. Nothing was written. | Connect online (`logix_connect`) or pass `controllerIp`; check that port 44818 is reachable with `logix_doctor`. |
| `logix_diagnose` | `FaultRecordUnavailable` | The controller fault record (CIP class 0x73, instance 1) could not be read or parsed. | Check that port 44818 is reachable (`logix_doctor`), then retry. |
| `logix_write` | `WriteGateRequired` | The session has no write gate and no site allowlist is set, so nothing checks the write. Two causes: (1) a live CIP session was connected with no project open; (2) the open project is a partial (routine or program) export, so no gate can be derived from it. `logix_open_project` showed `partialExport: true` and an `exportTarget`; this applies to `cip`, `virtual` and `l5x` sessions alike, and the connect itself succeeded with `gated: false`. Nothing was written; reads are unaffected. | Open the engineer's full controller export (`logix_open_project`) and call `logix_connect` again so writes are checked against it, or set `LGX_WRITE_ALLOWLIST` to the tags this site permits. With a partial export and an allowlist, only listed tags can be written and the partial file's denies and ranges still apply (`gateSource: "allowlist+partial-export-denies"`). |
| `logix_write` | `AuditUnavailable` | `LGX_AUDIT_FAIL_CLOSED=1` is set and the pre-write check found that the audit log cannot be opened for append (audit directory not writable, disk full). Nothing was written to the controller. | Make the audit directory writable (`logix_doctor` `audit` check), or unset `LGX_AUDIT_FAIL_CLOSED`. |

## Other tool errors

| Tool | `kind` | Meaning | Typical fix |
|---|---|---|---|
| any tool | `InternalError` | The tool threw an unexpected exception. The error names the `tool`, the `exceptionType` and the first line of the `message`; the full exception is in the server log. | Check the server log (stderr) for the stack trace; retry, and report it if it repeats. |
| any tool | `InvalidArguments` | A required argument was missing (the error names the `parameter`), an argument had the wrong type or range (for example a negative `insertPosition` or `replaceCount`, named in `parameter` when it can be told), or `logix_write` `value` was not a JSON number, boolean or string. Nothing changed. | Pass the argument named in the error and call the tool again. |
| `logix_connect` | `ConfirmationRequired` | A connect option that needs the user's explicit approval was requested without `confirmedByUser=true`. Nothing was done. | Ask the user, then repeat the call with `confirmedByUser=true`. |
| `logix_get_rung_diagram` | `NotLadder` | The routine is structured text, function block or sequential function chart, so no rung diagram can be drawn. The response carries the `routineType` (`ST`, `FBD` or `SFC`). | Use `logix_get_routine` for the routine's text. |
| `logix_get_rung_diagram` | `InvalidValue` | `rung` is not a number or a range of at most 10 rungs, or `format` is not `text`, `svg` or `both`. | Pass one rung (`11`) or a range such as `3-7`, and a valid `format`. |
| `logix_trace_origin` | `InvalidValue` | `tag` is empty, not a tag reference or not declared; `maxDepth` is outside 1-50; `focus` is not a program in the project; or `format` is not `text`, `json`, `mermaid` or `all`. The error text says which. | Fix the named argument; for a program tag use `Program:<program>.<tag>`. |
| `logix_diff_against_controller` | `NoActiveSession` | There is no session, or the session holds no controller image. No session backend in this build holds one. | Use `logix_diff_projects` to compare two `.L5X` files. |
| `logix_diff_against_controller` | `NotOnline` | The session exists but is not an online controller session (virtual or file-backed). | Use `logix_diff_projects` to compare two `.L5X` files. |
| (write allowlist, `LGX_WRITE_ALLOWLIST`) | `AllowlistInvalid` | The allowlist file is unreadable, or a line holds an invalid entry. The response names the `path` and the `line`. Writes fail closed. | Fix the named line (tag names, or a trailing `*` prefix such as `Recipe_*`), or unset the variable to run without an allowlist. |

## SDK lane errors

These kinds come only from a build with the Logix Designer SDK lane (see the
[safety model](safety-model.md#the-sdk-lane-online-edits-and-studio-automation)).
Four kinds that belong to this lane, `SdkCapabilityUnavailable`,
`BuildNotPerformed`, `TagRouteUnavailable` and `SdkRequired`, are in the
[datatype-import table](#datatype-import-errors-logix_import_datatype) above.

### Logix Services faulted

| Tool | `kind` | Meaning | Typical fix |
|---|---|---|---|
| any SDK-backed tool | `SdkServerFaulted` | The Logix Designer SDK's server reported `LgxSrv_E_FATAL_ERROR` or `LgxSrv_E_SERVER_FAULTED`: Logix Services died or faulted. The record carries `session: "faulted"`. Edits staged in the dead session are lost. | Call `logix_connect` again (`logix_close_project` also works) to start a new session, then read `logix_pending_edits` to see what the controller actually holds. See [Troubleshooting](troubleshooting.md#logix-services-faulted-sdkserverfaulted). |
| `logix_connect` | `SdkServiceDegraded` | The controller answers on EtherNet/IP port 44818, but the SDK service's communications layer has faulted. The record carries the SDK's own code (`sdkCode`) and the `controllerAddress`. | Restart the `LdSdkService` Windows service, then call `logix_connect` again. |
| `logix_connect` | `SdkCommLayerFaulted` | The SDK reported a communications-layer fault (`RxE_COMM_*`). The SDK service is running but its comm layer is degraded; this is not by itself a dead controller. | Check whether the controller answers on port 44818: if it does, restart `LdSdkService`; if not, the controller or the network is the problem. |
| `logix_connect` | `SdkServiceUnstable` | The connect gave up after its crash-retry limit because the Rockwell SDK service (`LdSdkServer`) kept restarting. `sdkRestarts` lists every restart in that connect, one record per crash, including the one that triggered the give-up. | Wait a minute for the service to settle, then reconnect. `logix_doctor` shows recent crash counts. |

`SdkServiceDegraded` is also what the SDK's "Project is not open." message
becomes in a later call: the SDK service crashed or was stopped and the
session handle is no longer valid. Reconnect to start a new session.

### Connect and download errors (`logix_connect`, `backend="sdk"`)

| `kind` | Meaning | Typical fix |
|---|---|---|
| *(no `kind`)* | The SDK is not available in this build or on this machine. The response lists `probedPaths` (every directory searched, with the reason each was rejected) and a `hint`. | Install Logix Designer and the Logix Designer SDK, or set `LOGIX_SDK_PATH`; see [Troubleshooting](troubleshooting.md#logix_connect-sdk-fails-sdk-not-found). |
| `MissingSdkCommPath` | `sdkMode="online"` with neither `sdkCommPath` nor `gateway` and no project open. Nothing was contacted. | Pass `sdkCommPath` or `gateway` to upload the controller's current project, or open a project with `logix_open_project` first. |
| `DownloadRequired` | The file-based online route would need a controller download. The record states the effect: a download stops the running controller and permanently overwrites the program stored in it. Nothing was sent. | Prefer the upload route (pass a comm path). Authorising the download needs `LGX_ENABLE_LIVE_WRITES=1` and `LGX_ENABLE_CONTROLLER_DOWNLOAD=1` on the server, then `allowControllerDownload=true` and `confirmedByUser=true` after the user has approved. |
| `ConfirmationRequired` | `allowControllerDownload=true` was passed without `confirmedByUser=true`. Nothing was done. | Ask the user, then repeat the call. |
| `CapabilityDisabled` | `allowControllerDownload=true` was passed while `LGX_ENABLE_LIVE_WRITES` or `LGX_ENABLE_CONTROLLER_DOWNLOAD` is off. The response names the missing variable. Refused before any network or SDK call. | Set the named variable only if you intend the operation, then restart the server. |

### Publish errors (`logix_propose_rung_edit`)

Every error returned **after** the import reached the controller carries
`pendingAfterError`: the routine's pending rungs read back, or `unknown` with
the read-back error. No message claims that nothing is pending unless a
read-back or a successful go-offline confirmed it.

| `kind` | Meaning | Typical fix |
|---|---|---|
| `InsertPositionOutOfRange` | `insertPosition` is past the end of the routine; N appends. The error names the routine, its rung count N and the requested values. Nothing was staged or imported. | Read the routine with `logix_get_routine` and use a position from 0 to N. |
| `ReplaceCountOutOfRange` | `insertPosition + replaceCount` reaches past the end of the routine. The error names the routine, its rung count N and the requested values. Nothing was staged or imported. | Lower `replaceCount` or the position so the replaced rungs exist. |
| `PendingZoneExists` | `target="live"` was refused because the program already has a pending edit zone, or its state could not be read. Nothing was imported. | Accept or cancel the zone first (`logix_accept_pending_edits` / `logix_cancel_pending_edits`, or in Logix Designer under Logic → Online Edits), then retry. |
| `PendingEditsPresent` | A `target="controller"` import was blocked because a `logix_pending_edits` read shows an existing edit zone. Nothing was imported. | Resolve the zone (accept or cancel), then publish again. |
| `PendingEditsBlockImport` | The program holds edits that are not yet assembled, so the SDK refuses any import into it (`XMLSrv_E_PIE_CONTROLLER_EDITS_EXIST_FOR_PROGRAM`). The kind is the same whether the pre-check or the SDK found it, and for `logix_create_routine`. Nothing was imported. | Assemble or cancel the edits (`logix_assemble_pending_edits` / `logix_cancel_pending_edits`, or in Logix Designer under Logic → Online Edits), then publish again. |
| `NothingPending` | A cancel or accept of pending edits was refused because `logix_pending_edits` reports `state='none'` for the routine (for cancel, none of the program's routines holds edits either). No Studio command was sent. | Nothing to do; read `logix_pending_edits` if you expected edits. |
| `ControllerLocked` | Another client holds an exclusive lock on the controller, for example while it is importing (`RxCsE_ASA_BLOCKING_HIGHER_EXCL_LOCK`). The server retried the import up to 3 tries over about 10 s before reporting this; `retries` says how many retries were made. Nothing was imported. | Another client is importing; retry in a few seconds. |
| `ControllerBusy` | A `target="controller"` publish failed with `RxE_ILLEGAL_OPERATIONS` (`80042021`, "Sequence of operations not allowed"), the importer named no rung-level reason, and the program holds no pending edits afterwards: another client was editing at the same moment. Carries `retryAfterSeconds`. Nothing was imported. (If the program does hold pending edits, the kind is `PendingEditsBlockImport`; if the importer names a rung-level reason, it is `ImportRejectedByImporter`.) | Retry after `retryAfterSeconds`; if it fails again, check `logix_pending_edits` and whether Studio or another server is editing the program. |
| `AmbiguousTagName` | `logix_verify_interlock` or `logix_cross_reference` was given a bare tag name that matches program-scoped tags in two or more programs, and no controller tag has that name. The error lists the qualified names in `matches`; the matches are never merged. Nothing was searched. | Re-run with one of the qualified names, for example `\\P1.Gate`. A controller tag of the same name resolves without this error. |
| `EmptyRoutineEnvelope` | The `l5xFragment` has a self-closed `<Routine/>` (or one with no `<RLLContent>`), which carries no rungs. Nothing was imported. | Pass the rungs with `rungs`, or open the routine element with an `<RLLContent>` holding the `<Rung>` elements. |
| `StagedRungsPresent` | `target="live"` was refused because this session has staged rungs (`target="workstation"`). A live import finalizes immediately and cannot carry a staged rung set. Staged tags are allowed. | Publish the staged rungs with `target="controller"` and resolve the zone, or discard them, then retry. |
| `StaleTarget` | `target="live"` replaces or deletes rungs, and a rung no longer matches `expectedRungText`, or the routine could not be exported to check. The same kind refuses an `undo` block whose rungs have changed since it was issued. Nothing was imported. | Re-read the routine with `logix_export_rungs`, re-derive the edit and retry with fresh text. |
| `ExpectedRungTextRequired` | `target="live"` with `replaceCount` above 0 needs one `expectedRungText` entry per replaced rung. | Pass the text of each rung you intend to replace, in rung order. |
| `DeleteNeedsLive` | A rung delete (`rungs: []`) was requested with a target other than `live`. No zone was created. | Use `target="live"`, with `expectedRungText`. |
| `DeleteWithTags` | A rung delete was combined with `tags`. Nothing changed. | Delete the rungs in one call and declare tags in another. |
| `DeleteNeedsExplicitEmptyRungs` | `target="live"` with `replaceCount` above 0 but no new rungs: omitting `rungs` is not a delete and the call would change nothing. Nothing was imported. | To delete, pass an explicit `rungs: []` with `replaceCount` and `expectedRungText`; to replace, pass the new rungs. |
| `LiveFinalizeNotConfirmed` | The live import returned without error, but re-exporting the routine did not show the expected clean state. The response carries `beforeRungs` and `afterRungs`. | Do not assume the edit is live. Inspect with `logix_export_rungs` and `logix_pending_edits` before retrying. |
| `PublishMismatch` | After a `target="controller"` import the pending rungs read back do not match what was staged. The response lists `expectedRungs` and `pendingRungs`. The result is never reported as `proposed`. | Review the pending edits in Logix Designer (Logic → Online Edits). Do not accept or assemble until the difference is understood; cancel if the edit is wrong. |
| `PublishMismatchUnresolved` | `logix_test_pending_edits` or `logix_assemble_pending_edits` was called after a `PublishMismatch` that is still unresolved for the program. No command was sent. | Review the zone with `logix_pending_edits`: it clears when the zone holds every staged rung. Or discard the zone with `logix_cancel_pending_edits`, then re-stage and publish. |
| `ImportRejectedByImporter` | The SDK importer rejected a reference in the fragment (`XMLSrv_E_IMPORT_ABORTED_NO_CHANGES`). No changes were made on the controller. | Fix the fragment (declare the tags it uses) and publish again. |
| `BuildFailedEditPending` | The offline-copy build check really failed after an import that went to the controller. The edit is on the controller and was **not** rolled back; the pending rungs are listed. | For `controller`: cancel in Logix Designer or with `logix_cancel_pending_edits`, fix the rung and publish again. For `live`: correct or undo the change with another `logix_propose_rung_edit`. |
| `RungSyntaxError` | A workstation-stage import was refused because the importer's log carries the rung-text scanner's syntax error, for example `XIC(A)[OTE(B);` (an unclosed branch). The error carries the log's `line` and `column` and the offending text. Nothing was staged. | Fix the rung text at the position named. |
| `OperandCountMismatch` | The pre-check found a common instruction (`XIC`, `XIO`, `OTE`, `OTL`, `OTU`, `ONS`, `TON`, `TOF`, `RTO`, `CTU`, `CTD`, `RES`, `MOV`, `MOVE`, `ADD`, `SUB`, `MUL`, `DIV`, `EQU`, `NEQ`, `GRT`, `GEQ`, `LES`, `LEQ`, `JSR`, `RET`, `CPT`, `CLR`) with the wrong number of operands, for example `XIC(A,B)`. Nothing was staged. A name that is not a known instruction or Add-On Instruction is not refused: it is reported as an `UnknownInstruction` warning. | Write the instruction with the operands it takes. |
| `EnvelopeIncomplete`, `AmbiguousInput`, `InvalidRungText`, `InvalidTag` | The fragment or the typed `rungs` / `tags` arguments are malformed. Nothing was imported. | Correct the argument named in the message. |
| `ExternalAccessChangeRefused` | The fragment would change a tag's External Access setting, which this tool never does. Nothing was imported. | Change External Access in Logix Designer. |

A build check that cannot run is not an error. On Logix Designer 36 the SDK's
build is not supported: the publish succeeds and reports
`buildVerify: "unavailable"` with the `designerVersion`; Studio's own
verification still runs when you accept or assemble.

### Pending-state errors (`logix_pending_edits`, finalize, accept, test, untest, assemble, cancel)

| Tool | `kind` | Meaning | Typical fix |
|---|---|---|---|
| `logix_finalize_edits` | `PendingZoneStudioOnly` | The program already has a pending edit zone. The SDK has no accept, assemble or cancel call for an existing zone, so this tool never calls it. Nothing changed. | Accept or cancel the zone with the Studio lifecycle tools (with `LGX_ENABLE_STUDIO_AUTOMATION=1`) or in Logix Designer. |
| `logix_cancel_pending_edits` | `ZoneTested` | The zone is tested, and Studio disables Cancel Accepted while it is. No command was sent. | Call `logix_untest_pending_edits`, then cancel. |
| `logix_cancel_pending_edits` | `CancelNotApplicable` | Studio showed no confirmation dialog although the zone is not tested. | Look for an open Studio dialog, then retry. |
| `logix_assemble_pending_edits`, `logix_untest_pending_edits` | `NotTested` | The zone is not tested, so there is nothing to assemble or untest. No command was sent. | Call `logix_test_pending_edits` first, or `logix_accept_pending_edits` to finalize in one step. |
| `logix_test_pending_edits` | `AlreadyTested` | The zone is already tested. No command was sent. | Read behavior with `logix_read`, then assemble or untest. |
| `logix_test_pending_edits` | `TestStateNotConfirmed` | The command was posted, but a re-read of `zoneState` did not show `tested`. The response carries the before and after states. | Read `logix_pending_edits` to see the actual state. |
| `logix_untest_pending_edits` | `UntestNotApplicable`, `UntestStateNotConfirmed` | As above, for untest. | Look for an open Studio dialog; read `logix_pending_edits`. |
| the Studio tools | `CapabilityDisabled` | The Studio automation switch is off. The response names `LGX_ENABLE_STUDIO_AUTOMATION` and says that the tool also needs `LGX_ENABLE_LIVE_WRITES`. No Studio action was taken. | Set the named variables only if you intend the operation, then restart the server. |
| the Studio tools | `StudioNotRunning`, `StudioOffline` | No Studio window was found, or the window title shows the project offline. The tools never launch Studio or take it online. No command was sent. | Open the project in Studio and go online by Upload, then retry. |
| the Studio tools | `StudioInOtherSession` | Studio runs only in another Windows session than the server, so it cannot be driven. Nothing was launched. | Run the server in the same Windows session as Studio. |
| the Studio tools | `StudioElevated` | Studio is elevated and the server is not. Nothing was launched. | Run Studio and the server at the same privilege level. |
| the Studio tools | `StudioWindowNotFound` | Logix Designer windows are open, but none is titled for the session's controller (and project file). | Open the session's project in Studio, online, then retry. |
| the Studio tools | `StudioWindowAmbiguous` | More than one Logix Designer window matches the session's controller and project. | Close the extra windows, then retry. |
| the Studio tools | `StudioModalOpen` | A dialog is open in Studio. No command was sent. | Dismiss the dialog in Studio, then retry. |

Success is never inferred from the posted command alone: accept, assemble and
cancel report success only when a re-read shows no pending zone, and test and
untest only when `zoneState` shows `tested` or `untested`.

## Skills errors

| Tool | `kind` | Meaning |
|---|---|---|
| `logix_get_skill` | `SkillNotFound` | The requested name is not a shipped skill. The response lists `validNames`. |

## Connect errors (`logix_connect`)

| Condition | What it means |
|---|---|
| Missing required argument (for example no `gateway` for `cip`) | The connect call is incomplete for the backend requested. `MissingGateway`: the `hint` names the parameter. |
| Malformed `gateway` or `path` for `cip` | The value contains `&`, `=`, a control character or whitespace (a single space between path segments is allowed). `InvalidArgument`: refused before any controller call; the `parameter` field names the offender. |
| Unknown backend / bad parameter combination | `backend` is not one of `cip`, `l5x` or `virtual`, or an argument was supplied that the chosen backend does not use. |
| Connection failed (`ConnectionFailed`) | The controller does not answer on the EtherNet/IP port at all. The error text explicitly says this is *not* a comm-path formatting problem when the path itself parses correctly, to avoid sending you down the wrong debugging path. |
| Project file missing (`ProjectFileMissing`) | `logix_connect` found a registered project whose file is gone from disk (an upload's copy is removed when its session closes). Nothing was connected. The `hint` names the file. Call `logix_connect` with `sdkMode="online"` and a comm path to upload again, or `logix_open_project` with a path. |
| No project loaded (`NoProjectLoaded`) | `logix_connect` needs a loaded project (`backend="virtual"`, `backend="l5x"`, or an offline `backend="sdk"` connect) and none is loaded. Nothing was connected. The `hint` names the next steps: call `logix_open_project` with a path, or for the SDK call `logix_connect` with `sdkMode="online"` and `sdkCommPath` or `gateway` to upload the controller's project. |

Two kinds are specific to the CIP backend. `CipPathInvalid` means connect
was given an empty or omitted `path` and the probe read failed: ControlLogix
and CompactLogix 5x80 controllers usually need `1,<slot>` (for example `1,0`);
no path is assumed for you, and no session is left behind. `ConnectionFailed`
is returned when a path was given but the probe still failed, including when
the probe hits its hard 15-second deadline (the message then reads "timed out
connecting ... after N s" and names the gateway and path). The server keeps
answering after a timeout. `CipRuntimeMissing` is returned instead when the
CIP library's native component cannot load because the Visual C++ runtime is
missing; no controller was contacted, so check the runtime, not the controller.
Connect fails fast rather than reporting `connected` and failing on first use.

## Session errors

No active session (a read, write or poll before `logix_connect`) returns the
message "No active session. Call logix_connect first." with `kind`
`NoActiveSession`. The tools that need a live session return the same kind
(`logix_controller_mode`, `logix_diagnose` and
`logix_diff_against_controller`).

Out-of-band controller changes are reported by these kinds:

| `kind` | Meaning | Typical fix |
|---|---|---|
| `ControllerChanged` | The controller behind the session changed outside this session in a way the session image cannot follow: a different controller name or serial (the error gives name and serial before and after), or a `project re-downloaded` (routines the image holds are gone). Image readers (`logix_get_routine`, `logix_lint`, `logix_cross_reference`, `logix_document`, `logix_diff_against_controller` and the other session-image reads) refuse; live tag reads and `logix_pending_edits` keep working. | Call `logix_connect` again; staged rungs of this session are discarded on reconnect. |
| `ForeignPendingZone` | A refusal because the program's pending zone is a pending zone this server did not publish (it was staged by another tool or person). Nothing was accepted. | Review the zone with `logix_pending_edits`; accept or cancel it where it was made, or cancel it with `logix_cancel_pending_edits`. |

A session-image result that follows a refresh forced by an out-of-band change carries
`imageRefreshedBecause: "controller changed outside this session"` on the first read after the refresh.
`logix_pending_edits` reports `fingerprintAgeMs`, the age of the last controller fingerprint probe.

## The gate-only path is always available

Static analysis, gate derivation, and interlock proof run entirely against the
open project file, with **no controller and no live write path at all**. If you
are only seeing errors on the live paths, confirm `LGX_ENABLE_LIVE_WRITES` is
set the way you intend before assuming something else is broken.
