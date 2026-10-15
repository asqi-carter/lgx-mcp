# Testing: global static state audit

Audit for epic "Eliminate flaky `make test` by isolating global static state in tests".
Audit only: no production or test code was changed by this task. Follow-up tasks apply the fixes below.

## Why `make test` is flaky

xunit runs test classes in different collections in parallel, in one process. A class with no
`[Collection]` is its own collection, so it runs in parallel with every other class. Anything that
writes a process-wide static (a static property or field, a process environment variable,
`Console.SetError`) can therefore change what another running test sees.

Existing mitigations, and why they are not enough:

| Mechanism | Where | Gap |
|---|---|---|
| `[CollectionDefinition("StaticHandlerHooks", DisableParallelization = true)]` | `StaticHandlerHooksCollection.cs` | Only 3 classes join it. Other classes write the same hooks (see section 2). |
| `[CollectionDefinition("ProcessEnvironment", DisableParallelization = true)]` | `ProcessEnvironmentCollection.cs` | Only `DoctorHandlerProcessEnvironmentTests` joins it. Seven other classes call `SetEnvironmentVariable` (section 3). |
| `[Collection("StudioConfirmWait")]` | `StudioConfirmWaitTests.cs` | **No `[CollectionDefinition]` exists.** It only serialises that one class with itself, and does not stop other classes from using `StudioConfirmWait` in parallel. |
| `[Collection("AuditLoggerDir")]` | 4 classes | **No `[CollectionDefinition]`.** Same gap. The log directory itself is `AsyncLocal` (safe); the other statics in section 4 are not. |
| `AsyncLocal` overrides | `TagRoute`, `GateConfigResolver`, `AuditLogger` (log dir, rotation, move, fail-closed, failure counter), `TrafficLogger` log dir | Safe per execution context. Not listed as sites below, but they fall back to the process env var when no override is set (section 3, readers). |

`xunit.runner.json` and `ParallelizeTestCollections` are not set anywhere, so parallelism is on.

## Fix choices

- **(a) Injectable instance**: production code takes the state as a constructor or method argument
  (default = the shared production instance); tests pass their own. Removes the sharing, so the test
  needs no collection.
- **(b) Non-parallel collection**: one `[CollectionDefinition(..., DisableParallelization = true)]`;
  every class that writes **or reads** the state joins it. A collection with
  `DisableParallelization = true` runs alone, so it also excludes unrelated readers. Reader-side
  membership is only needed for a reader that asserts on a value the writer sets.

Rule applied: choose (a) when the state is reached indirectly from many test classes (a collection
would have to cover all of them), or when the type already takes constructor arguments. Choose (b)
when the state is a true process global (env vars, `Console`, module-initializer registration) and
the number of writers is small.

## 1. Sites called out in the task

### 1.1 `StudioConfirmWait` (`src/LogixMcp/StudioConfirmWait.cs`) - fix (a)

State: `static ConcurrentDictionary LastCall`, `static Func<DateTimeOffset> Now`, `Reset()`.

Direct writers (tests):
- `StudioConfirmWaitTests` (ctor and `Dispose` set `Now` and call `Reset()`; all 5 tests).

Indirect writers (production code reached from tests):
- `LogixSdkTools.ProposeRungEdit` -> `StudioConfirmWait.Record` (`LogixSdkTools.cs:755`), when the
  result status is `proposed` or `live`. Reached by any test that runs a successful
  `ProposeRungEdit` through `LogixSdkTools`: `ProposeRungEditHandlerTests`, `PublishModeTests`,
  `ProposeRungEditLiveTests`, `ProposeRungEditNormalizationTests`, `ProposeRungEditTypedInputTests`,
  `PublishStagedSetTests`, `RungEditAuditTests`.
- `LogixSdkTools.RunLifecycleDriverAsync` -> `SecondsFor` and `Record` (`LogixSdkTools.cs:1823`,
  `1833`). It is the default runner for `CancelPendingEdits`, `AcceptPendingEdits`, `TestPendingEdits`,
  `UntestPendingEdits` and `AssemblePendingEdits` (`LogixSdkTools.cs:1451`, `1624`, `1805`). Most tests
  pass their own `session1Runner`, which bypasses it; any that do not reach the shared dictionary.
  Check `*PendingEditsToolTests`, `StudioLifecycleGatingTests` and `StudioWindowTargetingTests`.
