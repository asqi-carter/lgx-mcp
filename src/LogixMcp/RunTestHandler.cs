using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

// ── Input types ───────────────────────────────────────────────────────────────

/// <summary>A tag assignment inside a test case (<c>setup</c> or a step's <c>set</c>).</summary>
/// <param name="Tag">Controller-scoped tag name.</param>
/// <param name="Value">JSON value to write: boolean, number or string.</param>
public sealed record RunTestSet(
    [property: JsonPropertyName("tag")]   string      Tag,
    [property: JsonPropertyName("value")] JsonElement Value);

/// <summary>An assertion on a tag value; exactly one of <c>Equals</c>, <c>Min</c>, <c>Max</c> is usual, all given must hold.</summary>
/// <param name="Tag">Controller-scoped tag name to read.</param>
/// <param name="EqualsValue">Expected exact value (boolean, number or string).</param>
/// <param name="Min">Inclusive lower bound for numeric tags.</param>
/// <param name="Max">Inclusive upper bound for numeric tags.</param>
public sealed record RunTestExpect(
    [property: JsonPropertyName("tag")]    string       Tag,
    [property: JsonPropertyName("equals")] JsonElement? EqualsValue = null,
    [property: JsonPropertyName("min")]    double?      Min    = null,
    [property: JsonPropertyName("max")]    double?      Max    = null);

/// <summary>One step of a test case: set tags, wait, then check expectations.</summary>
/// <param name="Set">Tags to write at the start of the step.</param>
/// <param name="WaitMs">Settle time after the writes; default 200, clamped to [0, 10000].</param>
/// <param name="Expect">Assertions evaluated after the wait.</param>
public sealed record RunTestStep(
    [property: JsonPropertyName("set")]    IReadOnlyList<RunTestSet>?    Set    = null,
    [property: JsonPropertyName("waitMs")] int?                          WaitMs = null,
    [property: JsonPropertyName("expect")] IReadOnlyList<RunTestExpect>? Expect = null);

/// <summary>A named scripted test case.</summary>
/// <param name="Name">Case name reported in the result.</param>
/// <param name="Setup">Tags written once before the first step.</param>
/// <param name="Steps">Ordered steps.</param>
public sealed record RunTestCase(
    [property: JsonPropertyName("name")]  string                      Name,
    [property: JsonPropertyName("setup")] IReadOnlyList<RunTestSet>?  Setup = null,
    [property: JsonPropertyName("steps")] IReadOnlyList<RunTestStep>? Steps = null);

// ── Result types ──────────────────────────────────────────────────────────────

/// <summary>Structured refusal or validation error from <see cref="RunTestHandler"/>. Nothing was written.</summary>
/// <param name="Error">Human-readable description.</param>
/// <param name="Kind">Machine-readable kind (<c>EmulatorOnly</c>, <c>IdentityUnavailable</c>, <c>CapabilityDisabled</c>, <c>ConfirmationRequired</c>, <c>LimitExceeded</c>, <c>InvalidTest</c>, <c>NoActiveSession</c>, <c>TestWriteRefused</c>).</param>
/// <param name="ProductName">The ListIdentity product name observed, for <c>EmulatorOnly</c>.</param>
/// <param name="Hint">Imperative remediation, when one is known.</param>
/// <param name="Refused">For <c>TestWriteRefused</c>: every tag the run may not write, with reason.</param>
public sealed record RunTestError(
    [property: JsonPropertyName("error")]       string  Error,
    [property: JsonPropertyName("kind")]        string  Kind,
    [property: JsonPropertyName("productName")] string? ProductName = null,
    [property: JsonPropertyName("hint")]        string? Hint        = null,
    [property: JsonPropertyName("refused")]     IReadOnlyList<TestWriteRefusal>? Refused = null);