- The candidate lists above come from a text search for `LogixSdkTools` and `ProposeRungEdit` in the
  tests, not from tracing each test. Some `ProposeRungEdit*` classes sit behind `<Compile Remove>` or
  `#if LOGIX_SDK`. Confirm the real set when implementing (see Verification).

Why (a): `StudioConfirmWaitTests` swaps the global clock and clears the dictionary while publish
tests in other collections call `Record`. Putting all of those classes in one serial collection would
serialise most of the SDK test suite. Fix: make `StudioConfirmWait` an instance class
(`Now` and `LastCall` as instance members), give `LogixSdkTools` a constructor parameter (default =
a shared production instance), and have `StudioConfirmWaitTests` build its own instance.

### 1.2 `DoctorEnvironment.SdkObservations` (`src/LogixMcp/DoctorEnvironment.cs:47`) - fix (a)

State: `public static IDoctorSdkObservations? SdkObservations { get; set; }`.

Writers:
- Test: `DoctorAvailabilityTests` (`SdkObservations = null`, restored in `finally`, L55-82). No collection.
- Production: `SdkConnectFactory.Initialize()` (`SdkConnectFactory.cs:1591`), a `[ModuleInitializer]`
  that `SdkAdapterLoader.TryLoad` also invokes by reflection. Any test that calls `TryLoad` or
  `InvokeInitializer` re-registers it (see 2.4).
Readers: `DoctorEnvironment` instances used by `DoctorHandler` tests, `SdkServerFaultedSessionTests`
(`new DoctorSdkObservations().GetSession` is an instance, so it is not a write).

Why (a): `DoctorEnvironment` already takes `tagSessions`, `probe`, `project` in its constructor. Add
an optional `IDoctorSdkObservations? observations` parameter; keep the static only as the default the
adapter registers at startup. `DoctorAvailabilityTests` then passes `null` or a stub, with no write.

### 1.3 `ConnectState` (`src/LogixMcp/ConnectState.cs`) - fix (a)

`ConnectState` instances are already per `LogixTagTools` (`connectState ?? new ConnectState()`,
`LogixTagTools.cs:64`), and the connect tests use `new ConnectState()`. The process-global part is
the static `ConnectState.SyncBudgetFromEnvironment()`, which reads `LGX_CONNECT_SYNC_BUDGET_S`
(`ConnectState.cs:151`) and is called from `LogixTagTools.cs:575` on every budgeted connect.

Writers:
- `ConnectBudgetTests.SyncBudgetFromEnvironment_ReadsTheVariable` (L265): sets "7" and null.
- `TasksTests.TaskClient_KeepsTaskPath_EvenWithZeroSyncBudget` (L216): sets "0" for the whole
  in-process server rig. This is the worst case; the rig runs connects while other classes run.
Indirect readers: every test that connects through `LogixTagTools` with the `sdk` backend and no
Tasks client (the budgeted path).

Why (a): inject the budget. `ConnectState` gets a `Func<TimeSpan>` / `TimeSpan` syncBudget
constructor argument (default `SyncBudgetFromEnvironment`), and `LogixTagTools` reads it from its own
`_connectState`. `TasksTests` then builds its rig with a zero budget and does not touch the process
environment. `ConnectBudgetTests` keeps testing the pure `ResolveSyncBudget(string?)`; the one test
that must read the real env var joins the `ProcessEnvironment` collection (b).

### 1.4 `LGX_*` environment reads

See section 3 for the full table.

## 2. Static hooks (settable static properties)

All are `public static ... { get; set; }` delegates or factories, replaced by tests and put back in
`finally`. The save/restore is not atomic across threads.