/// <summary>One evaluated expectation.</summary>
/// <param name="Tag">Tag read.</param>
/// <param name="Observed">Value observed at check time, or null when the read failed.</param>
/// <param name="Verdict"><c>pass</c> or <c>fail</c>.</param>
/// <param name="Expected">Human-readable expectation, e.g. <c>equals true</c>.</param>
/// <param name="Error">Read or comparison failure text.</param>
public sealed record RunTestExpectResult(
    [property: JsonPropertyName("tag")]      string  Tag,
    [property: JsonPropertyName("expected")] string  Expected,
    [property: JsonPropertyName("observed")] object? Observed,
    [property: JsonPropertyName("verdict")]  string  Verdict,
    [property: JsonPropertyName("error")]    string? Error = null);

/// <summary>One executed step.</summary>
/// <param name="Set">Tags written, as <c>{tag, value}</c>.</param>
/// <param name="WaitMs">Effective (clamped) wait applied.</param>
/// <param name="Expect">Evaluated expectations.</param>
/// <param name="Verdict"><c>pass</c> or <c>fail</c>.</param>
/// <param name="Error">Write failure text, when a write was refused or failed.</param>
public sealed record RunTestStepResult(
    [property: JsonPropertyName("set")]     IReadOnlyList<object>              Set,
    [property: JsonPropertyName("waitMs")]  int                                WaitMs,
    [property: JsonPropertyName("expect")]  IReadOnlyList<RunTestExpectResult> Expect,
    [property: JsonPropertyName("verdict")] string                             Verdict,
    [property: JsonPropertyName("error")]   string?                            Error = null);

/// <summary>One tag restored to its pre-case value after a case ran.</summary>
/// <param name="Tag">Tag that was restored.</param>
/// <param name="Value">The snapshot value written back (the value the tag held before the case).</param>
/// <param name="Confirmed">True only when a read-back after the restore matched <paramref name="Value"/>.</param>
/// <param name="Error">Write or read-back failure text, when the restore was not confirmed.</param>
public sealed record RunTestRestoreEntry(
    [property: JsonPropertyName("tag")]       string  Tag,
    [property: JsonPropertyName("value")]     object? Value,
    [property: JsonPropertyName("confirmed")] bool    Confirmed,
    [property: JsonPropertyName("error")]     string? Error = null);

/// <summary>One executed case.</summary>
/// <param name="Name">Case name.</param>
/// <param name="Result"><c>pass</c> or <c>fail</c>.</param>
/// <param name="Steps">Executed steps (stops at the first write failure).</param>
/// <param name="DurationMs">Elapsed milliseconds.</param>
/// <param name="Error">Setup write failure text, when setup failed.</param>
/// <param name="OrdinaryWriteDenied">Tags this case sets that ordinary <c>logix_write</c> would deny (empty when none).</param>
/// <param name="Restored">Snapshot restore outcome for every tag the case sets; runs even when the case fails or throws.</param>
public sealed record RunTestCaseResult(
    [property: JsonPropertyName("name")]       string                          Name,
    [property: JsonPropertyName("result")]     string                          Result,
    [property: JsonPropertyName("steps")]      IReadOnlyList<RunTestStepResult> Steps,
    [property: JsonPropertyName("durationMs")] long                            DurationMs,
    [property: JsonPropertyName("error")]      string?                         Error = null,
    [property: JsonPropertyName("ordinaryWriteDenied")] IReadOnlyList<TestWriteDisclosure>? OrdinaryWriteDenied = null,
    [property: JsonPropertyName("restored")] IReadOnlyList<RunTestRestoreEntry>? Restored = null);

/// <summary>Result of a <c>logix_run_test</c> call that passed every gate.</summary>
/// <param name="Overall"><c>pass</c>, <c>fail</c>, or <c>restore_failed</c> when any restore was not confirmed (takes precedence over <c>fail</c>).</param>
/// <param name="ProductName">ListIdentity product name the emulator gate observed.</param>
/// <param name="Cases">Per-case results.</param>
/// <param name="DurationMs">Total elapsed milliseconds.</param>
/// <param name="Restored">Every restore entry of every case, in run order.</param>
/// <param name="RestoreFailedTags">Tags whose restore was not confirmed (empty when all confirmed).</param>
public sealed record RunTestResult(
    [property: JsonPropertyName("overall")]     string                         Overall,
    [property: JsonPropertyName("productName")] string                         ProductName,
    [property: JsonPropertyName("cases")]       IReadOnlyList<RunTestCaseResult> Cases,
    [property: JsonPropertyName("durationMs")]  long                           DurationMs,
    [property: JsonPropertyName("restored")]    IReadOnlyList<RunTestRestoreEntry>? Restored = null,
    [property: JsonPropertyName("restoreFailedTags")] IReadOnlyList<string>? RestoreFailedTags = null);

// ── RunTestHandler ────────────────────────────────────────────────────────────

/// <summary>
/// Pure handler for the <c>logix_run_test</c> MCP tool: scripted logic tests that run
/// against the Echo emulator only.
///
/// <para>
/// Gate order, before anything is written: active session → ListIdentity must report a product
/// starting with <c>Emulate</c> (<c>EmulatorOnly</c>; <c>IdentityUnavailable</c> when the lookup
/// fails) → <c>LGX_ENABLE_LIVE_WRITES=1</c> → <c>confirmedByUser=true</c> → size limits → test write
/// rules (<see cref="TestWriteRules"/>, checked for every tag of every case up front). Every
/// refusal returns a <see cref="RunTestError"/> and writes nothing.
/// </para>
///
/// <para>
/// Allowed writes bypass the ordinary grant list but still go through the typed SDK path: they are
/// sent to the service beneath the <see cref="GatedTagService"/> decorator (the
/// <c>TypedSdkTagService</c> in an SDK session). Each case reports which of its tags ordinary
/// <c>logix_write</c> would deny.
/// </para>
/// </summary>
public static class RunTestHandler
{
    /// <summary>Maximum number of cases per call.</summary>
    public const int MaxCases = 20;

    /// <summary>Maximum number of steps per case.</summary>
    public const int MaxStepsPerCase = 50;

    /// <summary>Default <c>waitMs</c> when a step omits it.</summary>
    public const int DefaultWaitMs = 200;

    /// <summary>Upper clamp for <c>waitMs</c>.</summary>
    public const int MaxWaitMs = 10_000;

    /// <summary>Reads the ListIdentity of <c>host</c>; overridable so tests need no network.</summary>
    /// <param name="host">Controller host.</param>
    /// <param name="ct">Cancellation token.</param>
    public delegate Task<EmulatorIdentityInfo> IdentityReader(string host, CancellationToken ct);

    /// <summary>Clamps a requested wait to <c>[0, MaxWaitMs]</c>, defaulting to <see cref="DefaultWaitMs"/>.</summary>
    /// <param name="waitMs">Requested wait, or null.</param>
    /// <returns>The effective wait in milliseconds.</returns>
    public static int ClampWaitMs(int? waitMs) =>
        Math.Clamp(waitMs ?? DefaultWaitMs, 0, MaxWaitMs);