| Static | Test writers (file: tests) | In `StaticHandlerHooks`? | Fix |
|---|---|---|---|
| `CloseProjectHandler.SdkSessionInspector` / `SdkSessionDisposer` (`CloseProjectHandler.cs:120,136`) | `SdkServerFaultedTests` (8 tests); `StagedBufferHonestOutcomesTests` (2 tests); **`ProjectHandlerTests`** (about 10 close-project tests, L1294-1581) | First two yes. `ProjectHandlerTests` **no** | (a) pass inspector/disposer to `CloseProjectHandler` as arguments (default = static). Interim (b): add `ProjectHandlerTests` to `StaticHandlerHooks`. |
| `OpenProjectHandler.AcdConverter` (`OpenProjectHandler.cs:142`) | `ProjectHandlerTests` (L239-499), `AgentFacingTextTests` (L339) | No | (a) pass converter to `OpenProjectHandler`. Interim (b) as above. |
| `ConnectHandler.DefaultSdkFactory` (`ConnectHandler.cs:929`) | `ErrorHintTests` (L49), `SdkAdapterLoaderTests` (L1259, 1369), `LogixTagToolsTests` (L3825) | No | (a) `LogixTagTools` already receives a factory; make the handler use that, not the static. Interim (b). |
| `SdkImportDelegates.SdkImportRunner`, `ReexportReader`, `BuildRunner`, `StudioLockChecker` (`SdkImportDelegates.cs`) | `ImportDatatypeWiringTests` (ctor and `Dispose`, 4 hooks) | No | (a) The handler functions already take `buildRunner:` / `reexportReader:` arguments in `ImportDatatypeTests` and `HardwareTests`; finish this for `SdkImportRunner` and `StudioLockChecker`, so the wiring tests stop writing the statics. |
| `StudioLifecycleHandler.AfterSettleInterval` (`StudioLifecycleHandler.cs:745`) | `Cancel/Accept/Test/UntestPendingEditsToolTests` (set to zero, restore in `finally`) | No | (a) pass the settle interval as a parameter. `AfterSettleAttempts` (L742) is a static setter with no test writer found. |
| `LogixTools.CurrentTarget` (`Program.cs:198`) | `TargetRevisionTests` (L368) | No | (b) one test, one static; a small `StaticHandlerHooks` membership is enough. Production sets it in `Program.cs:100`. |

### 2.4 Registration by the adapter (indirect writer)

`SdkConnectFactory.Initialize()` (`SdkConnectFactory.cs:1586-1591`) writes **all** of:
`DefaultSdkFactory`, `AcdConverter`, `SdkSessionInspector`, `SdkSessionDisposer`, the four
`SdkImportDelegates` hooks (through `SdkImportDatatypeRunner.Register`), and
`DoctorEnvironment.SdkObservations`. It runs as a `[ModuleInitializer]` and from
`SdkAdapterLoader.TryLoad` (reflective `InvokeInitializer`). `SdkAdapterLoaderTests`
(`TryLoad_*`, `InvokeInitializer_SetsDefaultSdkFactory_*`) therefore overwrite every hook above as a
side effect. Fix (b): put `SdkAdapterLoaderTests` in the same serial collection as the hook writers
(the `StaticHandlerHooks` collection). The long-term (a) fix is the per-handler injection in the
table, after which `Initialize()` only changes the production defaults.

`SdkAdapterLoader.LoadFailureReason` (`SdkAdapterLoader.cs:110`, `private set`) is written by
`TryLoad` and read by `DoctorHandlerTests` and `LogixTagToolsTests`. Same collection, fix (b).

## 3. Process environment variables

Writers (tests that call `Environment.SetEnvironmentVariable`). Only the last row is in the
`ProcessEnvironment` collection:

| Test class (methods) | Variable(s) | Collection | Fix |
|---|---|---|---|
| `ConnectBudgetTests.SyncBudgetFromEnvironment_ReadsTheVariable` | `LGX_CONNECT_SYNC_BUDGET_S` | none | (a) per 1.3; the remaining read test goes to `ProcessEnvironment` |
| `TasksTests.TaskClient_KeepsTaskPath_EvenWithZeroSyncBudget` | `LGX_CONNECT_SYNC_BUDGET_S` | none | (a) per 1.3 |
| `ControllerDownloadGatingTests.FromEnvironment_ReadsControllerDownloadFlag` | `LGX_ENABLE_CONTROLLER_DOWNLOAD` (via `Capabilities.FromEnvironment`) | none | (b) join `ProcessEnvironment`. `Capabilities.FromEnvironment()` is the one function that reads the env; the other tests construct `Capabilities` directly. |
| `AuditLoggerTests.FailClosed_EnvVar_UsesTruthyParser` | `LGX_AUDIT_FAIL_CLOSED` | `AuditLoggerDir` (not serial) | (b) join `ProcessEnvironment`. Other tests already use `AuditLogger.OverrideFailClosed` (`AsyncLocal`). |
| `SdkAdapterLoaderTests` (about 30 probe and `TryLoad` tests) | `LOGIX_SDK_PATH` | none | (b) join `ProcessEnvironment` (and the hook collection for 2.4, see below) |
| `LogixTagToolsTests` (4 `ConnectHandler_SdkBackend_*` tests, L1440-1586) | `LOGIX_SDK_PATH` | none | (b) join `ProcessEnvironment`. These read the env through `SdkAdapterLoader.CollectProbeResults`. |
| `IntegrationSmokeTests` (6 `IntegrationFixture_*` tests) | `AB_SERVER_HOST`, `AB_SERVER_PATH` | none | (b) join `ProcessEnvironment`. They read the same variables that `IntegrationFixture` reads for every `[Collection("Integration")]` test. |
| `DoctorHandlerProcessEnvironmentTests.ServerCheck_IgnoresProcessEnvironment` | `LGX_GATE_COMMAND_PREFIXES`, `LGX_WRITE_ALLOWLIST` | `ProcessEnvironment` | already (b) |