    /// <summary>
    /// Runs the gates, then the cases. Never throws for gate, validation or tag errors.
    /// </summary>
    /// <param name="cases">Test cases (up to <see cref="MaxCases"/>).</param>
    /// <param name="confirmedByUser">Must be true: the engineer's go-ahead to write test inputs.</param>
    /// <param name="session">Active tag session; supplies the service and controller host.</param>
    /// <param name="capabilities">Process capability flags; live writes must be enabled.</param>
    /// <param name="identityReader">ListIdentity reader; defaults to <see cref="EmulatorIdentity.ReadAsync"/>.</param>
    /// <param name="delay">Wait implementation; defaults to <see cref="Task.Delay(int, CancellationToken)"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="progress">
    /// Optional receiver, called once when each case starts (<c>Completed</c> = cases finished so far)
    /// and once when the run ends (<c>Completed == Total</c>). Only reached after every gate passed;
    /// <c>null</c> reports nothing and changes no result.
    /// </param>
    /// <returns>A <see cref="RunTestResult"/> or a <see cref="RunTestError"/>.</returns>
    public static async Task<object> ExecuteAsync(
        IReadOnlyList<RunTestCase>? cases,
        bool                        confirmedByUser,
        TagSessionService           session,
        Capabilities?               capabilities   = null,
        IdentityReader?             identityReader = null,
        Func<int, CancellationToken, Task>? delay  = null,
        CancellationToken           ct             = default,
        IProgress<(int Completed, int Total, string Message)>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (!session.IsConnected)
            return new RunTestError(
                "No active session. Call logix_connect first.", ErrorKinds.NoActiveSession);

        // ── Emulator gate: a fact read from the controller, before anything else ──
        var host = session.ControllerHost;
        if (string.IsNullOrWhiteSpace(host))
            return new RunTestError(
                "Cannot establish the controller's product name: the active session has no controller address " +
                "(offline, virtual and file-backed sessions are not testable).",
                ErrorKinds.IdentityUnavailable,
#if LOGIX_SDK
                Hint: "Connect online to the Echo emulator with logix_connect (sdkMode=\"online\", sdkCommPath " +
                      "\"EmulateEthernet\\<ip>\"), then call logix_run_test again.");
#else
                Hint: "Connect to the Echo emulator with logix_connect (backend=\"cip\", gateway = the " +
                      "emulator's IP), then call logix_run_test again.");
#endif

        EmulatorIdentityInfo identity;
        try
        {
            identity = await (identityReader ?? DefaultIdentityReader)(host, ct).ConfigureAwait(false);
        }
        catch (EmulatorIdentityException ex)
        {
            return new RunTestError(
                $"Cannot establish that the controller is an emulator: {ex.Message}",
                ErrorKinds.IdentityUnavailable,
                Hint: $"Check that {host}:44818 is reachable (logix_doctor), then retry. Nothing was written.");
        }

        if (!identity.IsEmulator)
            return new RunTestError(
                $"logix_run_test runs on the Echo emulator only; the controller at {host} reports product " +
                $"name '{identity.ProductName}'. Nothing was written.",
                ErrorKinds.EmulatorOnly,
                ProductName: identity.ProductName,
                Hint: "Never run scripted tests on real hardware. Connect to an Echo emulator " +
                      "(product name starting with 'Emulate').");

        // ── Capability and confirmation ───────────────────────────────────────
        if (capabilities is not { LiveWritesEnabled: true })
        {
            AuditLogger.LogCapabilityRefusal("logix_run_test", Capabilities.EnvVar);
            return new RunTestError(
                "logix_run_test refused: LGX_ENABLE_LIVE_WRITES is not set. Nothing was written.",
                "CapabilityDisabled",
                ProductName: identity.ProductName,
                Hint: $"Set the {Capabilities.EnvVar} environment variable to 1 to enable it. " +
                      $"Client command: {Capabilities.ClientCommandHint}");
        }

        if (!confirmedByUser)
            return new RunTestError(
                "logix_run_test refused: confirmedByUser is not true. A test run writes tag values on the " +
                "controller (restoring them afterwards). Nothing was written.",
                "ConfirmationRequired",
                ProductName: identity.ProductName,
                Hint: "Ask the engineer, then call again with confirmedByUser=true.");

        // ── Limits and shape ──────────────────────────────────────────────────
        if (cases is null || cases.Count == 0)
            return new RunTestError("cases must contain at least one test case.", "InvalidTest",
                ProductName: identity.ProductName);
        if (cases.Count > MaxCases)
            return new RunTestError(
                $"Too many cases: {cases.Count} supplied, the limit is {MaxCases}. Nothing was written.",
                "LimitExceeded", ProductName: identity.ProductName,
                Hint: $"Split the run into calls of at most {MaxCases} cases.");
        foreach (var c in cases)
        {
            var steps = c.Steps?.Count ?? 0;
            if (steps == 0)
                return new RunTestError($"Case '{c.Name}' has no steps.", "InvalidTest",
                    ProductName: identity.ProductName);
            if (steps > MaxStepsPerCase)
                return new RunTestError(
                    $"Case '{c.Name}' has {steps} steps, the limit is {MaxStepsPerCase}. Nothing was written.",
                    "LimitExceeded", ProductName: identity.ProductName,
                    Hint: $"Split the case into cases of at most {MaxStepsPerCase} steps.");
        }

        // ── Test write rules: every tag of every case, before anything is written ──
        if (session.Service is not GatedTagService gated)
            return new RunTestError(
                "logix_run_test refused: the session has no write-gate policy, so it cannot be shown that " +
                "the tags to set are not written by program logic. Nothing was written.",
                ErrorKinds.TestWriteRefused,
                ProductName: identity.ProductName,
                Hint: "Open the project (logix_open_project) or connect with an SDK session that derives " +
                      "the write-gate policy, then call logix_run_test again.");

        var disclosures = new List<IReadOnlyList<TestWriteDisclosure>>(cases.Count);
        var refusals    = new List<TestWriteRefusal>();
        foreach (var c in cases)
        {
            var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var denied = new List<TestWriteDisclosure>();
            foreach (var s in SetsOf(c))
            {
                if (!seen.Add(s.Tag)) continue;
                var d = TestWriteRules.Evaluate(s.Tag, gated.Policy, session, session.WriteAllowlist);
                if (d.Refusal is { } r) refusals.Add(r);
                else if (d.OrdinaryDenial is { } o) denied.Add(o);
            }
            disclosures.Add(denied);
        }
        if (refusals.Count > 0)
        {
            foreach (var r in refusals)
                AuditLogger.LogGateDecision(r.Tag, "denied", r.Reason, "test-run: " + r.Provenance);
            return new RunTestError(
                "logix_run_test refused: " + string.Join("; ", refusals.Select(r => $"'{r.Tag}' ({r.Reason})")) +
                " may not be written by a test. Nothing was written.",
                ErrorKinds.TestWriteRefused,
                ProductName: identity.ProductName,
                Hint: "Drive the program's inputs instead of the outputs it writes, and never a tag whose " +
                      "External Access is not Read/Write; the engineer's grant is not bypassed.",
                Refused: refusals);
        }

        // ── Tag route (LGX_TAG_ROUTE): resolve before anything is written ─────
        // In cip mode with no CIP route the whole run is refused with the typed error rather than
        // reported case by case.
        if (gated.InnerService is RoutedTagService routed)
        {
            try { await routed.ResolveRouteAsync(ct).ConfigureAwait(false); }
            catch (TagServiceException ex) when (ex.Error.Kind == TagServiceErrorKind.TagRouteUnavailable)
            {
                return new RunTestError(ex.Error.Message, ErrorKinds.TagRouteUnavailable,
                    ProductName: identity.ProductName, Hint: ex.Error.Hint);
            }
        }

        // ── Run ───────────────────────────────────────────────────────────────
        // Reads use the session service; allowed writes go straight to the typed SDK service
        // beneath the gate, because the ordinary grant list is not the test rule.
        var service  = session.Service!;
        var writer   = gated.InnerService;
        var wait     = delay ?? ((ms, token) => Task.Delay(ms, token));
        var total    = System.Diagnostics.Stopwatch.StartNew();
        var results  = new List<RunTestCaseResult>(cases.Count);
        for (var i = 0; i < cases.Count; i++)
        {
            progress?.Report((i, cases.Count, $"Running case {i + 1} of {cases.Count}: {cases[i].Name}"));
            results.Add(await RunCaseAsync(cases[i], service, writer, disclosures[i], wait, ct).ConfigureAwait(false));
        }
        progress?.Report((cases.Count, cases.Count, "All cases finished"));
        total.Stop();