Because `SdkAdapterLoaderTests` needs both `ProcessEnvironment` and the hook collection, merge the two
into a single serial collection (suggested name `ProcessGlobals`). One collection is simpler than two
because a class can belong to only one.

Readers (production code that reads `LGX_*` at call time, reached indirectly). These are safe
only while no writer above runs in parallel, which is why writers must be serial:

| Reader | Variable(s) | Reached from |
|---|---|---|
| `Capabilities.FromEnvironment()` (`Capabilities.cs:145`) | `LGX_ENABLE_LIVE_WRITES`, `LGX_ENABLE_STUDIO_AUTOMATION`, `LGX_PUBLISH_MODE`, `LGX_ENABLE_LIVE_FINALIZE`, `LGX_ENABLE_CONTROLLER_DOWNLOAD`, plus `LGX_WRITE_ALLOWLIST` | `Program.cs:26,192`, `LogixDoctorTools.cs:46` (when no capabilities are injected), ping |
| `WriteAllowlist.PathFromEnvironment()` (`WriteAllowlist.cs:125`) | `LGX_WRITE_ALLOWLIST` | `Capabilities.FromEnvironment`, `DoctorEnvironment.WriteAllowlistRaw` |
| `GateConfigResolver.Current` / `FromEnvironment()` (`GateConfigResolver.cs:78`) | `LGX_GATE_COMMAND_PREFIXES` (after the `AsyncLocal` override) | `ConnectHandler.cs:1139,1690,1955,2264`, `LogixSdkTools.cs:494,694`, `ProposeRungEditHandler.cs:2530`, `WriteClassifier.cs:745`, `GateReportHandler`, `AuditLogger.LogConnect` |
| `TagRoute.Current` (`TagRoute.cs:70`) | `LGX_TAG_ROUTE` (after the `AsyncLocal` override) | callers not traced in this audit (no use found outside `TagRoute.cs` by text search of `src/`); tests set it through the override, not the env |
| `AuditLogger.MaxMb`, `Keep`, `FailClosed` (`AuditLogger.cs:202,220,319`) | `LGX_AUDIT_MAX_MB`, `LGX_AUDIT_KEEP`, `LGX_AUDIT_FAIL_CLOSED` | `WriteTagHandler.cs:348`, `PingHandler.cs:146`, `DoctorHandler.cs:439` |
| `ConnectState.SyncBudgetFromEnvironment()` | `LGX_CONNECT_SYNC_BUDGET_S` | `LogixTagTools.cs:575` |
| `SdkAdapterLoader.GetProbeCandidates` (`SdkAdapterLoader.cs:375`) | `LOGIX_SDK_PATH` | `ConnectHandler.cs:1415` (`CollectProbeResults`), `DoctorEnvironment.cs:101` |

Tests that only **mention** `LGX_*` names in assertions (`ScriptEnvVarTests`, `SafetyClaimsTests`,
`PublicDocsScopeTests`, `PublishModeTests`, `SetupMcpClientTests`, `StudioLifecycleGatingTests`, ...) do not
set the variables: they pass `Capabilities` instances or check text. No action needed. Re-verify
with `grep -rn SetEnvironmentVariable tests/` (the list above is complete as of this audit: 8 files).

## 4. Other process-wide statics