        var restored     = results.SelectMany(r => r.Restored ?? []).ToList();
        var restoreFails = restored.Where(r => !r.Confirmed).Select(r => r.Tag)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var overall = restoreFails.Count > 0 ? "restore_failed"
                    : results.All(r => r.Result == "pass") ? "pass" : "fail";
        AuditLogger.LogTestRun(overall, results.Select(r => (r.Name, r.Result)), total.ElapsedMilliseconds);
        return new RunTestResult(overall, identity.ProductName, results, total.ElapsedMilliseconds,
            restored, restoreFails);
    }

    private static Task<EmulatorIdentityInfo> DefaultIdentityReader(string host, CancellationToken ct) =>
        EmulatorIdentity.ReadAsync(host, ct: ct);

    private static IEnumerable<RunTestSet> SetsOf(RunTestCase c) =>
        (c.Setup ?? []).Concat((c.Steps ?? []).SelectMany(st => st.Set ?? []));

    private static async Task<RunTestCaseResult> RunCaseAsync(
        RunTestCase c, ITagService service, ITagService writer, IReadOnlyList<TestWriteDisclosure> disclosed,
        Func<int, CancellationToken, Task> wait, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Snapshot every distinct tag the case will set, before anything is written.
        var snapshot = new List<(string Tag, TagValue Before)>();
        var seen     = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in SetsOf(c))
        {
            if (!seen.Add(s.Tag)) continue;
            try
            {
                snapshot.Add((s.Tag, await service.ReadAsync(s.Tag, ct).ConfigureAwait(false)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Nothing has been written yet, so there is nothing to restore.
                return new RunTestCaseResult(c.Name, "fail", [], sw.ElapsedMilliseconds,
                    $"snapshot of '{s.Tag}' failed, nothing was written: {ex.Message}", disclosed, []);
            }
        }

        var steps = new List<RunTestStepResult>();
        var pass  = false;
        string? error = null;
        IReadOnlyList<RunTestRestoreEntry> restored = [];
        try
        {
            (pass, error) = await RunBodyAsync(c, service, writer, wait, steps, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            pass  = false;
            error = $"case threw: {ex.Message}";
        }
        finally
        {
            // Always restore, even on failure, exception or cancellation (hence CancellationToken.None).
            restored = await RestoreAsync(snapshot, service, writer).ConfigureAwait(false);
        }

        return new RunTestCaseResult(c.Name, pass ? "pass" : "fail", steps, sw.ElapsedMilliseconds,
            error, disclosed, restored);
    }

    private static async Task<(bool Pass, string? Error)> RunBodyAsync(
        RunTestCase c, ITagService service, ITagService writer,
        Func<int, CancellationToken, Task> wait, List<RunTestStepResult> steps, CancellationToken ct)
    {
        var setupError = await WriteAllAsync(c.Setup, writer, new List<object>(), ct).ConfigureAwait(false);
        if (setupError is not null)
            return (false, setupError);

        var pass = true;
        foreach (var step in c.Steps!)
        {
            var written   = new List<object>();
            var writeErr  = await WriteAllAsync(step.Set, writer, written, ct).ConfigureAwait(false);
            var waitMs    = ClampWaitMs(step.WaitMs);
            if (writeErr is not null)
            {
                steps.Add(new RunTestStepResult(written, waitMs, [], "fail", writeErr));
                pass = false;
                break;
            }

            await wait(waitMs, ct).ConfigureAwait(false);

            var expects = new List<RunTestExpectResult>();
            foreach (var e in step.Expect ?? [])
                expects.Add(await CheckAsync(e, service, ct).ConfigureAwait(false));
            var ok = expects.All(x => x.Verdict == "pass");
            pass &= ok;
            steps.Add(new RunTestStepResult(written, waitMs, expects, ok ? "pass" : "fail"));
        }
        return (pass, null);
    }

    /// <summary>Writes each snapshot back and reads it to confirm; one failure never stops the others.</summary>
    private static async Task<IReadOnlyList<RunTestRestoreEntry>> RestoreAsync(
        List<(string Tag, TagValue Before)> snapshot, ITagService service, ITagService writer)
    {
        var entries = new List<RunTestRestoreEntry>(snapshot.Count);
        foreach (var (tag, before) in snapshot)
        {
            try
            {
                await writer.WriteAsync(tag, before.Value, CancellationToken.None).ConfigureAwait(false);
                AuditLogger.LogGateDecision(tag, "granted", null, "test-run: restore of pre-case value");
                var after = await service.ReadAsync(tag, CancellationToken.None).ConfigureAwait(false);
                var ok    = WriteTagHandler.ValuesMatch(before.Value, after.Value, before.DataType);
                entries.Add(new RunTestRestoreEntry(tag, before.Value, ok,
                    ok ? null : $"read-back {after.Value} does not match snapshot {before.Value}"));
            }
            catch (Exception ex)
            {
                entries.Add(new RunTestRestoreEntry(tag, before.Value, false, $"restore failed: {ex.Message}"));
            }
        }
        return entries;
    }

    private static async Task<string?> WriteAllAsync(
        IReadOnlyList<RunTestSet>? sets, ITagService writer, List<object> written, CancellationToken ct)
    {
        foreach (var s in sets ?? [])
        {
            var value = ToValue(s.Value);
            try
            {
                await writer.WriteAsync(s.Tag, value, ct).ConfigureAwait(false);
                AuditLogger.LogGateDecision(s.Tag, "granted", null, "test-run: emulator test write via typed SDK path");
                written.Add(new { tag = s.Tag, value });
            }
            catch (TagServiceException ex)
            {
                return $"write of '{s.Tag}' failed ({ex.Error.Kind}): {ex.Error.Message}";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return $"write of '{s.Tag}' failed: {ex.Message}";
            }
        }
        return null;
    }

    private static async Task<RunTestExpectResult> CheckAsync(
        RunTestExpect e, ITagService service, CancellationToken ct)
    {
        var expected = Describe(e);
        TagValue read;
        try
        {
            read = await service.ReadAsync(e.Tag, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new RunTestExpectResult(e.Tag, expected, null, "fail", $"read failed: {ex.Message}");
        }

        var observed = read.Value;
        var ok = true;
        if (e.EqualsValue is { } eq)
            ok &= WriteTagHandler.ValuesMatch(ToValue(eq), observed, read.DataType);
        if (e.Min is { } min)
            ok &= TryDouble(observed, out var d) && d >= min;
        if (e.Max is { } max)
            ok &= TryDouble(observed, out var d2) && d2 <= max;
        if (e.EqualsValue is null && e.Min is null && e.Max is null)
            return new RunTestExpectResult(e.Tag, expected, observed, "fail",
                "expectation needs one of equals, min or max");
        return new RunTestExpectResult(e.Tag, expected, observed, ok ? "pass" : "fail");
    }

    private static string Describe(RunTestExpect e)
    {
        var parts = new List<string>();
        if (e.EqualsValue is { } eq) parts.Add($"equals {eq.GetRawText()}");
        if (e.Min is { } min)   parts.Add($"min {min.ToString(CultureInfo.InvariantCulture)}");
        if (e.Max is { } max)   parts.Add($"max {max.ToString(CultureInfo.InvariantCulture)}");
        return string.Join(", ", parts);
    }

    private static bool TryDouble(object? v, out double d)
    {
        try
        {
            if (v is not null and not string and not bool)
            {
                d = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                return true;
            }
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException) { }
        d = 0;
        return false;
    }

    /// <summary>Converts a JSON value to the .NET value passed to <see cref="ITagService.WriteAsync"/>.</summary>
    /// <param name="v">Boolean, number or string JSON value.</param>
    /// <returns>A <see cref="bool"/>, <see cref="int"/>, <see cref="double"/> or <see cref="string"/>.</returns>
    public static object ToValue(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.True   => true,
        JsonValueKind.False  => false,
        JsonValueKind.Number => v.TryGetInt32(out var i) ? (object)i
                             : v.TryGetInt64(out var l) ? l
                             : v.TryGetUInt64(out var ul) ? ul
                             : v.GetDouble(),
        JsonValueKind.String => WriteTagHandler.ParseValue(v.GetString() ?? string.Empty),
        _                    => v.GetRawText(),
    };
}