| Site | Writers | Fix |
|---|---|---|
| `AuditLogger.LastConnect` (`AuditLogger.cs:599`, `private set`) and `LastSdkServiceDiagnosis` (`:738`) | Written by every `AuditLogger.LogConnect(..., phaseTimings)` / `LogSdkServiceDiagnosis`. Reached from `ConnectHandler` in any connect test. Read by `ConnectPhaseTimingsTests` and doctor tests. Not `AsyncLocal`. | (a) make these `AsyncLocal`-overridable like the log dir (`OverrideLogDir` pattern), or move them to an injectable `IAuditStatus`. Readers that assert on them join `AuditLoggerDir`; that collection needs a real `[CollectionDefinition(DisableParallelization = true)]` for (b). |
| `AuditLogger._globalFailures`, `_rotationFailureLogged` (`AuditLogger.cs:175,290`) | Any audit append failure in any test when no `_failuresOverride` is set | (a) existing `_failuresOverride` covers tests that set it; the rest need `AuditLoggerDir` made serial (b). |
| `Console.SetError` (`AuditLoggerTests.AppendFailure_StderrAndDoctorCount`, L1491-1503) | Redirects stderr for the whole process; any parallel test that writes to stderr is captured | (b) join the serial collection. |
| `LibPlcTagNativeInit._done`, `ExtractedLibraryPath` (`LibPlcTagNativeInit.cs:80,86`) | One-time native extraction, written by `EnsureInitialized()`. Tests (`LibPlcTagNativeInitTests`) call the pure `ExtractVerified`. | No change needed: idempotent, no test writes it. |
| `TrafficLogger._logDirOverride` | `AsyncLocal` | No change needed. |

## Summary of choices

| Site | Fix |
|---|---|
| `StudioConfirmWait` (`Now`, `LastCall`, `Reset`) incl. indirect `LogixSdkTools` Record/SecondsFor | **(a)** injectable instance |
| `DoctorEnvironment.SdkObservations` | **(a)** constructor parameter |
| `ConnectState.SyncBudgetFromEnvironment` / `LGX_CONNECT_SYNC_BUDGET_S` | **(a)** injected budget |
| `CloseProjectHandler.SdkSessionInspector/Disposer`, `OpenProjectHandler.AcdConverter`, `ConnectHandler.DefaultSdkFactory`, `SdkImportDelegates.*`, `StudioLifecycleHandler.AfterSettleInterval` | **(a)** per-handler parameters; **(b)** `ProcessGlobals` collection until then |
| `LogixTools.CurrentTarget` | **(b)** |
| `SdkConnectFactory.Initialize()` registration via `SdkAdapterLoaderTests`, `LoadFailureReason` | **(b)** |
| `LGX_ENABLE_CONTROLLER_DOWNLOAD`, `LGX_AUDIT_FAIL_CLOSED`, `LOGIX_SDK_PATH`, `AB_SERVER_HOST/PATH` writers | **(b)** `ProcessGlobals` (merge of `ProcessEnvironment` and `StaticHandlerHooks`) |
| `AuditLogger.LastConnect`, `LastSdkServiceDiagnosis`, failure counters, `Console.SetError` | **(a)** where an `AsyncLocal` override exists or is added; **(b)** otherwise |

## Verification (not run in this audit)

This audit is a source search; it did not run the suite. pending verification: confirm each indirect
site by running the affected classes with a recording hook, e.g.
`dotnet test --filter "FullyQualifiedName~ProposeRungEdit|FullyQualifiedName~PendingEdits"` after temporarily
adding a counter to `StudioConfirmWait.Record`, and `grep -rn "SetEnvironmentVariable\|\.Now = \|Console.Set" tests/`
to re-check the writer list.

## Status

Applied: `StudioConfirmWait` is an instance (`StudioConfirmWait.Shared` in production; `LogixSdkTools` takes an optional instance; `StudioConfirmWaitTests` uses its own). `DoctorEnvironment` takes an optional `observations` argument (`DoctorEnvironment.NoObservations` = no adapter), so `DoctorAvailabilityTests` no longer writes `SdkObservations`. Classes that write hook statics or env vars now join the `StaticHandlerHooks` / `ProcessEnvironment` collections, and `AuditLoggerDir` has a `[CollectionDefinition]` with `DisableParallelization`. `GlobalStateGuardTests` checks env vars, stderr and the shared tracker after the run. Serial collections never overlap each other or parallel ones, so a separate merged `ProcessGlobals` collection is not needed. Not done (still fix (b) only): per-handler injection for `CloseProjectHandler`/`OpenProjectHandler`/`ConnectHandler` hooks, `ConnectState` budget.
